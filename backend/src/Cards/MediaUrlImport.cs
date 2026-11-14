using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using MySqlConnector;
using MyApp.Api.Storage;

namespace MyApp.Api.Cards;

public sealed record MediaUrlImportRequest(string? Url, string? Referer, string? PageUrl, string? Title);

/// <summary>
/// Creates a media card from a file URL that nodeck downloads itself, so the browser extension does
/// not have to pass the bytes through the browser. Only public http(s) addresses are fetched; a
/// private or loopback address is allowed only when it is on the same host as the page the user
/// saved from (for example a site on the user's own network).
/// </summary>
public static class MediaUrlImport
{
    private static readonly HttpRequestOptionsKey<string> AllowedHostKey = new("nodeck-allowed-host");
    private static readonly HttpClient Client = CreateClient();

    public static async Task<IResult> ImportAsync(
        HttpRequest httpRequest,
        MediaUrlImportRequest request,
        CardRepository repository,
        PreviewService previewService,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!CardEndpoints.TryReadCreationKey(httpRequest, out var key, out var keyError))
            return Results.BadRequest(new { error = keyError });
        if (!TryHttpUrl(request.Url, out var url))
            return Results.BadRequest(new { error = "url must be an absolute http or https URL of at most 2048 characters." });
        Uri? referer = null;
        if (!string.IsNullOrWhiteSpace(request.Referer) && !TryHttpUrl(request.Referer, out referer))
            return Results.BadRequest(new { error = "referer must be an absolute http or https URL." });
        Uri? page = null;
        if (!string.IsNullOrWhiteSpace(request.PageUrl) && !TryHttpUrl(request.PageUrl, out page))
            return Results.BadRequest(new { error = "pageUrl must be an absolute http or https URL." });
        var title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();

        var maxBytes = UploadLimits.MaxBytes(configuration);
        var tempPath = Path.Combine(Path.GetTempPath(), $"nodeck-import-{Guid.NewGuid():N}");
        try
        {
            string fileName;
            long length;
            using (var message = new HttpRequestMessage(HttpMethod.Get, url))
            {
                message.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
                message.Headers.TryAddWithoutValidation("Accept", "image/*,video/*,*/*;q=0.8");
                if (referer is not null) message.Headers.Referrer = referer;
                if (page is not null) message.Options.Set(AllowedHostKey, page.Host);

                HttpResponseMessage response;
                try
                {
                    response = await Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (HttpRequestException exception) when (FindBlocked(exception) is { } blocked)
                {
                    return Results.BadRequest(new { error = blocked.Message });
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                    && !cancellationToken.IsCancellationRequested)
                {
                    return Results.Json(new { error = $"Could not download the file: {exception.Message}" },
                        statusCode: StatusCodes.Status502BadGateway);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                        return Results.Json(new { error = $"The site answered {(int)response.StatusCode}." },
                            statusCode: StatusCodes.Status502BadGateway);
                    if (response.Content.Headers.ContentLength > maxBytes)
                        return TooLarge(maxBytes);
                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var output = File.Create(tempPath);
                    var buffer = new byte[81920];
                    length = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        length += read;
                        if (length > maxBytes) return TooLarge(maxBytes);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    fileName = FileName(response.RequestMessage?.RequestUri ?? url);
                }
            }
            if (length == 0)
                return Results.Json(new { error = "The site sent an empty file." }, statusCode: StatusCodes.Status502BadGateway);

            await using var stream = File.OpenRead(tempPath);
            var file = new FormFile(stream, 0, length, "file", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/octet-stream",
            };
            var metadata = new JsonObject
            {
                ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["imported_from_url"] = url.AbsoluteUri,
            };
            var creation = key is null ? null : new CardCreationRequest("media", key,
                await CardCreationHash.MediaAsync("media", title, null, [], file, cancellationToken));
            var created = await repository.CreateAsync(
                new CreateCardData("media", title, null, metadata, []),
                async cardId => await previewService.SaveMediaAndCreatePreviewAsync(cardId, file),
                previewService.DeleteCardFiles,
                creation);
            return CardEndpoints.CreationResponse(created);
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (MySqlException exception) when (CardEndpoints.IsRetryableLockError(exception))
        {
            return Results.StatusCode(CardEndpoints.TooEarlyStatusCode);
        }
        finally
        {
            try { File.Delete(tempPath); } catch (IOException) { }
        }
    }

    private static IResult TooLarge(long maxBytes) => Results.Json(
        new { error = $"The file is larger than the upload limit ({maxBytes / 1024 / 1024} MB)." },
        statusCode: StatusCodes.Status413PayloadTooLarge);

    private static bool TryHttpUrl(string? value, out Uri url)
    {
        url = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host)) return false;
        url = parsed;
        return true;
    }

    private static string FileName(Uri url)
    {
        var last = Uri.UnescapeDataString(url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "");
        var name = new string(last.Where(c => !char.IsControl(c) && c is not '\'' and not '"' and not '\\').Take(120).ToArray());
        return string.IsNullOrWhiteSpace(name) ? "imported" : name;
    }

    private static ImportBlockedException? FindBlocked(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
            if (exception is ImportBlockedException blocked) return blocked;
        return null;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // Every connection, also after a redirect, is checked here against the address actually used.
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var sameHostAsPage = context.InitialRequestMessage.Options.TryGetValue(AllowedHostKey, out var allowedHost)
                    && string.Equals(allowedHost, host, StringComparison.OrdinalIgnoreCase);
                var addresses = IPAddress.TryParse(host, out var literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(host, cancellationToken);
                var usable = sameHostAsPage ? addresses : addresses.Where(IsPublic).ToArray();
                if (usable.Length == 0)
                    throw new ImportBlockedException($"nodeck does not download from {host}: it is a private or local address.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(usable, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
    }

    /// <summary>True for addresses on the public internet; false for loopback, private, link-local and similar.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast || address.IsIPv6UniqueLocal) return false;
            var b = address.GetAddressBytes();
            // NAT64 (64:ff9b::/96) carries an IPv4 address in its last four bytes.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
                return IsPublic(new IPAddress(b[12..16]));
            return true;
        }
        return false;
    }

    private sealed class ImportBlockedException(string message) : Exception(message);
}

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyApp.Api.Extension;

/// <summary>
/// Serves the browser extension packages built from <c>extension/</c>. Each download carries a
/// <c>defaults.json</c> with the address it was downloaded from, so the extension finds this server.
/// </summary>
public static class ExtensionEndpoints
{
    private static readonly string[] Browsers = ["chrome", "firefox"];

    public static IEndpointRouteBuilder MapExtensionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/extension");
        group.MapGet("", (HttpRequest request, IConfiguration configuration) =>
        {
            var directory = PackageDirectory(configuration);
            var packages = Browsers.Select(browser =>
            {
                var path = PackagePath(directory, browser);
                var available = File.Exists(path);
                return new
                {
                    browser,
                    available,
                    version = available ? ReadVersion(path) : null,
                    url = $"/api/extension/{browser}",
                };
            }).ToArray();
            return Results.Ok(new { serverUrl = ServerUrl(request), packages });
        });
        group.MapGet("/{browser}", async (string browser, HttpRequest request, IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (!Browsers.Contains(browser))
                return Results.NotFound(new { error = "Unknown browser; use chrome or firefox." });
            var path = PackagePath(PackageDirectory(configuration), browser);
            if (!File.Exists(path))
                return Results.NotFound(new { error = "The extension package is not included in this build." });
            var bytes = await WithDefaultsAsync(path, ServerUrl(request), cancellationToken);
            var version = ReadVersion(path) ?? "dev";
            return Results.File(bytes, "application/zip", $"nodeck-{browser}-{version}.zip");
        });
        return app;
    }

    private static string PackageDirectory(IConfiguration configuration) =>
        configuration["NODECK_EXTENSION_DIR"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "extension");

    private static string PackagePath(string directory, string browser) =>
        Path.Combine(directory, $"nodeck-{browser}.zip");

    private static string ServerUrl(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');

    private static string? ReadVersion(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            using var stream = archive.GetEntry("manifest.json")?.Open();
            if (stream is null) return null;
            return JsonNode.Parse(stream)?["version"]?.GetValue<string>();
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException
            or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<byte[]> WithDefaultsAsync(string path, string serverUrl, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await using (var source = File.OpenRead(path))
            await source.CopyToAsync(buffer, cancellationToken);
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            archive.GetEntry("defaults.json")?.Delete();
            var entry = archive.CreateEntry("defaults.json", CompressionLevel.Optimal);
            await using var writer = entry.Open();
            await JsonSerializer.SerializeAsync(writer, new { serverUrl }, cancellationToken: cancellationToken);
        }
        return buffer.ToArray();
    }
}

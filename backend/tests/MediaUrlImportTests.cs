using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Cards;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class MediaUrlImportTests
{
    [DatabaseFact]
    public async Task ImportsAFileFromTheSameHostAsThePageWithItsReferer()
    {
        await using var site = await FileSite.StartAsync();
        await using var host = await ApiTestHost.CreateAsync();

        using var response = await Import(host, site.Url("/protected.png"), referer: site.Url("/page"), pageUrl: site.Url("/page"),
            title: "From the site");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("media", card.GetProperty("type").GetString());
        Assert.Equal("From the site", card.GetProperty("title").GetString());
        var metadata = card.GetProperty("metadata");
        Assert.Equal(16, metadata.GetProperty("width").GetInt32());
        Assert.Equal("image/png", metadata.GetProperty("mime_type").GetString());
        Assert.Equal(site.Url("/protected.png"), metadata.GetProperty("imported_from_url").GetString());
        Assert.Equal("protected.png", metadata.GetProperty("original_file_name").GetString());
        Assert.Equal(site.Url("/page"), Assert.Single(site.Referers));
    }

    [DatabaseFact]
    public async Task ASiteRefusalIsReportedAsBadGateway()
    {
        await using var site = await FileSite.StartAsync();
        await using var host = await ApiTestHost.CreateAsync();

        using var response = await Import(host, site.Url("/protected.png"), referer: null, pageUrl: site.Url("/page"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("403", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await CountCards(host));
    }

    [DatabaseFact]
    public async Task PrivateAddressesAreNotFetchedForAPageElsewhere()
    {
        await using var site = await FileSite.StartAsync();
        await using var host = await ApiTestHost.CreateAsync();

        using var withoutPage = await Import(host, site.Url("/open.png"), referer: null, pageUrl: null);
        using var otherPage = await Import(host, site.Url("/open.png"), referer: null, pageUrl: "https://example.org/post/1");

        foreach (var response in new[] { withoutPage, otherPage })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("private or local", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, site.Requests);
        Assert.Equal(0, await CountCards(host));
    }

    [DatabaseFact]
    public async Task ARedirectToAnotherPrivateHostIsBlocked()
    {
        await using var site = await FileSite.StartAsync();
        await using var host = await ApiTestHost.CreateAsync();

        // The page is on 127.0.0.1, so that host is allowed; the redirect goes to "localhost", which is not.
        using var response = await Import(host, site.Url("/redirect"), referer: null, pageUrl: site.Url("/page"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("localhost", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, site.Requests);
        Assert.Equal(0, await CountCards(host));
    }

    [DatabaseFact]
    public async Task OnlyHttpAndHttpsUrlsAreAccepted()
    {
        await using var host = await ApiTestHost.CreateAsync();

        foreach (var url in new[] { "file:///etc/passwd", "ftp://example.org/a.png", "/relative.png", "" })
        {
            using var response = await Import(host, url, referer: null, pageUrl: null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var badReferer = await Import(host, "https://example.org/a.png", referer: "javascript:alert(1)", pageUrl: null);
        Assert.Equal(HttpStatusCode.BadRequest, badReferer.StatusCode);
    }

    [DatabaseFact]
    public async Task FilesOverTheUploadLimitAreRefused()
    {
        await using var site = await FileSite.StartAsync();
        await using var host = await ApiTestHost.CreateAsync(new Dictionary<string, string?> { ["NODECK_MAX_UPLOAD_MB"] = "1" });

        using var declared = await Import(host, site.Url("/big"), referer: null, pageUrl: site.Url("/page"));
        using var streamed = await Import(host, site.Url("/big-chunked"), referer: null, pageUrl: site.Url("/page"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
        Assert.Equal(0, await CountCards(host));
    }

    [DatabaseFact]
    public async Task TheSameIdempotencyKeyCreatesOneCard()
    {
        await using var site = await FileSite.StartAsync();
        await using var host = await ApiTestHost.CreateAsync();
        var key = Guid.NewGuid().ToString("D");

        using var first = await Import(host, site.Url("/open.png"), referer: null, pageUrl: site.Url("/page"), key: key);
        using var second = await Import(host, site.Url("/open.png"), referer: null, pageUrl: site.Url("/page"), key: key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64(),
            (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64());
        Assert.Equal(1, await CountCards(host));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.0.104", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    public void OnlyPublicAddressesCountAsPublic(string address, bool expected) =>
        Assert.Equal(expected, MediaUrlImport.IsPublic(IPAddress.Parse(address)));

    private static Task<HttpResponseMessage> Import(ApiTestHost host, string url, string? referer, string? pageUrl,
        string? title = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/cards/import-url")
        {
            Content = JsonContent.Create(new { url, referer, pageUrl, title }),
        };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return host.Client.SendAsync(request);
    }

    private static async Task<long> CountCards(ApiTestHost host)
    {
        var cards = await host.Client.GetFromJsonAsync<JsonElement>("/api/cards");
        return cards.GetArrayLength();
    }

    /// <summary>A small site on 127.0.0.1: a Referer-protected image, an open one, a redirect and large files.</summary>
    private sealed class FileSite : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _requests;
        public string BaseUrl { get; }
        public ConcurrentQueue<string> Referers { get; } = new();
        public int Requests => _requests;

        private FileSite(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public string Url(string path) => BaseUrl + path;

        public static async Task<FileSite> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            FileSite? site = null;
            var png = Png();
            app.Use(async (context, next) => { Interlocked.Increment(ref site!._requests); await next(context); });
            app.MapGet("/protected.png", (HttpRequest request) =>
            {
                var referer = request.Headers.Referer.ToString();
                if (string.IsNullOrEmpty(referer)) return Results.StatusCode(403);
                site!.Referers.Enqueue(referer);
                return Results.File(png, "image/png");
            });
            app.MapGet("/open.png", () => Results.File(png, "image/png"));
            app.MapGet("/redirect", (HttpRequest request) =>
                Results.Redirect($"http://localhost:{request.Host.Port}/open.png"));
            app.MapGet("/big", () => Results.File(new byte[2 * 1024 * 1024], "application/octet-stream"));
            app.MapGet("/big-chunked", async (HttpResponse response) =>
            {
                response.ContentType = "application/octet-stream";
                var chunk = new byte[64 * 1024];
                for (var i = 0; i < 40; i++) await response.Body.WriteAsync(chunk);
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            site = new FileSite(app, address.TrimEnd('/'));
            return site;
        }

        private static byte[] Png()
        {
            using var image = new Image<Rgba32>(16, 12, new Rgba32(200, 40, 40));
            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            return stream.ToArray();
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}

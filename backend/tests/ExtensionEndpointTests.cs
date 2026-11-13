using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Extension;

namespace MyApp.Api.Tests;

public sealed class ExtensionEndpointTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nodeck-extension-tests", Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["NODECK_EXTENSION_DIR"] = _directory });
        _app = builder.Build();
        _app.MapExtensionEndpoints();
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ListsPackagesWithTheirVersionAndThisServerAddress()
    {
        WritePackage("chrome", "1.2.3");

        var body = JsonDocument.Parse(await _client.GetStringAsync("/api/extension")).RootElement;

        Assert.Equal(BaseUrl, body.GetProperty("serverUrl").GetString());
        var packages = body.GetProperty("packages").EnumerateArray().ToDictionary(
            package => package.GetProperty("browser").GetString()!);
        Assert.True(packages["chrome"].GetProperty("available").GetBoolean());
        Assert.Equal("1.2.3", packages["chrome"].GetProperty("version").GetString());
        Assert.Equal("/api/extension/chrome", packages["chrome"].GetProperty("url").GetString());
        Assert.False(packages["firefox"].GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, packages["firefox"].GetProperty("version").ValueKind);
    }

    [Fact]
    public async Task DownloadKeepsThePackageAndWritesTheServerAddress()
    {
        WritePackage("firefox", "0.4.0", staleDefaults: true);

        using var response = await _client.GetAsync("/api/extension/firefox");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nodeck-firefox-0.4.0.zip", response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName);
        using var archive = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(["defaults.json", "manifest.json", "src/background.js"],
            archive.Entries.Select(entry => entry.FullName).Order().ToArray());
        Assert.Equal("console.log('hi');", Read(archive, "src/background.js"));
        var defaults = JsonDocument.Parse(Read(archive, "defaults.json")).RootElement;
        Assert.Equal(BaseUrl, defaults.GetProperty("serverUrl").GetString());
    }

    [Fact]
    public async Task UnknownBrowserAndMissingPackageAreNotFound()
    {
        WritePackage("chrome", "1.0.0");

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/extension/opera")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/extension/firefox")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/extension/chrome")).StatusCode);
    }

    private string BaseUrl => _client.BaseAddress!.ToString().TrimEnd('/');

    private void WritePackage(string browser, string version, bool staleDefaults = false)
    {
        using var archive = ZipFile.Open(Path.Combine(_directory, $"nodeck-{browser}.zip"), ZipArchiveMode.Create);
        Write(archive, "manifest.json", $$"""{ "manifest_version": 3, "version": "{{version}}" }""");
        Write(archive, "src/background.js", "console.log('hi');");
        if (staleDefaults) Write(archive, "defaults.json", """{ "serverUrl": "http://old.example" }""");
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static string Read(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}

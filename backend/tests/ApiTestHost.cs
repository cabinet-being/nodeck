using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Analysis;
using MyApp.Api.Cards;
using MyApp.Api.Storage;
using MySqlConnector;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace MyApp.Api.Tests;

/// <summary>Isolated database plus a Kestrel host mapping only the card endpoints (no hosted services).</summary>
internal sealed class ApiTestHost : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private readonly WebApplication _app;
    public string AppDataRoot { get; }
    public MySqlConnection Connection { get; }
    public HttpClient Client { get; }
    public string ConnectionString { get; }

    private ApiTestHost(string adminConnectionString, string databaseName, string connectionString,
        string appDataRoot, WebApplication app, MySqlConnection connection, HttpClient client)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        ConnectionString = connectionString;
        AppDataRoot = appDataRoot;
        _app = app;
        Connection = connection;
        Client = client;
    }

    public static async Task<ApiTestHost> CreateAsync(IDictionary<string, string?>? settings = null)
    {
        var builder = new MySqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!);
        if (!builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("API tests require a dedicated test database.");
        var databaseName = $"nodeck_api_{Guid.NewGuid():N}_test";
        var appDataRoot = Path.Combine(Path.GetTempPath(), "nodeck-api-tests", Guid.NewGuid().ToString("N"));
        builder.Database = "";
        var adminConnectionString = builder.ConnectionString;
        await using (var admin = new MySqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"CREATE DATABASE `{databaseName}`;");
        }

        builder.Database = databaseName;
        var connection = new MySqlConnection(builder.ConnectionString);
        WebApplication? app = null;
        try
        {
            await connection.OpenAsync();
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "database/init/001_cards.sql")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            foreach (var file in new[] { "001_cards.sql", "002_catalog.sql" })
            {
                var sql = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "database/init", file));
                foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    await connection.ExecuteAsync(statement);
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = builder.ConnectionString }).Build();
            var initializer = new CardDatabaseInitializer(configuration);
            await initializer.InitializeAsync();
            await initializer.InitializeCreationRequestsAsync();
            await initializer.InitializeContentHashAsync();

            var host = WebApplication.CreateBuilder();
            host.WebHost.UseUrls("http://127.0.0.1:0");
            host.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Default"] = builder.ConnectionString, ["NODECK_APP_DATA"] = appDataRoot });
            if (settings is not null) host.Configuration.AddInMemoryCollection(settings);
            host.AddUploadLimits();
            host.Services.AddScoped<CardRepository>();
            host.Services.AddSingleton<AnalysisQueueRepository>();
            host.Services.AddSingleton<SpeechAnalysisQueueRepository>();
            host.Services.AddSingleton<PreviewService>();
            app = host.Build();
            // Surface unhandled exceptions in the response body so test failures are diagnosable.
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                // Bad requests (such as an oversized body) keep Kestrel's own status code, as in production.
                catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested &&
                    exception is not BadHttpRequestException)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsync(exception.ToString());
                }
            });
            app.MapCardEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(5) };
            return new ApiTestHost(adminConnectionString, databaseName, builder.ConnectionString,
                appDataRoot, app, connection, client);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            await connection.DisposeAsync();
            await using var admin = new MySqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE `{databaseName}`;");
            if (Directory.Exists(appDataRoot)) Directory.Delete(appDataRoot, recursive: true);
            throw;
        }
    }

    public static byte[] Png(int seed)
    {
        using var image = new Image<Rgba32>(8, 8, new Rgba32((byte)(seed % 256), (byte)(seed / 256 % 256), 77, 255));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    public static byte[] Gif(int seed)
    {
        using var image = new Image<Rgba32>(8, 8, new Rgba32((byte)(seed % 256), (byte)(seed / 256 % 256), 200, 255));
        using var stream = new MemoryStream();
        image.SaveAsGif(stream);
        return stream.ToArray();
    }

    public async Task<HttpResponseMessage> PostMediaAsync(
        byte[] bytes, string fileName, string title, string? key = null, CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("media"), "type");
        content.Add(new StringContent(title), "title");
        content.Add(new ByteArrayContent(bytes), "image", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cards") { Content = content };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await Client.SendAsync(request, cancellationToken);
    }

    public async Task<HttpResponseMessage> PutMediaAsync(
        long id, byte[]? bytes, string? fileName, string title)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(title), "title");
        if (bytes is not null) content.Add(new ByteArrayContent(bytes), "image", fileName!);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/cards/{id}") { Content = content };
        return await Client.SendAsync(request);
    }

    public async Task<long> CreateImageCardAsync(int seed, string? title = null)
    {
        using var response = await PostMediaAsync(Png(seed), $"seed{seed}.png", title ?? $"card {seed}");
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await IdAsync(response);
    }

    public async Task<long> CreateCollectionAsync(string type, params long[] children)
    {
        using var response = await Client.PostAsJsonAsync("/api/cards/collections",
            new { type, title = type + " test", childCardIds = children });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return await IdAsync(response);
    }

    public async Task<HttpResponseMessage> MoveAsync(long sourceId, long childId, long targetParentId, int? position) =>
        await Client.PostAsJsonAsync($"/api/cards/{sourceId}/children/{childId}/move",
            new { targetParentId, position });

    public static async Task<long> IdAsync(HttpResponseMessage response)
    {
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(body);
        return body.RootElement.GetProperty("id").GetInt64();
    }

    public Task<string?> HashAsync(long id) => Connection.ExecuteScalarAsync<string?>(
        "SELECT content_sha256 FROM cards WHERE id = @Id;", new { Id = id });

    public Task<int> CountCardsAsync() => Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cards;");

    public async Task<long[]> ChildIdsAsync(long parentId) => (await Connection.QueryAsync<long>("""
        SELECT to_card_id FROM card_relations
        WHERE from_card_id = @ParentId AND relation_type = 'contains'
        ORDER BY CAST(JSON_UNQUOTE(JSON_EXTRACT(properties, '$.position')) AS SIGNED);
        """, new { ParentId = parentId })).ToArray();

    public async Task<long[]> ChildPositionsAsync(long parentId) => (await Connection.QueryAsync<long>("""
        SELECT CAST(JSON_UNQUOTE(JSON_EXTRACT(properties, '$.position')) AS SIGNED) FROM card_relations
        WHERE from_card_id = @ParentId AND relation_type = 'contains'
        ORDER BY 1;
        """, new { ParentId = parentId })).ToArray();

    public async Task<(string? Preview, int? ContainedCount)> ParentStateAsync(long id)
    {
        var row = await Connection.QuerySingleAsync<(string? Preview, int? Count)>("""
            SELECT preview, CAST(JSON_EXTRACT(metadata, '$.contained_count') AS SIGNED) FROM cards WHERE id = @Id;
            """, new { Id = id });
        return (row.Preview, row.Count);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        await Connection.DisposeAsync();
        await using var admin = new MySqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE `{_databaseName}`;");
        if (Directory.Exists(AppDataRoot)) Directory.Delete(AppDataRoot, recursive: true);
    }
}

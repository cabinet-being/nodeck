using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Analysis;
using MyApp.Api.Cards;
using MyApp.Api.Storage;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class CardCreationIdempotencyTests
{
    [Fact]
    public async Task MediaHashDistinguishesDifferentBytesWithSameNameAndLength()
    {
        using var firstStream = new MemoryStream([1, 2, 3]);
        using var secondStream = new MemoryStream([1, 2, 4]);
        var first = new FormFile(firstStream, 0, firstStream.Length, "image", "same.png");
        var second = new FormFile(secondStream, 0, secondStream.Length, "image", "same.png");

        var firstHash = await CardCreationHash.MediaAsync("media", "same", null, [], first, CancellationToken.None);
        var secondHash = await CardCreationHash.MediaAsync("media", "same", null, [], second, CancellationToken.None);

        Assert.NotEqual(Convert.ToHexString(firstHash), Convert.ToHexString(secondHash));
    }

    [DatabaseFact]
    public async Task MediaReplayReturnsSameCardAndRequestLookupFindsIt()
    {
        await using var scope = await CreationScope.CreateAsync();
        var key = Guid.NewGuid().ToString("D");

        using var first = await scope.PostMediaAsync(key, "first");
        using var replay = await scope.PostMediaAsync(key, "first");
        using var lookup = await scope.Client.GetAsync($"/api/cards/creation-requests/media/{key}");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        var id = await CardIdAsync(first);
        Assert.Equal(id, await CardIdAsync(replay));
        Assert.Equal(id, await CardIdAsync(lookup));
        Assert.Equal(1, await scope.CountCardsAsync());
        Assert.Equal(1, await scope.CountRequestsAsync("media", key));
    }

    [DatabaseFact]
    public async Task MediaKeyCannotBeReusedForChangedPayload()
    {
        await using var scope = await CreationScope.CreateAsync();
        var key = Guid.NewGuid().ToString("D");

        using var first = await scope.PostMediaAsync(key, "first");
        using var changed = await scope.PostMediaAsync(key, "changed");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(1, await scope.CountCardsAsync());
        Assert.Equal(1, await scope.CountRequestsAsync("media", key));
    }

    [DatabaseFact]
    public async Task CollectionReplayKeepsOrderAndChangedOrderConflicts()
    {
        await using var scope = await CreationScope.CreateAsync();
        var firstChild = await scope.InsertMediaAsync("first child");
        var secondChild = await scope.InsertMediaAsync("second child");
        var key = Guid.NewGuid().ToString("D");

        using var first = await scope.PostCollectionAsync(key, [firstChild, secondChild]);
        using var replay = await scope.PostCollectionAsync(key, [firstChild, secondChild]);
        using var reversed = await scope.PostCollectionAsync(key, [secondChild, firstChild]);
        using var lookup = await scope.Client.GetAsync($"/api/cards/creation-requests/collection/{key}");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, reversed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        var id = await CardIdAsync(first);
        Assert.Equal(id, await CardIdAsync(replay));
        Assert.Equal(id, await CardIdAsync(lookup));
        Assert.Equal(new[] { firstChild, secondChild }, await scope.ChildIdsAsync(id));
        Assert.Equal(3, await scope.CountCardsAsync());
        Assert.Equal(1, await scope.CountRequestsAsync("collection", key));
    }

    [DatabaseFact]
    public async Task DeletedCardLeavesCreationTombstone()
    {
        await using var scope = await CreationScope.CreateAsync();
        var key = Guid.NewGuid().ToString("D");
        using var first = await scope.PostMediaAsync(key, "deleted");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id = await CardIdAsync(first);
        await scope.DeleteCardAsync(id);

        using var replay = await scope.PostMediaAsync(key, "deleted");
        using var lookup = await scope.Client.GetAsync($"/api/cards/creation-requests/media/{key}");

        Assert.Equal(HttpStatusCode.Gone, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, lookup.StatusCode);
        Assert.Equal(0, await scope.CountCardsAsync());
        Assert.Equal(1, await scope.CountRequestsAsync("media", key));
    }

    [DatabaseFact]
    public async Task ConcurrentMediaRequestsWithSameKeyCreateOneCard()
    {
        await using var scope = await CreationScope.CreateAsync();
        var key = Guid.NewGuid().ToString("D");

        var responses = await Task.WhenAll(
            scope.PostMediaAsync(key, "simultaneous"),
            scope.PostMediaAsync(key, "simultaneous"));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Created },
                responses.Select(response => response.StatusCode).Order().ToArray());
            Assert.Equal(await CardIdAsync(responses[0]), await CardIdAsync(responses[1]));
            Assert.Equal(1, await scope.CountCardsAsync());
            Assert.Equal(1, await scope.CountRequestsAsync("media", key));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    private static async Task<long> CardIdAsync(HttpResponseMessage response)
    {
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(body);
        return body.RootElement.GetProperty("id").GetInt64();
    }

    private sealed class CreationScope : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;
        private readonly string _appDataRoot;
        private readonly WebApplication _app;
        private readonly MySqlConnection _connection;
        public HttpClient Client { get; }

        private CreationScope(string adminConnectionString, string databaseName, string appDataRoot,
            WebApplication app, MySqlConnection connection, HttpClient client)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            _appDataRoot = appDataRoot;
            _app = app;
            _connection = connection;
            Client = client;
        }

        public static async Task<CreationScope> CreateAsync()
        {
            var builder = new MySqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!);
            if (!builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Card creation tests require a dedicated test database.");
            var databaseName = $"nodeck_creation_{Guid.NewGuid():N}_test";
            var appDataRoot = Path.Combine(Path.GetTempPath(), "nodeck-creation-tests", Guid.NewGuid().ToString("N"));
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
                var sql = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "database/init/001_cards.sql"));
                foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    await connection.ExecuteAsync(statement);
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:Default"] = builder.ConnectionString }).Build();
                await new CardDatabaseInitializer(configuration).InitializeAsync();

                var host = WebApplication.CreateBuilder();
                host.WebHost.UseUrls("http://127.0.0.1:0");
                host.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["ConnectionStrings:Default"] = builder.ConnectionString, ["NODECK_APP_DATA"] = appDataRoot });
                host.Services.AddScoped<CardRepository>();
                host.Services.AddSingleton<AnalysisQueueRepository>();
                host.Services.AddSingleton<SpeechAnalysisQueueRepository>();
                host.Services.AddSingleton<PreviewService>();
                app = host.Build();
                app.MapCardEndpoints();
                await app.StartAsync();
                var address = app.Services.GetRequiredService<IServer>().Features
                    .Get<IServerAddressesFeature>()!.Addresses.Single();
                var client = new HttpClient { BaseAddress = new Uri(address) };
                return new CreationScope(adminConnectionString, databaseName, appDataRoot, app, connection, client);
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

        public async Task<HttpResponseMessage> PostMediaAsync(string key, string title)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent("media"), "type");
            content.Add(new StringContent(title), "title");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cards") { Content = content };
            request.Headers.Add("Idempotency-Key", key);
            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> PostCollectionAsync(string key, long[] children)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cards/collections")
            {
                Content = JsonContent.Create(new { type = "comic", title = "ordered pages", childCardIds = children }),
            };
            request.Headers.Add("Idempotency-Key", key);
            return await Client.SendAsync(request);
        }

        public Task<long> InsertMediaAsync(string title) => _connection.ExecuteScalarAsync<long>("""
            INSERT INTO cards (type, title, metadata) VALUES ('media', @Title, JSON_OBJECT());
            SELECT LAST_INSERT_ID();
            """, new { Title = title });

        public Task<int> CountCardsAsync() => _connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cards;");

        public Task<int> CountRequestsAsync(string scope, string key) => _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_creation_requests WHERE scope = @Scope AND request_key = @Key;",
            new { Scope = scope, Key = key });

        public async Task<long[]> ChildIdsAsync(long parentId) => (await _connection.QueryAsync<long>("""
            SELECT to_card_id FROM card_relations
            WHERE from_card_id = @ParentId AND relation_type = 'contains'
            ORDER BY CAST(JSON_UNQUOTE(JSON_EXTRACT(properties, '$.position')) AS UNSIGNED);
            """, new { ParentId = parentId })).ToArray();

        public Task DeleteCardAsync(long id) => _connection.ExecuteAsync(
            "DELETE FROM cards WHERE id = @Id;", new { Id = id });

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
            await using var admin = new MySqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE `{_databaseName}`;");
            if (Directory.Exists(_appDataRoot)) Directory.Delete(_appDataRoot, recursive: true);
        }
    }
}

using System.Net;
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
using Microsoft.Extensions.Logging.Abstractions;
using MyApp.Api.Analysis;
using MyApp.Api.Cards;
using MyApp.Api.Catalog;
using MyApp.Api.Decks;
using MyApp.Api.Storage;
using MySqlConnector;

namespace MyApp.Api.Tests;

/// <summary>
/// Isolated database plus a Kestrel host mapping card and deck endpoints
/// (ApiTestHost maps only card endpoints and does not create deck tables).
/// </summary>
internal sealed class DeckApiHost : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private readonly WebApplication _app;
    public MySqlConnection Connection { get; }
    public HttpClient Client { get; }
    public IConfiguration Configuration { get; }

    private DeckApiHost(string admin, string name, WebApplication app, MySqlConnection connection,
        HttpClient client, IConfiguration configuration)
    {
        _adminConnectionString = admin;
        _databaseName = name;
        _app = app;
        Connection = connection;
        Client = client;
        Configuration = configuration;
    }

    public static async Task<DeckApiHost> CreateAsync()
    {
        var builder = new MySqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!);
        if (!builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Deck API tests require a dedicated test database.");
        var databaseName = $"nodeck_deckapi_{Guid.NewGuid():N}_test";
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
            await new DeckDatabaseInitializer(configuration).InitializeAsync();
            await new CatalogDatabaseInitializer(configuration, NullLogger<CatalogDatabaseInitializer>.Instance)
                .InitializeAsync();

            var host = WebApplication.CreateBuilder();
            host.WebHost.UseUrls("http://127.0.0.1:0");
            host.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Default"] = builder.ConnectionString, ["NODECK_APP_DATA"] = appDataRoot });
            host.Services.AddScoped<CardRepository>();
            host.Services.AddScoped<DeckRepository>();
            host.Services.AddSingleton<AnalysisQueueRepository>();
            host.Services.AddSingleton<SpeechAnalysisQueueRepository>();
            host.Services.AddSingleton<PreviewService>();
            app = host.Build();
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsync(exception.ToString());
                }
            });
            app.MapCardEndpoints();
            app.MapDeckEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(2) };
            return new DeckApiHost(adminConnectionString, databaseName, app, connection, client, configuration);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            await connection.DisposeAsync();
            await using var admin = new MySqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE `{databaseName}`;");
            throw;
        }
    }

    public Task<long> InsertCardAsync(string type, string title) => Connection.ExecuteScalarAsync<long>("""
        INSERT INTO cards (type, title, metadata) VALUES (@Type, @Title, JSON_OBJECT('created_at', '2026-01-01T00:00:00Z'));
        SELECT LAST_INSERT_ID();
        """, new { Type = type, Title = title });

    public Task AddChildAsync(long parentId, long childId, int position) => Connection.ExecuteAsync("""
        INSERT INTO card_relations (from_card_id, to_card_id, relation_type, properties, metadata)
        VALUES (@ParentId, @ChildId, 'contains', JSON_OBJECT('position', @Position), JSON_OBJECT());
        """, new { ParentId = parentId, ChildId = childId, Position = position });

    public async Task<long> CreateDeckAsync(string title, params long[] cardIds)
    {
        using var response = await Client.PostAsJsonAsync("/api/decks",
            new { title, cards = cardIds.Select(id => new { cardId = id }).ToArray() });
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await ApiTestHost.IdAsync(response);
    }

    public async Task<(HttpStatusCode Status, JsonElement[] Items)> ListAsync(string query)
    {
        using var response = await Client.GetAsync("/api/cards?" + query);
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, []);
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return (response.StatusCode, body!.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray());
    }

    public static long[] Ids(IEnumerable<JsonElement> items) =>
        items.Select(item => item.GetProperty("id").GetInt64()).ToArray();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        await Connection.DisposeAsync();
        await using var admin = new MySqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE `{_databaseName}`;");
    }
}

[Collection("Collection database")]
public sealed class CardListScopeApiTests
{
    [DatabaseFact]
    public async Task DeckScopeFollowsPositionAndPagesHaveNoGapsOrOverlaps()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var cards = new List<long>();
        for (var i = 0; i < 5; i++) cards.Add(await host.InsertCardAsync("media", $"scope-order-{i}"));
        var outside = await host.InsertCardAsync("media", "scope-outside");
        // Deliberately not creation order: positions follow the given order.
        var ordered = new[] { cards[3], cards[0], cards[4], cards[1], cards[2] };
        var deckId = await host.CreateDeckAsync("order deck", ordered);

        var (status, all) = await host.ListAsync($"deck_id={deckId}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(ordered, DeckApiHost.Ids(all));
        Assert.DoesNotContain(outside, DeckApiHost.Ids(all));
        Assert.Equal(ordered, DeckApiHost.Ids((await host.ListAsync($"deck_id={deckId}&sort=position")).Items));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 },
            all.Select(item => item.GetProperty("deckPosition").GetInt32()).ToArray());

        var paged = new List<long>();
        for (var offset = 0; offset < 5; offset += 2)
        {
            var (pageStatus, page) = await host.ListAsync($"deck_id={deckId}&limit=2&offset={offset}");
            Assert.Equal(HttpStatusCode.OK, pageStatus);
            Assert.True(page.Length <= 2);
            paged.AddRange(DeckApiHost.Ids(page));
        }
        Assert.Equal(ordered, paged.ToArray());
        Assert.Empty((await host.ListAsync($"deck_id={deckId}&limit=2&offset=5")).Items);
    }

    [DatabaseFact]
    public async Task SearchFiltersOnlyWithinDeck()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var inDeckMatch = await host.InsertCardAsync("media", $"match-{token}-in");
        var inDeckOther = await host.InsertCardAsync("media", "unrelated-in-deck");
        var outsideMatch = await host.InsertCardAsync("media", $"match-{token}-out");
        var deckId = await host.CreateDeckAsync("search deck", inDeckOther, inDeckMatch);

        var (status, items) = await host.ListAsync($"deck_id={deckId}&search={token}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(new[] { inDeckMatch }, DeckApiHost.Ids(items));
        Assert.Contains(outsideMatch, DeckApiHost.Ids((await host.ListAsync($"search={token}")).Items));
    }

    [DatabaseFact]
    public async Task CatalogTagAndSourceFiltersApplyOnlyWithinDeck()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var catalog = new CatalogRepository(host.Configuration);
        var assignments = new CardCatalogRepository(host.Configuration);
        var tagId = (await catalog.CreateTagAsync(new TagRequest($"scope-tag-{token}", null))).Id;
        var sourceId = (await catalog.CreateSourceAsync(new SourceRequest($"scope-source-{token}"))).Id;
        var inDeckTagged = await host.InsertCardAsync("media", "tagged-in");
        var inDeckPlain = await host.InsertCardAsync("media", "plain-in");
        var outsideTagged = await host.InsertCardAsync("media", "tagged-out");
        foreach (var id in new[] { inDeckTagged, outsideTagged })
        {
            await assignments.SetTagAsync(id, tagId);
            await assignments.SetSourceAsync(id, sourceId, new CardSourceRequest($"https://example.test/{token}/{id}"));
        }
        var deckId = await host.CreateDeckAsync("catalog deck", inDeckPlain, inDeckTagged);

        var byTag = await host.ListAsync($"deck_id={deckId}&catalog_tags={tagId}");
        var bySource = await host.ListAsync($"deck_id={deckId}&catalog_source={sourceId}");

        Assert.Equal(HttpStatusCode.OK, byTag.Status);
        Assert.Equal(new[] { inDeckTagged }, DeckApiHost.Ids(byTag.Items));
        Assert.Equal(HttpStatusCode.OK, bySource.Status);
        Assert.Equal(new[] { inDeckTagged }, DeckApiHost.Ids(bySource.Items));
        Assert.Contains(outsideTagged, DeckApiHost.Ids((await host.ListAsync($"catalog_tags={tagId}")).Items));
    }

    [DatabaseFact]
    public async Task ChildMemberOfDeckIsReturnedUnlessContainedMediaIsExcluded()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var child = await host.InsertCardAsync("media", "scope-child");
        var sibling = await host.InsertCardAsync("media", "scope-sibling");
        var comic = await host.InsertCardAsync("comic", "scope-comic");
        await host.AddChildAsync(comic, child, 0);
        await host.AddChildAsync(comic, sibling, 1);
        var deckId = await host.CreateDeckAsync("child deck", comic, child);

        var (status, items) = await host.ListAsync($"deck_id={deckId}");
        var excluded = await host.ListAsync($"deck_id={deckId}&exclude_contained_media=true");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(new[] { comic, child }, DeckApiHost.Ids(items));
        Assert.DoesNotContain(sibling, DeckApiHost.Ids(items));
        Assert.Equal(new[] { comic }, DeckApiHost.Ids(excluded.Items));
    }

    [DatabaseFact]
    public async Task InvalidDeckScopeIsRejected()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var deckId = await host.CreateDeckAsync("empty");

        Assert.Equal(HttpStatusCode.NotFound, (await host.ListAsync($"deck_id={deckId + 9999}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.ListAsync("deck_id=0")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.ListAsync("deck_id=-3")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.ListAsync("sort=position")).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.ListAsync($"deck_id={deckId}&sort=position")).Status);
    }

    [DatabaseFact]
    public async Task DeckFieldsAreNullWithoutDeckScopeAndFilledWithMembershipProperties()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var title = $"scope-null-{Guid.NewGuid():N}";
        var card = await host.InsertCardAsync("media", title);
        using var created = await host.Client.PostAsJsonAsync("/api/decks",
            new { title = "props deck", cards = new[] { new { cardId = card, properties = new { caption = "hello" } } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var deckId = await ApiTestHost.IdAsync(created);

        var (_, unscoped) = await host.ListAsync($"search={title}");
        var item = Assert.Single(unscoped);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("deckPosition").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("membershipProperties").ValueKind);

        var (_, scoped) = await host.ListAsync($"deck_id={deckId}");
        var scopedItem = Assert.Single(scoped);
        Assert.Equal(0, scopedItem.GetProperty("deckPosition").GetInt32());
        Assert.Equal("hello", scopedItem.GetProperty("membershipProperties").GetProperty("caption").GetString());
    }

    [DatabaseFact]
    public async Task IncludeCardsFalseReturnsEmptyCardsAndDefaultStillIncludesThem()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var card = await host.InsertCardAsync("media", "deck-detail-card");
        var deckId = await host.CreateDeckAsync("detail deck", card);

        using var lean = await host.Client.GetAsync($"/api/decks/{deckId}?include_cards=false");
        Assert.Equal(HttpStatusCode.OK, lean.StatusCode);
        using var leanBody = await lean.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(deckId, leanBody!.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(0, leanBody.RootElement.GetProperty("cards").GetArrayLength());
        Assert.Equal(1, leanBody.RootElement.GetProperty("cardCount").GetInt64());

        using var full = await host.Client.GetAsync($"/api/decks/{deckId}");
        using var fullBody = await full.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(new[] { card },
            fullBody!.RootElement.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("id").GetInt64()));
        Assert.Equal(1, fullBody.RootElement.GetProperty("cardCount").GetInt64());
    }
    [DatabaseFact]
    public async Task CollapsedDeckHidesOnlyPagesWhoseCollectionIsAlsoInTheDeck()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var page = await host.InsertCardAsync("media", "collapse-page");
        var comic = await host.InsertCardAsync("comic", "collapse-comic");
        var lonePage = await host.InsertCardAsync("media", "collapse-lone-page");
        var otherSet = await host.InsertCardAsync("set", "collapse-other-set");
        var plain = await host.InsertCardAsync("media", "collapse-plain");
        await host.AddChildAsync(comic, page, 0);
        await host.AddChildAsync(otherSet, lonePage, 0);
        var deckId = await host.CreateDeckAsync("collapse deck", comic, page, lonePage, plain);

        var all = await host.ListAsync($"deck_id={deckId}");
        var collapsed = await host.ListAsync($"deck_id={deckId}&collapse_pages=true");
        using var totalResponse = await host.Client.GetAsync(
            $"/api/cards?deck_id={deckId}&collapse_pages=true&include_total=true&limit=1");

        Assert.Equal(new[] { comic, page, lonePage, plain }, DeckApiHost.Ids(all.Items));
        Assert.Equal(new[] { comic, lonePage, plain }, DeckApiHost.Ids(collapsed.Items));
        Assert.Equal("3", totalResponse.Headers.GetValues("X-Total-Count").Single());
        var parents = all.Items.ToDictionary(item => item.GetProperty("id").GetInt64(),
            item => item.GetProperty("parentCardId"));
        Assert.Equal(comic, parents[page].GetInt64());
        Assert.Equal(otherSet, parents[lonePage].GetInt64());
        Assert.Equal(JsonValueKind.Null, parents[plain].ValueKind);
        Assert.Equal(JsonValueKind.Null, parents[comic].ValueKind);
    }

    [DatabaseFact]
    public async Task CollapsedLibraryKeepsPagesWhenTheirCollectionTypeIsFilteredOut()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N")[..8];
        var page = await host.InsertCardAsync("media", $"{token}-page");
        var comic = await host.InsertCardAsync("comic", $"{token}-comic");
        var plain = await host.InsertCardAsync("media", $"{token}-plain");
        await host.AddChildAsync(comic, page, 0);

        var mixed = await host.ListAsync($"types=media,comic&search={token}&collapse_pages=true&sort=id&order=asc");
        var mediaOnly = await host.ListAsync($"types=media&search={token}&collapse_pages=true&sort=id&order=asc");

        Assert.Equal(new[] { comic, plain }, DeckApiHost.Ids(mixed.Items));
        Assert.Equal(new[] { page, plain }, DeckApiHost.Ids(mediaOnly.Items));
    }

    [DatabaseFact]
    public async Task SearchFindsACollectionByThePageTitle()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N")[..8];
        var page = await host.InsertCardAsync("media", $"page {token}");
        var comic = await host.InsertCardAsync("comic", "named differently");
        await host.AddChildAsync(comic, page, 0);

        var collections = await host.ListAsync($"search={token}&exclude_contained_media=true");

        Assert.Equal(new[] { comic }, DeckApiHost.Ids(collections.Items));
    }
}

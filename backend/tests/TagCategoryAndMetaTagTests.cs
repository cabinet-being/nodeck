using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyApp.Api.Cards;
using MyApp.Api.Catalog;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class TagCategoryAndMetaTagTests
{
    private const string CategoryError = "category must be general, artist, character, copyright, meta or custom.";

    [Theory]
    [InlineData("image", 800, 600, false, null, new[] { "image", "horizontal" })]
    [InlineData("image", 600, 800, false, null, new[] { "image", "vertical" })]
    [InlineData("image", 1000, 1040, false, null, new[] { "image", "square_image" })]
    [InlineData("image", 1600, 900, false, null, new[] { "image", "horizontal", "highres" })]
    [InlineData("image", 3200, 1000, false, null, new[] { "image", "horizontal", "absurdres", "highres" })]
    [InlineData("image", 400, 300, false, null, new[] { "image", "horizontal", "lowres" })]
    [InlineData("image", 500, 500, false, true, new[] { "image", "animated", "square_image" })]
    [InlineData("gif", 640, 480, false, null, new[] { "gif", "animated", "horizontal" })]
    [InlineData("video", 1920, 1080, true, null, new[] { "video", "animated", "sound", "horizontal", "highres" })]
    [InlineData("video", 300, 480, false, null, new[] { "video", "animated", "silent", "vertical", "lowres" })]
    public void ComputeDerivesMetaTagsFromFileProperties(
        string mediaType, int width, int height, bool hasAudio, bool? animated, string[] expected)
    {
        var metadata = new JsonObject
        {
            ["media_type"] = mediaType, ["width"] = width, ["height"] = height, ["has_audio"] = hasAudio,
        };
        if (animated is not null) metadata["animated"] = animated;
        Assert.Equal(expected.OrderBy(n => n), SystemMetaTags.Compute("media", metadata).OrderBy(n => n));
    }

    [Fact]
    public void ComputeGivesCollectionsOnlyTheirTypeAndOtherTypesNothing()
    {
        Assert.Equal(new[] { "comic_book" }, SystemMetaTags.Compute("comic", new JsonObject()));
        Assert.Equal(new[] { "set" }, SystemMetaTags.Compute("set", null));
        Assert.Empty(SystemMetaTags.Compute("tag", new JsonObject { ["media_type"] = "image" }));
        Assert.Empty(SystemMetaTags.Compute("source", null));
        Assert.Empty(SystemMetaTags.Compute("media", new JsonObject()));
    }

    [DatabaseFact]
    public async Task UserTagCategoriesAreValidatedAndEditable()
    {
        await using var host = await DeckApiHost.CreateAsync();
        await using var api = await CatalogHost.StartAsync(host.Configuration);

        using (var created = await api.Client.PostAsJsonAsync("/api/tags", new { name = "plain" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var body = await created.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal("custom", body!.RootElement.GetProperty("category").GetString());
        }
        using var artist = await api.Client.PostAsJsonAsync("/api/tags", new { name = "my-artist", category = "artist" });
        Assert.Equal(HttpStatusCode.Created, artist.StatusCode);
        using var artistBody = await artist.Content.ReadFromJsonAsync<JsonDocument>();
        var artistId = artistBody!.RootElement.GetProperty("id").GetInt64();
        Assert.Equal("artist", artistBody.RootElement.GetProperty("category").GetString());
        Assert.Equal("user", artistBody.RootElement.GetProperty("origin").GetString());

        using var bad = await api.Client.PostAsJsonAsync("/api/tags", new { name = "bad", category = "Artist" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(CategoryError, await ErrorAsync(bad));

        // Category omitted keeps it; rename + recategorise works for user tags.
        using var keep = await api.Client.PutAsJsonAsync($"/api/tags/{artistId}", new { name = "my-artist-2" });
        Assert.Equal("artist", (await Json(keep)).GetProperty("category").GetString());
        using var moved = await api.Client.PutAsJsonAsync($"/api/tags/{artistId}",
            new { name = "my-artist-2", aliases = new[] { "alias-x" }, category = "character" });
        Assert.Equal("character", (await Json(moved)).GetProperty("category").GetString());
        using var invalid = await api.Client.PutAsJsonAsync($"/api/tags/{artistId}", new { name = "x", category = "zzz" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // Unused user tags of any category can be deleted.
        using var deleted = await api.Client.DeleteAsync($"/api/tags/{artistId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [DatabaseFact]
    public async Task ImportedTagsOnlyAcceptCategoryChanges()
    {
        await using var host = await DeckApiHost.CreateAsync();
        await using var api = await CatalogHost.StartAsync(host.Configuration);
        var imported = await host.Connection.ExecuteScalarAsync<long>("""
            INSERT INTO tags (name, canonical_key, category, origin) VALUES ('imp', 'danbooru:imp', 'general', 'danbooru');
            INSERT INTO tag_aliases (tag_id, alias, origin) VALUES (LAST_INSERT_ID(), 'imp-alias', 'danbooru');
            SELECT id FROM tags WHERE canonical_key = 'danbooru:imp';
            """);

        using var rename = await api.Client.PutAsJsonAsync($"/api/tags/{imported}",
            new { name = "renamed", aliases = new[] { "imp-alias" } });
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal("Imported tag names cannot be changed.", await ErrorAsync(rename));
        using var aliases = await api.Client.PutAsJsonAsync($"/api/tags/{imported}", new { name = "imp", aliases = new string[0] });
        Assert.Equal(HttpStatusCode.Conflict, aliases.StatusCode);

        using var ok = await api.Client.PutAsJsonAsync($"/api/tags/{imported}",
            new { name = "imp", aliases = new[] { "imp-alias" }, category = "artist" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await Json(ok);
        Assert.Equal("artist", body.GetProperty("category").GetString());
        Assert.Equal("danbooru", body.GetProperty("origin").GetString());
        Assert.Equal("imp-alias", Assert.Single(body.GetProperty("aliases").EnumerateArray()).GetString());

        using var omitted = await api.Client.PutAsJsonAsync($"/api/tags/{imported}",
            new { name = "imp", aliases = new[] { "imp-alias" } });
        Assert.Equal("artist", (await Json(omitted)).GetProperty("category").GetString());
        using var del = await api.Client.DeleteAsync($"/api/tags/{imported}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
    }

    [DatabaseFact]
    public async Task CategoryFilterIgnoresOriginAndUsageListIncludesUnusedUserTags()
    {
        await using var host = await DeckApiHost.CreateAsync();
        await using var api = await CatalogHost.StartAsync(host.Configuration);
        var assignments = new CardCatalogRepository(host.Configuration);
        long Id(JsonElement e) => e.GetProperty("id").GetInt64();
        async Task<long> Create(string name, string? category = null)
        {
            using var response = await api.Client.PostAsJsonAsync("/api/tags", new { name, category });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return Id(await Json(response));
        }
        var unusedB = await Create("b-unused");
        var unusedA = await Create("a-unused");
        var used = await Create("c-used");
        var userArtist = await Create("z-user-artist", "artist");
        var imported = await host.Connection.ExecuteScalarAsync<long>("""
            INSERT INTO tags (name, canonical_key, category, origin) VALUES ('imp-custom', 'danbooru:ic', 'custom', 'danbooru');
            SELECT LAST_INSERT_ID();
            """);
        var card = await host.InsertCardAsync("media", "m");
        await assignments.SetTagAsync(card, used);

        async Task<long[]> List(string query)
        {
            using var response = await api.Client.GetAsync("/api/tags?" + query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await Json(response)).EnumerateArray().Select(Id).ToArray();
        }
        Assert.Equal(new[] { used, unusedA, unusedB }, await List("category=custom&sort=usage"));
        Assert.Equal(new[] { used, unusedA, unusedB, userArtist },
            (await List("sort=usage")).Where(id => id != imported).ToArray());
        Assert.Equal(new[] { userArtist }, await List("category=artist"));
        Assert.Equal(new[] { userArtist }, await List("category=artist&sort=usage"));
        Assert.Contains(imported, await List("category=custom"));
    }

    [DatabaseFact]
    public async Task MetaTagsAreMaterialisedOnCreateAndReplaceAndProtected()
    {
        await using var host = await DeckApiHost.CreateAsync();
        await using var api = await CatalogHost.StartAsync(host.Configuration);
        var cards = new CardRepository(host.Configuration);
        var created = await cards.CreateAsync(
            new CreateCardData("media", "pic", null, CardMetadata(), []),
            id => Task.FromResult<CardFileAssets?>(Assets("image", 2000, 1000)), _ => { });
        var tags = await TagsOfAsync(api, created.Id);
        Assert.Equal(new[] { "highres", "horizontal", "image" }, tags.Select(t => t.Name).Order().ToArray());
        Assert.All(tags, t =>
        {
            Assert.Equal("system", t.Source);
            Assert.Equal("meta", t.Category);
            Assert.Equal("system", t.Origin);
        });
        Assert.Equal("meta", await host.Connection.ExecuteScalarAsync<string>(
            "SELECT category FROM tags WHERE canonical_key = 'system-meta:image';"));

        var highres = tags.Single(t => t.Name == "highres").Id;
        using var put = await api.Client.PutAsync($"/api/cards/{created.Id}/tags/{highres}", null);
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal("Meta tags are set from the file.", await ErrorAsync(put));
        using var delete = await api.Client.DeleteAsync($"/api/cards/{created.Id}/tags/{highres}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        using var edit = await api.Client.PutAsJsonAsync($"/api/tags/{highres}", new { name = "highres" });
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("Meta tags are managed automatically.", await ErrorAsync(edit));

        // Replacing the file swaps the derived set; rows are not duplicated.
        await cards.UpdateAsync(created.Id, new UpdateCardData("pic", null, [], Assets("image", 300, 450)));
        Assert.Equal(new[] { "image", "lowres", "vertical" },
            (await TagsOfAsync(api, created.Id)).Select(t => t.Name).Order().ToArray());

        // Suggestions never touch system rows.
        var imageTag = await host.Connection.ExecuteScalarAsync<long>(
            "SELECT id FROM tags WHERE canonical_key = 'system-meta:image';");
        await new CardCatalogRepository(host.Configuration).UpsertSuggestionsAsync(
            created.Id, [new TagSuggestionProposal(imageTag, 0.9m, "model")]);
        Assert.Equal("system", await host.Connection.ExecuteScalarAsync<string>(
            "SELECT assignment_source FROM card_tags WHERE card_id = @Id AND tag_id = @Tag;",
            new { Id = created.Id, Tag = imageTag }));
        Assert.Equal(1, await host.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_tags WHERE card_id = @Id AND tag_id = @Tag;",
            new { Id = created.Id, Tag = imageTag }));
    }

    [DatabaseFact]
    public async Task SyncIsIdempotentSkipsNameClashesAndBackfillCoversLibrary()
    {
        await using var host = await DeckApiHost.CreateAsync();
        // A pre-existing imported tag owns the meta name "comic_book".
        await host.Connection.ExecuteAsync("""
            INSERT INTO tags (name, canonical_key, category, origin) VALUES ('comic_book', 'danbooru:comic_book', 'general', 'danbooru');
            """);
        var media = await host.InsertCardAsync("media", "legacy-media");
        await host.Connection.ExecuteAsync("""
            UPDATE cards SET metadata = JSON_OBJECT('media_type', 'video', 'width', 100, 'height', 100, 'has_audio', true)
            WHERE id = @Id;
            """, new { Id = media });
        var comic = await host.InsertCardAsync("comic", "c");
        var set = await host.InsertCardAsync("set", "s");
        var tagCard = await host.InsertCardAsync("tag", "t");

        var service = new SystemMetaTagBackfillService(host.Configuration, NullLogger<SystemMetaTagBackfillService>.Instance);
        var (processed, skipped) = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(3, processed);
        Assert.Equal(new[] { "comic_book" }, skipped);
        await service.RunOnceAsync(CancellationToken.None);

        async Task<string[]> Names(long cardId) => (await host.Connection.QueryAsync<string>("""
            SELECT t.name FROM card_tags ct JOIN tags t ON t.id = ct.tag_id
            WHERE ct.card_id = @Id AND ct.assignment_source = 'system' ORDER BY t.name;
            """, new { Id = cardId })).ToArray();
        Assert.Equal(new[] { "animated", "lowres", "sound", "square_image", "video" }, await Names(media));
        Assert.Empty(await Names(comic));
        Assert.Equal(new[] { "set" }, await Names(set));
        Assert.Empty(await Names(tagCard));
        Assert.Equal(1, await host.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tags WHERE name = 'comic_book';"));

        // Stale rows are removed when the file properties change.
        await host.Connection.ExecuteAsync("""
            UPDATE cards SET metadata = JSON_OBJECT('media_type', 'image', 'width', 2000, 'height', 3000) WHERE id = @Id;
            """, new { Id = media });
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(new[] { "highres", "image", "vertical" }, await Names(media));
    }

    private static JsonObject CardMetadata() => new() { ["created_at"] = "2026-01-01T00:00:00Z" };

    private static CardFileAssets Assets(string mediaType, int width, int height, bool hasAudio = false) =>
        new("cards/x/original.bin", "cache/cards/x/preview.webp", mediaType, "image/png", "x.png", 10, width, height,
            HasAudio: hasAudio);

    private sealed record CardTag(long Id, string Name, string Category, string Origin, string Source);

    private static async Task<CardTag[]> TagsOfAsync(CatalogHost api, long cardId)
    {
        using var response = await api.Client.GetAsync($"/api/cards/{cardId}/tags");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response)).EnumerateArray().Select(e => new CardTag(
            e.GetProperty("id").GetInt64(), e.GetProperty("name").GetString()!,
            e.GetProperty("category").GetString()!, e.GetProperty("origin").GetString()!,
            e.GetProperty("assignmentSource").GetString()!)).ToArray();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.Clone();
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return body!.RootElement.GetProperty("error").GetString();
    }

    private sealed class CatalogHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Client { get; }

        private CatalogHost(WebApplication app, HttpClient client) { _app = app; Client = client; }

        public static async Task<CatalogHost> StartAsync(Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddConfiguration(configuration);
            builder.Services.AddScoped<CatalogRepository>();
            builder.Services.AddScoped<CardCatalogRepository>();
            var app = builder.Build();
            app.MapCatalogEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            return new CatalogHost(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }
}

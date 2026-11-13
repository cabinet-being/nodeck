using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyApp.Api.Duplicates;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class DuplicateMergeApiTests
{
    // ---- detection ----

    [DatabaseFact]
    public async Task ExactCopiesFormOneGroupWithTheOldestSuggested()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var first = await host.CreateImageCardAsync(1, "first");
        var second = await host.CreateImageCardAsync(1, "second");
        var third = await host.CreateImageCardAsync(1, "third");
        await host.CreateImageCardAsync(2, "unrelated");

        var page = await ListAsync(host);

        var group = Assert.Single(page.GetProperty("groups").EnumerateArray());
        Assert.Equal("exact", group.GetProperty("kind").GetString());
        Assert.Equal([first, second, third], CardIds(group));
        Assert.Equal(first, group.GetProperty("suggestedSurvivorId").GetInt64());
        Assert.Equal(1, page.GetProperty("exactGroups").GetInt32());
        Assert.Equal(2, page.GetProperty("exactRedundantCards").GetInt32());
        Assert.Equal(8, group.GetProperty("cards")[0].GetProperty("width").GetInt32());
    }

    [DatabaseFact]
    public async Task ResizedCopyIsFoundAsSimilarAfterPerceptualHashing()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var large = await PostAsync(host, Pattern(96, png: true), "large.png");
        var small = await PostAsync(host, Pattern(48, png: false), "small.jpg");
        var other = await PostAsync(host, Pattern(64, png: true, inverted: true), "other.png");

        Assert.Equal(0, (await ListAsync(host)).GetProperty("total").GetInt32());
        var (hashed, _) = await Backfill(host).RunOnceAsync(CancellationToken.None);
        Assert.Equal(3, hashed);
        // Hashes are kept per content hash, so a second run has nothing to do.
        Assert.Equal((0, 0), await Backfill(host).RunOnceAsync(CancellationToken.None));

        var page = await ListAsync(host, "similar");
        var group = Assert.Single(page.GetProperty("groups").EnumerateArray());
        Assert.Equal("similar", group.GetProperty("kind").GetString());
        Assert.Equal([large, small], CardIds(group));
        Assert.DoesNotContain(other, CardIds(group));
        Assert.True(group.GetProperty("maxDistance").GetInt32() <= PerceptualHash.SimilarDistance);
        Assert.Equal(large, group.GetProperty("suggestedSurvivorId").GetInt64());
        Assert.Equal(0, (await ListAsync(host, "exact")).GetProperty("total").GetInt32());
    }

    [DatabaseFact]
    public async Task DismissedGroupDoesNotReappear()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var a = await host.CreateImageCardAsync(5);
        var b = await host.CreateImageCardAsync(5);
        var c = await host.CreateImageCardAsync(5);

        using var dismissed = await host.Client.PostAsJsonAsync("/api/duplicates/dismiss", new { cardIds = new[] { a, b } });
        Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        // c still pairs with both, so they remain one group until all pairs are dismissed.
        Assert.Equal(1, (await ListAsync(host)).GetProperty("total").GetInt32());

        using var all = await host.Client.PostAsJsonAsync("/api/duplicates/dismiss", new { cardIds = new[] { a, b, c } });
        Assert.Equal(HttpStatusCode.NoContent, all.StatusCode);
        Assert.Equal(0, (await ListAsync(host)).GetProperty("total").GetInt32());

        using var missing = await host.Client.PostAsJsonAsync("/api/duplicates/dismiss", new { cardIds = new[] { a, 999999 } });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ---- merge ----

    [DatabaseFact]
    public async Task MergeKeepsAllDataOnTheSurvivorAndRemovesTheDuplicate()
    {
        await using var host = await ApiTestHost.CreateAsync(withLibraryApi: true);
        var db = host.Connection;
        var survivor = await host.CreateImageCardAsync(7, "keep me");
        var duplicate = await host.CreateImageCardAsync(7, "copy");
        var neighbour = await host.CreateImageCardAsync(8, "neighbour");
        var otherPage = await host.CreateImageCardAsync(9, "other page");
        var comic = await host.CreateCollectionAsync("comic", otherPage, duplicate);
        await db.ExecuteAsync("UPDATE cards SET title = NULL, properties = '{\"artist\":\"a\"}' WHERE id = @Id;", new { Id = survivor });
        await db.ExecuteAsync("UPDATE cards SET properties = '{\"artist\":\"b\",\"note\":\"from copy\"}' WHERE id = @Id;", new { Id = duplicate });
        await db.ExecuteAsync("UPDATE cards SET metadata = JSON_SET(metadata, '$.created_at', '2020-01-01T00:00:00Z') WHERE id = @Id;", new { Id = duplicate });

        // Tags: shared, own and legacy (a tag card linked through card_relations).
        var shared = await TagAsync(db, "shared");
        var own = await TagAsync(db, "own");
        var suggested = await TagAsync(db, "suggested");
        await db.ExecuteAsync("INSERT INTO card_tags (card_id, tag_id, assignment_source) VALUES (@S, @Shared, 'auto'), (@D, @Shared, 'manual'), (@D, @Own, 'manual');",
            new { S = survivor, D = duplicate, Shared = shared, Own = own });
        var legacyCard = await db.ExecuteScalarAsync<long>(
            "INSERT INTO cards (type, title, metadata) VALUES ('tag', 'legacy', JSON_OBJECT('created_at', '2024-01-01T00:00:00Z')); SELECT LAST_INSERT_ID();");
        var legacyTag = await db.ExecuteScalarAsync<long>(
            "INSERT INTO tags (name, canonical_key, legacy_card_id) VALUES ('legacy', 'legacy', @L); SELECT LAST_INSERT_ID();", new { L = legacyCard });
        foreach (var tagged in new[] { survivor, duplicate })
        {
            var relation = await db.ExecuteScalarAsync<long>(
                "INSERT INTO card_relations (from_card_id, to_card_id, relation_type, metadata) VALUES (@C, @L, 'tagged_with', '{}'); SELECT LAST_INSERT_ID();",
                new { C = tagged, L = legacyCard });
            await db.ExecuteAsync("INSERT INTO card_tags (card_id, tag_id, assignment_source, legacy_relation_id) VALUES (@C, @T, 'manual', @R);",
                new { C = tagged, T = legacyTag, R = relation });
        }
        // Sources, relations, suggestions, decisions, views, votes, decks, creation requests.
        var source = await db.ExecuteScalarAsync<long>("INSERT INTO sources (name, canonical_key) VALUES ('site', 'site'); SELECT LAST_INSERT_ID();");
        await db.ExecuteAsync("INSERT INTO card_sources (card_id, source_id, original_url) VALUES (@S, @Src, 'https://x/1'), (@D, @Src, 'https://x/1'), (@D, @Src, 'https://x/2');",
            new { S = survivor, D = duplicate, Src = source });
        await db.ExecuteAsync("INSERT INTO card_relations (from_card_id, to_card_id, relation_type, metadata) VALUES (@D, @N, 'related_to', '{}'), (@D, @S, 'related_to', '{}');",
            new { D = duplicate, N = neighbour, S = survivor });
        await db.ExecuteAsync("INSERT INTO card_tag_suggestions (card_id, tag_id, state, confidence) VALUES (@S, @T, 'pending', 0.5), (@D, @T, 'rejected', 0.4), (@D, @Own, 'pending', 0.9);",
            new { S = survivor, D = duplicate, T = suggested, Own = own });
        await db.ExecuteAsync("INSERT INTO card_tag_decisions (card_id, suggested_tag_id, decision, decided_at_utc) VALUES (@D, @T, 'rejected', UTC_TIMESTAMP(6));",
            new { D = duplicate, T = suggested });
        await db.ExecuteAsync("INSERT INTO card_view_events (event_id, card_id, viewed_at_utc, duration_ms) VALUES (UUID(), @S, UTC_TIMESTAMP(6), 3000), (UUID(), @D, UTC_TIMESTAMP(6), 4000), (UUID(), @D, UTC_TIMESTAMP(6), 5000);",
            new { S = survivor, D = duplicate });
        await db.ExecuteAsync("""
            INSERT INTO card_daily_votes (card_id, vote_date, vote, zone_id, updated_at_utc) VALUES
                (@S, '2026-01-01', -1, 'UTC', '2026-01-01 08:00:00'), (@D, '2026-01-01', 1, 'UTC', '2026-01-01 09:00:00'),
                (@D, '2026-01-02', 1, 'UTC', '2026-01-02 09:00:00');
            """, new { S = survivor, D = duplicate });
        var deck = await db.ExecuteScalarAsync<long>("INSERT INTO decks (title, metadata) VALUES ('deck', JSON_OBJECT('created_at', '2024-01-01T00:00:00Z')); SELECT LAST_INSERT_ID();");
        var favorites = await db.ExecuteScalarAsync<long>("SELECT id FROM decks WHERE system_key = 'favorites';");
        await db.ExecuteAsync("""
            INSERT INTO deck_cards (deck_id, card_id, position, metadata) VALUES
                (@Deck, @N, 0, '{}'), (@Deck, @S, 2, '{}'), (@Deck, @D, 1, '{}'), (@Fav, @D, 0, '{}');
            """, new { Deck = deck, Fav = favorites, N = neighbour, S = survivor, D = duplicate });
        await db.ExecuteAsync("INSERT INTO card_creation_requests (scope, request_key, request_hash, card_id, created_at_utc) VALUES ('media', UUID(), UNHEX(REPEAT('ab', 32)), @D, UTC_TIMESTAMP(6));",
            new { D = duplicate });
        var duplicateDirectory = Path.Combine(host.AppDataRoot, "cards", duplicate.ToString());
        Assert.True(Directory.Exists(duplicateDirectory));

        var result = await MergeAsync(host, survivor, duplicate);

        Assert.Equal([duplicate], result.GetProperty("mergedCardIds").EnumerateArray().Select(e => e.GetInt64()));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cards WHERE id = @D;", new { D = duplicate }));
        Assert.False(Directory.Exists(duplicateDirectory));
        Assert.True(Directory.Exists(Path.Combine(host.AppDataRoot, "cards", survivor.ToString())));

        // Card fields: empty title filled, properties united, the copy's own values recorded.
        var card = await db.QuerySingleAsync<(string? Title, string Properties, string Metadata)>(
            "SELECT title, properties, metadata FROM cards WHERE id = @S;", new { S = survivor });
        Assert.Equal("copy", card.Title);
        var properties = JsonNode.Parse(card.Properties)!.AsObject();
        Assert.Equal("a", (string?)properties["artist"]);
        Assert.Equal("from copy", (string?)properties["note"]);
        var metadata = JsonNode.Parse(card.Metadata)!.AsObject();
        Assert.Equal("2020-01-01T00:00:00Z", (string?)metadata["created_at"]);
        var record = Assert.Single(metadata["merged_cards"]!.AsArray())!.AsObject();
        Assert.Equal(duplicate, (long)record["id"]!);
        Assert.Equal("copy", (string?)record["title"]);
        Assert.Equal("b", (string?)record["properties"]!["artist"]);
        Assert.Equal("seed7.png", (string?)record["original_file_name"]);

        // Tags: one row per tag, a manual assignment wins, the legacy tag keeps exactly one relation.
        var tags = (await db.QueryAsync<(long TagId, string Source)>(
            "SELECT tag_id, assignment_source FROM card_tags ct JOIN tags t ON t.id = ct.tag_id WHERE card_id = @S AND t.origin <> 'system' ORDER BY tag_id;",
            new { S = survivor })).ToList();
        Assert.Equal([(shared, "manual"), (own, "manual"), (legacyTag, "manual")], tags);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_relations WHERE from_card_id = @S AND to_card_id = @L AND relation_type = 'tagged_with';",
            new { S = survivor, L = legacyCard }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_tags ct LEFT JOIN card_relations r ON r.id = ct.legacy_relation_id WHERE ct.legacy_relation_id IS NOT NULL AND r.id IS NULL;"));

        Assert.Equal(["https://x/1", "https://x/2"], (await db.QueryAsync<string>(
            "SELECT original_url FROM card_sources WHERE card_id = @S ORDER BY original_url;", new { S = survivor })).ToArray());

        // The relation to the neighbour moves; the relation between the two copies disappears.
        Assert.Equal(1, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_relations WHERE from_card_id = @S AND to_card_id = @N AND relation_type = 'related_to';",
            new { S = survivor, N = neighbour }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_relations WHERE from_card_id = to_card_id;"));

        // Suggestions: the rejection wins over pending; a pending suggestion for an assigned tag is settled.
        var suggestions = (await db.QueryAsync<(long TagId, string State)>(
            "SELECT tag_id, state FROM card_tag_suggestions WHERE card_id = @S ORDER BY tag_id;", new { S = survivor })).ToList();
        Assert.Equal([(suggested, "rejected")], suggestions);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_tag_decisions WHERE card_id = @S;", new { S = survivor }));
        Assert.Equal(3, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_view_events WHERE card_id = @S;", new { S = survivor }));
        var votes = (await db.QueryAsync<(DateTime Date, int Vote)>(
            "SELECT vote_date, vote FROM card_daily_votes WHERE card_id = @S ORDER BY vote_date;", new { S = survivor })).ToList();
        Assert.Equal([(new DateTime(2026, 1, 1), 1), (new DateTime(2026, 1, 2), 1)], votes);

        // Decks: the survivor takes the copy's earlier place and its favorite.
        Assert.Equal([neighbour, survivor], (await db.QueryAsync<long>(
            "SELECT card_id FROM deck_cards WHERE deck_id = @Deck ORDER BY position;", new { Deck = deck })).ToArray());
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT position FROM deck_cards WHERE deck_id = @Deck AND card_id = @S;", new { Deck = deck, S = survivor }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM deck_cards WHERE deck_id = @Fav AND card_id = @S;", new { Fav = favorites, S = survivor }));

        // The comic keeps its page count and order with the survivor in the copy's place.
        Assert.Equal(new long[] { otherPage, survivor }, await host.ChildIdsAsync(comic));
        Assert.Equal(new long[] { 0L, 1L }, await host.ChildPositionsAsync(comic));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_relations WHERE from_card_id = @P AND to_card_id = @S AND relation_type = 'next_in_sequence';",
            new { P = otherPage, S = survivor }));
        Assert.Equal(survivor, await db.ExecuteScalarAsync<long>("SELECT card_id FROM card_creation_requests;"));

        await AssertNoOrphansAsync(db);

        // A repeated request changes nothing and reports the copy as already gone.
        var again = await MergeAsync(host, survivor, duplicate);
        Assert.Empty(again.GetProperty("mergedCardIds").EnumerateArray());
        Assert.Equal([duplicate], again.GetProperty("skippedCardIds").EnumerateArray().Select(e => e.GetInt64()));
        Assert.Equal(0, (await ListAsync(host)).GetProperty("total").GetInt32());
    }

    [DatabaseFact]
    public async Task MergingSeveralCopiesKeepsAHistoryOfEach()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var a = await host.CreateImageCardAsync(11, "a");
        var b = await host.CreateImageCardAsync(11, "b");
        var c = await host.CreateImageCardAsync(11, "c");
        var outsider = await host.CreateImageCardAsync(12, "outsider");
        // A dismissal between a merged copy and another card carries over to the survivor.
        using (var dismissed = await host.Client.PostAsJsonAsync("/api/duplicates/dismiss", new { cardIds = new[] { c, outsider } }))
            Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);

        var result = await MergeAsync(host, b, a, c);

        Assert.Equal([a, c], result.GetProperty("mergedCardIds").EnumerateArray().Select(e => e.GetInt64()));
        var metadata = JsonNode.Parse(await host.Connection.ExecuteScalarAsync<string>(
            "SELECT metadata FROM cards WHERE id = @B;", new { B = b }))!.AsObject();
        Assert.Equal([a, c], metadata["merged_cards"]!.AsArray().Select(n => (long)n!["id"]!));
        Assert.Equal(1, await host.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_duplicate_dismissals WHERE low_card_id = @Low AND high_card_id = @High;",
            new { Low = Math.Min(b, outsider), High = Math.Max(b, outsider) }));
        Assert.Equal(2, await host.CountCardsAsync());
        await AssertNoOrphansAsync(host.Connection);
    }

    [DatabaseFact]
    public async Task PagesOfDifferentComicsAreNotMerged()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var a = await host.CreateImageCardAsync(21);
        var b = await host.CreateImageCardAsync(21);
        await host.CreateCollectionAsync("comic", a, await host.CreateImageCardAsync(22));
        await host.CreateCollectionAsync("set", b, await host.CreateImageCardAsync(23));

        var group = Assert.Single((await ListAsync(host)).GetProperty("groups").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(group.GetProperty("conflict").GetString()));

        using var response = await host.Client.PostAsJsonAsync("/api/duplicates/merge", new { survivorId = a, cardIds = new[] { b } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(6, await host.CountCardsAsync());
    }

    [DatabaseFact]
    public async Task StandaloneCopyMergedIntoAPageLeavesTheComicIntact()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var page = await host.CreateImageCardAsync(31);
        var loose = await host.CreateImageCardAsync(31);
        var cover = await host.CreateImageCardAsync(32);
        var comic = await host.CreateCollectionAsync("comic", cover, page);

        var group = Assert.Single((await ListAsync(host)).GetProperty("groups").EnumerateArray());
        Assert.Equal(page, group.GetProperty("suggestedSurvivorId").GetInt64());
        await MergeAsync(host, page, loose);
        Assert.Equal(new long[] { cover, page }, await host.ChildIdsAsync(comic));

        // Keeping the loose copy instead moves it into the page's place.
        var copy = await host.CreateImageCardAsync(31);
        await MergeAsync(host, copy, page);
        Assert.Equal(new long[] { cover, copy }, await host.ChildIdsAsync(comic));
        Assert.Equal(2, (await host.ParentStateAsync(comic)).ContainedCount);
    }

    [DatabaseFact]
    public async Task MergeRejectsBadInput()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var a = await host.CreateImageCardAsync(41);
        var comic = await host.CreateCollectionAsync("comic", await host.CreateImageCardAsync(42), await host.CreateImageCardAsync(43));

        using var noIds = await host.Client.PostAsJsonAsync("/api/duplicates/merge", new { survivorId = a, cardIds = Array.Empty<long>() });
        Assert.Equal(HttpStatusCode.BadRequest, noIds.StatusCode);
        using var missing = await host.Client.PostAsJsonAsync("/api/duplicates/merge", new { survivorId = 999999, cardIds = new[] { a } });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var collection = await host.Client.PostAsJsonAsync("/api/duplicates/merge", new { survivorId = a, cardIds = new[] { comic } });
        Assert.Equal(HttpStatusCode.Conflict, collection.StatusCode);
        Assert.Equal(4, await host.CountCardsAsync());
    }

    // ---- helpers ----

    private static async Task AssertNoOrphansAsync(MySqlConnector.MySqlConnection db)
    {
        foreach (var (table, column) in new[]
        {
            ("card_relations", "from_card_id"), ("card_relations", "to_card_id"), ("card_tags", "card_id"),
            ("card_sources", "card_id"), ("card_tag_suggestions", "card_id"), ("card_tag_decisions", "card_id"),
            ("card_view_events", "card_id"), ("card_daily_votes", "card_id"), ("deck_cards", "card_id"),
            ("card_creation_requests", "card_id"), ("card_perceptual_hashes", "card_id"),
            ("card_duplicate_dismissals", "low_card_id"), ("card_duplicate_dismissals", "high_card_id"),
        })
            Assert.Equal(0, await db.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {table} x LEFT JOIN cards c ON c.id = x.{column} WHERE x.{column} IS NOT NULL AND c.id IS NULL;"));
    }

    private static async Task<long> TagAsync(MySqlConnector.MySqlConnection db, string name) =>
        await db.ExecuteScalarAsync<long>("INSERT INTO tags (name, canonical_key) VALUES (@Name, @Name); SELECT LAST_INSERT_ID();",
            new { Name = name });

    private static async Task<JsonElement> ListAsync(ApiTestHost host, string kind = "all")
    {
        using var response = await host.Client.GetAsync($"/api/duplicates?kind={kind}&limit=50");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return body!.RootElement.Clone();
    }

    private static async Task<JsonElement> MergeAsync(ApiTestHost host, long survivorId, params long[] cardIds)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/duplicates/merge", new { survivorId, cardIds });
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return body!.RootElement.Clone();
    }

    private static long[] CardIds(JsonElement group) =>
        group.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("id").GetInt64()).ToArray();

    private static PerceptualHashBackfillService Backfill(ApiTestHost host) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = host.ConnectionString, ["NODECK_APP_DATA"] = host.AppDataRoot,
        }).Build(), NullLogger<PerceptualHashBackfillService>.Instance);

    private static async Task<long> PostAsync(ApiTestHost host, byte[] bytes, string name)
    {
        using var response = await host.PostMediaAsync(bytes, name, name);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await ApiTestHost.IdAsync(response);
    }

    /// <summary>A picture with large shapes, so resized or re-encoded copies keep the same coarse structure.</summary>
    private static byte[] Pattern(int size, bool png, bool inverted = false)
    {
        using var image = new Image<Rgba32>(96, 96);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < 96; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < 96; x++)
                {
                    var value = (byte)((x * 2 + (x / 24 % 2 == 0 ? y : 96 - y)) % 256);
                    if (inverted) value = (byte)(255 - value);
                    row[x] = new Rgba32(value, (byte)(255 - value), 90, 255);
                }
            }
        });
        if (size != 96) image.Mutate(context => context.Resize(size, size));
        using var stream = new MemoryStream();
        if (png) image.SaveAsPng(stream); else image.SaveAsJpeg(stream);
        return stream.ToArray();
    }
}

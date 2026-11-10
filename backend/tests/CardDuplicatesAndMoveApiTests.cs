using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class CardDuplicatesAndMoveApiTests
{
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<JsonElement[]> MatchesAsync(HttpResponseMessage response)
    {
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return body!.RootElement.GetProperty("matches").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    // ---- content hash and duplicates ----

    [DatabaseFact]
    public async Task MediaCreateStoresLowercaseSha256OfWholeUpload()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var bytes = ApiTestHost.Png(1);
        using var response = await host.PostMediaAsync(bytes, "one.png", "one");
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var stored = await host.HashAsync(await ApiTestHost.IdAsync(response));

        Assert.Equal(Sha(bytes), stored);
        Assert.Matches("^[0-9a-f]{64}$", stored!);
    }

    [DatabaseFact]
    public async Task GifCreateStoresSha256()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var bytes = ApiTestHost.Gif(2);
        using var response = await host.PostMediaAsync(bytes, "one.gif", "gif");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Equal(Sha(bytes), await host.HashAsync(await ApiTestHost.IdAsync(response)));
    }

    [DatabaseFact]
    public async Task CardWithoutUploadHasNullHash()
    {
        await using var host = await ApiTestHost.CreateAsync();
        using var content = new MultipartFormDataContent
        {
            { new StringContent("media"), "type" }, { new StringContent("no file"), "title" },
        };
        using var response = await host.Client.PostAsync("/api/cards", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Null(await host.HashAsync(await ApiTestHost.IdAsync(response)));
    }

    [DatabaseFact]
    public async Task CollectionCreatedViaUploadStoresHashForEveryPage()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var first = ApiTestHost.Png(10);
        var second = ApiTestHost.Png(11);
        using var content = new MultipartFormDataContent
        {
            { new StringContent("comic"), "type" }, { new StringContent("uploaded comic"), "title" },
            { new ByteArrayContent(first), "images", "p1.png" }, { new ByteArrayContent(second), "images", "p2.png" },
        };
        using var response = await host.Client.PostAsync("/api/cards", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var comicId = await ApiTestHost.IdAsync(response);

        var childIds = await host.ChildIdsAsync(comicId);
        Assert.Equal(2, childIds.Length);
        Assert.Equal(Sha(first), await host.HashAsync(childIds[0]));
        Assert.Equal(Sha(second), await host.HashAsync(childIds[1]));
        Assert.Null(await host.HashAsync(comicId));
    }

    [DatabaseFact]
    public async Task SameFileUploadedTwiceYieldsOneMatchWithAscendingIds()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var bytes = ApiTestHost.Png(20);
        var other = await host.CreateImageCardAsync(21);
        var firstId = await host.CreateImageCardAsync(20);
        using var second = await host.PostMediaAsync(bytes, "copy.png", "copy");
        var secondId = await ApiTestHost.IdAsync(second);
        // Re-create the first with identical bytes to make the pair explicit.
        using var third = await host.PostMediaAsync(ApiTestHost.Png(20), "again.png", "again");
        var thirdId = await ApiTestHost.IdAsync(third);
        Assert.NotEqual(0, other);

        using var response = await host.Client.PostAsJsonAsync("/api/cards/duplicates",
            new { sha256 = new[] { Sha(bytes) } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var matches = await MatchesAsync(response);
        var match = Assert.Single(matches);
        Assert.Equal(Sha(bytes), match.GetProperty("sha256").GetString());
        Assert.Equal(new[] { firstId, secondId, thirdId },
            match.GetProperty("cardIds").EnumerateArray().Select(e => e.GetInt64()).ToArray());
    }

    [DatabaseFact]
    public async Task UppercaseInputMatchesAndResponseIsLowercase()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var bytes = ApiTestHost.Png(30);
        using var created = await host.PostMediaAsync(bytes, "a.png", "a");
        var id = await ApiTestHost.IdAsync(created);

        using var response = await host.Client.PostAsJsonAsync("/api/cards/duplicates",
            new { sha256 = new[] { Sha(bytes).ToUpperInvariant() } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var match = Assert.Single(await MatchesAsync(response));
        Assert.Equal(Sha(bytes), match.GetProperty("sha256").GetString());
        Assert.Equal(new[] { id }, match.GetProperty("cardIds").EnumerateArray().Select(e => e.GetInt64()).ToArray());
    }

    [DatabaseFact]
    public async Task UnknownHashAndDuplicateInputReturnNoOrSingleMatch()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var bytes = ApiTestHost.Png(40);
        using var created = await host.PostMediaAsync(bytes, "a.png", "a");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var unknown = await host.Client.PostAsJsonAsync("/api/cards/duplicates",
            new { sha256 = new[] { new string('a', 64) } });
        using var repeated = await host.Client.PostAsJsonAsync("/api/cards/duplicates",
            new { sha256 = new[] { Sha(bytes), Sha(bytes), Sha(bytes).ToUpperInvariant() } });

        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Empty(await MatchesAsync(unknown));
        Assert.Single(await MatchesAsync(repeated));
    }

    [DatabaseFact]
    public async Task InvalidDuplicateRequestsReturnBadRequest()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var valid = new string('a', 64);
        var bodies = new Dictionary<string, object?>
        {
            ["empty list"] = new { sha256 = Array.Empty<string>() },
            ["missing field"] = new { },
            ["null list"] = new { sha256 = (string[]?)null },
            ["501 entries"] = new { sha256 = Enumerable.Repeat(valid, 501).ToArray() },
            ["short hash"] = new { sha256 = new[] { new string('a', 63) } },
            ["long hash"] = new { sha256 = new[] { new string('a', 65) } },
            ["non hex"] = new { sha256 = new[] { new string('g', 64) } },
            ["one bad among good"] = new { sha256 = new[] { valid, new string('z', 64) } },
            ["null entry"] = new { sha256 = new string?[] { null } },
        };
        foreach (var (name, body) in bodies)
        {
            using var response = await host.Client.PostAsJsonAsync("/api/cards/duplicates", body);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{name}: {response.StatusCode}");
        }

        using var maximum = await host.Client.PostAsJsonAsync("/api/cards/duplicates",
            new { sha256 = Enumerable.Repeat(valid, 500).ToArray() });
        Assert.Equal(HttpStatusCode.OK, maximum.StatusCode);
    }

    [DatabaseFact]
    public async Task MetadataOnlyUpdateKeepsHashAndFileReplacementChangesIt()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var original = ApiTestHost.Png(50);
        using var created = await host.PostMediaAsync(original, "a.png", "before");
        var id = await ApiTestHost.IdAsync(created);
        Assert.Equal(Sha(original), await host.HashAsync(id));

        using var titleOnly = await host.PutMediaAsync(id, null, null, "after");
        Assert.Equal(HttpStatusCode.OK, titleOnly.StatusCode);
        Assert.Equal(Sha(original), await host.HashAsync(id));

        var replacement = ApiTestHost.Png(51);
        using var replaced = await host.PutMediaAsync(id, replacement, "b.png", "after");
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(Sha(replacement), await host.HashAsync(id));
        Assert.NotEqual(Sha(original), await host.HashAsync(id));

        using var oldLookup = await host.Client.PostAsJsonAsync("/api/cards/duplicates",
            new { sha256 = new[] { Sha(original) } });
        Assert.Empty(await MatchesAsync(oldLookup));
    }

    // ---- move child ----

    [DatabaseFact]
    public async Task MoveUpdatesBothParentsOrderPreviewAndCount()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var a1 = await host.CreateImageCardAsync(100);
        var a2 = await host.CreateImageCardAsync(101);
        var a3 = await host.CreateImageCardAsync(102);
        var b1 = await host.CreateImageCardAsync(103);
        var b2 = await host.CreateImageCardAsync(104);
        var comic = await host.CreateCollectionAsync("comic", a1, a2, a3);
        var set = await host.CreateCollectionAsync("set", b1, b2);

        using var response = await host.MoveAsync(comic, a1, set, 1);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new[] { a2, a3 }, await host.ChildIdsAsync(comic));
        Assert.Equal(new long[] { 0, 1 }, await host.ChildPositionsAsync(comic));
        Assert.Equal(new[] { b1, a1, b2 }, await host.ChildIdsAsync(set));
        Assert.Equal(new long[] { 0, 1, 2 }, await host.ChildPositionsAsync(set));
        var comicState = await host.ParentStateAsync(comic);
        var setState = await host.ParentStateAsync(set);
        Assert.Equal(2, comicState.ContainedCount);
        Assert.Equal(3, setState.ContainedCount);
        Assert.Equal(await PreviewOfAsync(host, a2), comicState.Preview);
        Assert.Equal(await PreviewOfAsync(host, b1), setState.Preview);
        Assert.NotNull(comicState.Preview);
        // Comic order chain is rebuilt: only a2 -> a3 remains inside the source.
        Assert.Equal(1, await host.Connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM card_relations WHERE relation_type = 'next_in_sequence'
            AND from_card_id IN @Ids AND to_card_id IN @Ids;
            """, new { Ids = new[] { a1, a2, a3 } }));
        Assert.Equal(a3, await host.Connection.ExecuteScalarAsync<long>(
            "SELECT to_card_id FROM card_relations WHERE relation_type = 'next_in_sequence' AND from_card_id = @Id;",
            new { Id = a2 }));
        // Ownership is exclusive.
        Assert.Equal(1, await host.Connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM card_relations WHERE relation_type = 'contains' AND to_card_id = @Id;
            """, new { Id = a1 }));
    }

    [DatabaseFact]
    public async Task MoveToFrontMiddleAndAppendPositions()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var s1 = await host.CreateImageCardAsync(110);
        var s2 = await host.CreateImageCardAsync(111);
        var s3 = await host.CreateImageCardAsync(112);
        var t1 = await host.CreateImageCardAsync(113);
        var t2 = await host.CreateImageCardAsync(114);
        var source = await host.CreateCollectionAsync("set", s1, s2, s3);
        var target = await host.CreateCollectionAsync("set", t1, t2);

        using var front = await host.MoveAsync(source, s1, target, 0);
        Assert.Equal(HttpStatusCode.NoContent, front.StatusCode);
        Assert.Equal(new[] { s1, t1, t2 }, await host.ChildIdsAsync(target));
        Assert.Equal(await PreviewOfAsync(host, s1), (await host.ParentStateAsync(target)).Preview);

        using var omitted = await host.MoveAsync(source, s2, target, null);
        Assert.Equal(HttpStatusCode.NoContent, omitted.StatusCode);
        Assert.Equal(new[] { s1, t1, t2, s2 }, await host.ChildIdsAsync(target));

        using var outOfRange = await host.MoveAsync(source, s3, target, 99);
        Assert.Equal(HttpStatusCode.NoContent, outOfRange.StatusCode);
        Assert.Equal(new[] { s1, t1, t2, s2, s3 }, await host.ChildIdsAsync(target));
        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, await host.ChildPositionsAsync(target));
        Assert.Equal(5, (await host.ParentStateAsync(target)).ContainedCount);
    }

    [DatabaseFact]
    public async Task MovingLastChildLeavesEmptySourceWithNullPreview()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var a = await host.CreateImageCardAsync(120);
        var x = await host.CreateImageCardAsync(123);
        var b = await host.CreateImageCardAsync(121);
        var c = await host.CreateImageCardAsync(122);
        var source = await host.CreateCollectionAsync("set", x, a);
        var target = await host.CreateCollectionAsync("comic", b, c);

        using var first = await host.MoveAsync(source, x, target, 2);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(new[] { a }, await host.ChildIdsAsync(source));
        using var response = await host.MoveAsync(source, a, target, 0);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await host.ChildIdsAsync(source));
        var state = await host.ParentStateAsync(source);
        Assert.Equal(0, state.ContainedCount);
        Assert.Null(state.Preview);
        Assert.Equal(new[] { a, b, c, x }, await host.ChildIdsAsync(target));
        Assert.Equal(4, (await host.ParentStateAsync(target)).ContainedCount);
        Assert.Equal(await PreviewOfAsync(host, a), (await host.ParentStateAsync(target)).Preview);
        Assert.Equal(1, await host.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cards WHERE id = @Id;", new { Id = source }));
    }

    [DatabaseFact]
    public async Task MoveRejectsInvalidRequestsWithoutChangingAnything()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var a1 = await host.CreateImageCardAsync(130);
        var a2 = await host.CreateImageCardAsync(131);
        var b1 = await host.CreateImageCardAsync(132);
        var b2 = await host.CreateImageCardAsync(133);
        var loose = await host.CreateImageCardAsync(134);
        var source = await host.CreateCollectionAsync("comic", a1, a2);
        var target = await host.CreateCollectionAsync("set", b1, b2);

        async Task AssertStatus(HttpStatusCode expected, long sourceId, long childId, long targetId, string name)
        {
            using var response = await host.MoveAsync(sourceId, childId, targetId, 0);
            Assert.True(response.StatusCode == expected, $"{name}: {response.StatusCode}");
        }

        await AssertStatus(HttpStatusCode.BadRequest, source, a1, loose, "target is media");
        await AssertStatus(HttpStatusCode.BadRequest, source, a1, source, "target equals source");
        await AssertStatus(HttpStatusCode.NotFound, source, b1, target, "child is in another parent");
        await AssertStatus(HttpStatusCode.NotFound, source, loose, target, "child has no parent");
        await AssertStatus(HttpStatusCode.NotFound, source, 999999, target, "unknown child");
        await AssertStatus(HttpStatusCode.NotFound, 999999, a1, target, "unknown source");
        await AssertStatus(HttpStatusCode.NotFound, source, a1, 999999, "unknown target");
        await AssertStatus(HttpStatusCode.NotFound, loose, a1, target, "source is media");

        Assert.Equal(new[] { a1, a2 }, await host.ChildIdsAsync(source));
        Assert.Equal(new[] { b1, b2 }, await host.ChildIdsAsync(target));
        Assert.Equal(2, (await host.ParentStateAsync(source)).ContainedCount);
        Assert.Equal(2, (await host.ParentStateAsync(target)).ContainedCount);
    }

    // ---- PUT children ----

    [DatabaseFact]
    public async Task PutChildrenReordersWithoutChangingMembership()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var a = await host.CreateImageCardAsync(140);
        var b = await host.CreateImageCardAsync(141);
        var c = await host.CreateImageCardAsync(142);
        var comic = await host.CreateCollectionAsync("comic", a, b, c);

        using var response = await host.Client.PutAsJsonAsync($"/api/cards/{comic}/children",
            new { childCardIds = new[] { c, a, b } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { c, a, b }, await host.ChildIdsAsync(comic));
        var state = await host.ParentStateAsync(comic);
        Assert.Equal(3, state.ContainedCount);
        Assert.Equal(await PreviewOfAsync(host, c), state.Preview);
    }

    [DatabaseFact]
    public async Task PutEmptyChildrenDetachesAllAndClearsParentState()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var a = await host.CreateImageCardAsync(150);
        var b = await host.CreateImageCardAsync(151);
        var set = await host.CreateCollectionAsync("set", a, b);

        using var response = await host.Client.PutAsJsonAsync($"/api/cards/{set}/children",
            new { childCardIds = Array.Empty<long>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await host.ChildIdsAsync(set));
        var state = await host.ParentStateAsync(set);
        Assert.Equal(0, state.ContainedCount);
        Assert.Null(state.Preview);
        Assert.Equal(3, await host.CountCardsAsync());
        using var childLookup = await host.Client.GetAsync($"/api/cards/{a}");
        Assert.Equal(HttpStatusCode.OK, childLookup.StatusCode);
        Assert.Equal(0, await host.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_relations WHERE relation_type = 'contains' AND to_card_id IN @Ids;",
            new { Ids = new[] { a, b } }));
    }

    private static Task<string?> PreviewOfAsync(ApiTestHost host, long id) =>
        host.Connection.ExecuteScalarAsync<string?>("SELECT preview FROM cards WHERE id = @Id;", new { Id = id });
}

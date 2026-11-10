using System.Net;
using Dapper;
using MyApp.Api.Catalog;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class CardCatalogTagFilterApiTests
{
    private static async Task<long[]> FilterAsync(DeckApiHost host, string tags, string extra = "")
    {
        var (status, items) = await host.ListAsync($"catalog_tags={tags}{extra}");
        Assert.Equal(HttpStatusCode.OK, status);
        var ids = DeckApiHost.Ids(items);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        return ids.OrderBy(id => id).ToArray();
    }

    private static long[] Sorted(params long[] ids) => ids.OrderBy(id => id).ToArray();

    private static async Task<long> TagAsync(DeckApiHost host, string name) =>
        (await new CatalogRepository(host.Configuration).CreateTagAsync(new TagRequest(name, null))).Id;

    private static async Task<long> LegacyTagAsync(DeckApiHost host, string name)
    {
        var tagId = await TagAsync(host, name);
        var legacyCard = await host.InsertCardAsync("tag", $"legacy {name}");
        await host.Connection.ExecuteAsync("UPDATE tags SET legacy_card_id = @Card WHERE id = @Tag;",
            new { Card = legacyCard, Tag = tagId });
        return tagId;
    }

    private static async Task LegacyLinkAsync(DeckApiHost host, long cardId, long tagId)
    {
        var legacyCard = await host.Connection.ExecuteScalarAsync<long>(
            "SELECT legacy_card_id FROM tags WHERE id = @Tag;", new { Tag = tagId });
        await host.Connection.ExecuteAsync("""
            INSERT INTO card_relations (from_card_id, to_card_id, relation_type, properties, metadata)
            VALUES (@From, @To, 'tagged_with', JSON_OBJECT(), JSON_OBJECT());
            """, new { From = cardId, To = legacyCard });
    }

    [DatabaseFact]
    public async Task DirectTagMatchesCardOnceAndExcludesOthers()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var assignments = new CardCatalogRepository(host.Configuration);
        var tag = await TagAsync(host, $"direct-{token}");
        var other = await TagAsync(host, $"other-{token}");
        var tagged = await host.InsertCardAsync("media", "tagged");
        var both = await host.InsertCardAsync("media", "both");
        var otherTagged = await host.InsertCardAsync("media", "other-tagged");
        var plain = await host.InsertCardAsync("media", "plain");
        await assignments.SetTagAsync(tagged, tag);
        await assignments.SetTagAsync(both, tag);
        await assignments.SetTagAsync(both, other);
        await assignments.SetTagAsync(otherTagged, other);

        Assert.Equal(Sorted(tagged, both), await FilterAsync(host, tag.ToString()));
        Assert.DoesNotContain(plain, await FilterAsync(host, tag.ToString()));
    }

    [DatabaseFact]
    public async Task CollectionMatchesThroughChildAndChildItselfMatches()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var assignments = new CardCatalogRepository(host.Configuration);
        var tag = await TagAsync(host, $"child-{token}");
        var comic = await host.InsertCardAsync("comic", "comic");
        var set = await host.InsertCardAsync("set", "set");
        var emptyComic = await host.InsertCardAsync("comic", "empty-comic");
        var c1 = await host.InsertCardAsync("media", "c1");
        var c2 = await host.InsertCardAsync("media", "c2");
        var c3 = await host.InsertCardAsync("media", "c3");
        var plainChild = await host.InsertCardAsync("media", "plain-child");
        await assignments.SetTagAsync(c1, tag);
        await assignments.SetTagAsync(c2, tag);
        await assignments.SetTagAsync(c3, tag);
        await host.AddChildAsync(comic, c1, 0);
        await host.AddChildAsync(comic, c2, 1);
        await host.AddChildAsync(set, c3, 0);
        await host.AddChildAsync(emptyComic, plainChild, 0);

        Assert.Equal(Sorted(comic, set, c1, c2, c3), await FilterAsync(host, tag.ToString()));
    }

    [DatabaseFact]
    public async Task LegacyTaggedWithLinksMatchCardsAndCollections()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var tag = await LegacyTagAsync(host, $"legacy-{token}");
        var tagged = await host.InsertCardAsync("media", "legacy-tagged");
        var untagged = await host.InsertCardAsync("media", "legacy-untagged");
        var comic = await host.InsertCardAsync("comic", "legacy-comic");
        var child1 = await host.InsertCardAsync("media", "legacy-child1");
        var child2 = await host.InsertCardAsync("media", "legacy-child2");
        var emptyComic = await host.InsertCardAsync("comic", "legacy-empty-comic");
        var plainChild = await host.InsertCardAsync("media", "legacy-plain-child");
        await LegacyLinkAsync(host, tagged, tag);
        await LegacyLinkAsync(host, child1, tag);
        await LegacyLinkAsync(host, child2, tag);
        await host.AddChildAsync(comic, child1, 0);
        await host.AddChildAsync(comic, child2, 1);
        await host.AddChildAsync(emptyComic, plainChild, 0);

        var result = await FilterAsync(host, tag.ToString());
        Assert.Equal(Sorted(tagged, comic, child1, child2), result);
        Assert.DoesNotContain(untagged, result);
    }

    [DatabaseFact]
    public async Task MultipleTagIdsAreAnded()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var assignments = new CardCatalogRepository(host.Configuration);
        var a = await TagAsync(host, $"and-a-{token}");
        var b = await TagAsync(host, $"and-b-{token}");
        var legacyB = await LegacyTagAsync(host, $"and-lb-{token}");
        var onlyA = await host.InsertCardAsync("media", "only-a");
        var bothTags = await host.InsertCardAsync("media", "both-tags");
        var split = await host.InsertCardAsync("comic", "split-children");
        var childA = await host.InsertCardAsync("media", "child-a");
        var childB = await host.InsertCardAsync("media", "child-b");
        var mixed = await host.InsertCardAsync("media", "mixed-legacy");
        var onlyAComic = await host.InsertCardAsync("comic", "only-a-comic");
        var onlyAChild = await host.InsertCardAsync("media", "only-a-child");
        await assignments.SetTagAsync(onlyA, a);
        await assignments.SetTagAsync(bothTags, a);
        await assignments.SetTagAsync(bothTags, b);
        await assignments.SetTagAsync(childA, a);
        await assignments.SetTagAsync(childB, b);
        await host.AddChildAsync(split, childA, 0);
        await host.AddChildAsync(split, childB, 1);
        await assignments.SetTagAsync(mixed, a);
        await LegacyLinkAsync(host, mixed, legacyB);
        await assignments.SetTagAsync(onlyAChild, a);
        await host.AddChildAsync(onlyAComic, onlyAChild, 0);

        Assert.Equal(Sorted(bothTags, split), await FilterAsync(host, $"{a},{b}"));
        Assert.Equal(Sorted(mixed), await FilterAsync(host, $"{a},{legacyB}"));
        Assert.DoesNotContain(onlyA, await FilterAsync(host, $"{a},{b}"));
        Assert.Contains(onlyA, await FilterAsync(host, a.ToString()));
    }

    [DatabaseFact]
    public async Task CombinesWithSearchAndDeckScope()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var assignments = new CardCatalogRepository(host.Configuration);
        var tag = await TagAsync(host, $"combo-{token}");
        var inDeckMatch = await host.InsertCardAsync("media", $"findme-{token}");
        var inDeckOther = await host.InsertCardAsync("media", "inside-without-text");
        var outsideMatch = await host.InsertCardAsync("media", $"findme-{token}-outside");
        var untagged = await host.InsertCardAsync("media", $"findme-{token}-untagged");
        foreach (var id in new[] { inDeckMatch, inDeckOther, outsideMatch })
            await assignments.SetTagAsync(id, tag);
        var deckId = await host.CreateDeckAsync("combo deck", inDeckMatch, inDeckOther, untagged);

        Assert.Equal(Sorted(inDeckMatch, outsideMatch), await FilterAsync(host, tag.ToString(), $"&search=findme-{token}"));
        Assert.Equal(Sorted(inDeckMatch, inDeckOther), await FilterAsync(host, tag.ToString(), $"&deck_id={deckId}"));
        Assert.Equal(Sorted(inDeckMatch), await FilterAsync(host, tag.ToString(), $"&deck_id={deckId}&search=findme-{token}"));
    }
}

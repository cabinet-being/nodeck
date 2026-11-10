using System.Net;
using Dapper;
using MyApp.Api.Catalog;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class CardSearchApiTests
{
    private static async Task<long[]> SearchAsync(DeckApiHost host, string text)
    {
        var (status, items) = await host.ListAsync("search=" + Uri.EscapeDataString(text));
        Assert.Equal(HttpStatusCode.OK, status);
        return DeckApiHost.Ids(items).OrderBy(id => id).ToArray();
    }

    private static long[] Sorted(params long[] ids) => ids.OrderBy(id => id).ToArray();

    [DatabaseFact]
    public async Task MatchesByExactIdAndCaseInsensitiveTitleSubstring()
    {
        await using var host = await DeckApiHost.CreateAsync();
        // Letters-only token so the numeric id search cannot also hit the title substring.
        var token = new string(Guid.NewGuid().ToString("N").Select(c => (char)('a' + c % 16)).ToArray());
        var titled = await host.InsertCardAsync("media", $"Prefix-{token.ToUpperInvariant()}-suffix");
        var other = await host.InsertCardAsync("media", "unrelated");

        Assert.Equal(Sorted(titled), await SearchAsync(host, token));
        Assert.Equal(Sorted(titled), await SearchAsync(host, token.Substring(5, 12)));
        // Exact id match only; a partial id must not match through the id column.
        Assert.Equal(Sorted(other), await SearchAsync(host, other.ToString()));
        Assert.Empty(await SearchAsync(host, $"{token}-nomatch"));
    }

    [DatabaseFact]
    public async Task MatchesByCatalogTagNameAndAliasWithoutDuplicates()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var catalog = new CatalogRepository(host.Configuration);
        var assignments = new CardCatalogRepository(host.Configuration);
        var byName = (await catalog.CreateTagAsync(new TagRequest($"Tag-{token}-name", null))).Id;
        var byAlias = (await catalog.CreateTagAsync(new TagRequest("plain-name", [$"alias-{token}-x"]))).Id;
        var second = (await catalog.CreateTagAsync(new TagRequest($"second-{token}", [$"second-alias-{token}"]))).Id;
        var cardName = await host.InsertCardAsync("media", "card-a");
        var cardAlias = await host.InsertCardAsync("media", "card-b");
        var cardMulti = await host.InsertCardAsync("media", "card-c");
        var cardNone = await host.InsertCardAsync("media", "card-d");
        await assignments.SetTagAsync(cardName, byName);
        await assignments.SetTagAsync(cardAlias, byAlias);
        // Several matching tags plus a matching alias on the same card: still one item.
        await assignments.SetTagAsync(cardMulti, byName);
        await assignments.SetTagAsync(cardMulti, byAlias);
        await assignments.SetTagAsync(cardMulti, second);

        Assert.Equal(Sorted(cardName, cardMulti), await SearchAsync(host, $"tag-{token}-NAME"));
        Assert.Equal(Sorted(cardAlias, cardMulti), await SearchAsync(host, $"alias-{token}"));
        var all = await SearchAsync(host, token);
        Assert.Equal(Sorted(cardName, cardAlias, cardMulti), all);
        Assert.DoesNotContain(cardNone, all);
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [DatabaseFact]
    public async Task CollectionMatchesThroughContainedChildTagsOnceOnly()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var catalog = new CatalogRepository(host.Configuration);
        var assignments = new CardCatalogRepository(host.Configuration);
        var tagA = (await catalog.CreateTagAsync(new TagRequest($"child-{token}-a", null))).Id;
        var tagB = (await catalog.CreateTagAsync(new TagRequest("other", [$"child-alias-{token}"]))).Id;
        var comic = await host.InsertCardAsync("comic", "comic-with-tagged-children");
        var set = await host.InsertCardAsync("set", "set-with-alias-child");
        var emptyComic = await host.InsertCardAsync("comic", "comic-without-matches");
        var child1 = await host.InsertCardAsync("media", "child-1");
        var child2 = await host.InsertCardAsync("media", "child-2");
        var child3 = await host.InsertCardAsync("media", "child-3");
        var plainChild = await host.InsertCardAsync("media", "plain-child");
        await assignments.SetTagAsync(child1, tagA);
        await assignments.SetTagAsync(child2, tagA);
        await assignments.SetTagAsync(child2, tagB);
        await assignments.SetTagAsync(child3, tagB);
        await host.AddChildAsync(comic, child1, 0);
        await host.AddChildAsync(comic, child2, 1);
        await host.AddChildAsync(set, child3, 0);
        await host.AddChildAsync(emptyComic, plainChild, 0);

        var result = await SearchAsync(host, $"child-{token}-a");
        Assert.Equal(Sorted(comic, child1, child2), result);
        Assert.DoesNotContain(emptyComic, result);
        var viaAlias = await SearchAsync(host, $"child-alias-{token}");
        Assert.Equal(Sorted(comic, set, child2, child3), viaAlias);
        Assert.Equal(viaAlias.Length, viaAlias.Distinct().Count());
    }

    [DatabaseFact]
    public async Task LegacyTaggedWithRelationMatchesThroughTagCatalogEntry()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var catalog = new CatalogRepository(host.Configuration);
        var tagId = (await catalog.CreateTagAsync(new TagRequest("legacy-plain", [$"legacy-alias-{token}"]))).Id;
        var legacyCard = await host.InsertCardAsync("tag", "legacy tag card");
        await host.Connection.ExecuteAsync("UPDATE tags SET legacy_card_id = @Card WHERE id = @Tag;",
            new { Card = legacyCard, Tag = tagId });
        var tagged = await host.InsertCardAsync("media", "legacy-tagged");
        var untagged = await host.InsertCardAsync("media", "legacy-untagged");
        await host.Connection.ExecuteAsync("""
            INSERT INTO card_relations (from_card_id, to_card_id, relation_type, properties, metadata)
            VALUES (@From, @To, 'tagged_with', JSON_OBJECT(), JSON_OBJECT());
            """, new { From = tagged, To = legacyCard });

        var result = await SearchAsync(host, $"legacy-alias-{token}");
        Assert.Contains(tagged, result);
        Assert.DoesNotContain(untagged, result);
        Assert.Equal(result.Length, result.Distinct().Count());
    }

    [DatabaseFact]
    public async Task LikeWildcardsAreMatchedLiterally()
    {
        await using var host = await DeckApiHost.CreateAsync();
        var token = Guid.NewGuid().ToString("N");
        var catalog = new CatalogRepository(host.Configuration);
        var assignments = new CardCatalogRepository(host.Configuration);
        var percentTitle = await host.InsertCardAsync("media", $"{token} 100% done");
        var underscoreTitle = await host.InsertCardAsync("media", $"{token}_under");
        var bangTitle = await host.InsertCardAsync("media", $"{token}!bang");
        var plain = await host.InsertCardAsync("media", $"{token} plain text");
        var percentTagId = (await catalog.CreateTagAsync(new TagRequest($"tagpct-{token}%", null))).Id;
        var underscoreTagId = (await catalog.CreateTagAsync(new TagRequest($"tagus-{token}_x", null))).Id;
        var plainTagId = (await catalog.CreateTagAsync(new TagRequest($"tagpct-{token}Z", null))).Id;
        var percentTagged = await host.InsertCardAsync("media", "pct-tagged");
        var underscoreTagged = await host.InsertCardAsync("media", "us-tagged");
        var plainTagged = await host.InsertCardAsync("media", "z-tagged");
        await assignments.SetTagAsync(percentTagged, percentTagId);
        await assignments.SetTagAsync(underscoreTagged, underscoreTagId);
        await assignments.SetTagAsync(plainTagged, plainTagId);

        Assert.Equal(Sorted(percentTitle, percentTagged), await SearchAsync(host, "%"));
        Assert.Equal(Sorted(underscoreTitle, underscoreTagged), await SearchAsync(host, "_"));
        Assert.Equal(Sorted(bangTitle), await SearchAsync(host, "!"));
        Assert.Equal(Sorted(percentTagged), await SearchAsync(host, $"tagpct-{token}%"));
        Assert.Equal(Sorted(underscoreTagged), await SearchAsync(host, $"{token}_x"));
        Assert.Empty(await SearchAsync(host, $"{token}%done"));
        Assert.Empty(await SearchAsync(host, $"{token}_plain"));
        Assert.DoesNotContain(plain, await SearchAsync(host, "%"));
    }
}

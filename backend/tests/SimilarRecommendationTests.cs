using Dapper;
using MyApp.Api.Analysis;

namespace MyApp.Api.Tests;

/// <summary>"Similar" (contextCardId): ranked by the open card's content, not by popularity.</summary>
public sealed partial class RecommendationTests
{
    private static string SimilarUrl(long context, string mode = "pages", int limit = 50) =>
        $"/api/recommendations?mode={mode}&contextCardId={context}&limit={limit}";

    private static async Task<long> AddSystemMetaTagAsync(RecommendationScope scope, string name)
    {
        var id = await scope.AddTagAsync($"system-meta:{name}");
        await scope.Connection.ExecuteAsync(
            "UPDATE tags SET name = @Name, category = 'meta', origin = 'system' WHERE id = @Id;",
            new { Name = name, Id = id });
        return id;
    }

    [DatabaseFact]
    public async Task SimilarIgnoresSharedSystemMetaTag()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var horizontal = await AddSystemMetaTagAsync(scope, "horizontal");
        var subject = await scope.AddTagAsync("subject");
        var context = await scope.AddCardAsync("media", "context");
        var metaOnly = await scope.AddCardAsync("media", "only horizontal");
        var plain = await scope.AddCardAsync("media", "no tags");
        var match = await scope.AddCardAsync("media", "same subject");
        foreach (var card in new[] { context, metaOnly, match }) await scope.AssignTagAsync(card, horizontal);
        await scope.AssignTagAsync(context, subject);
        await scope.AssignTagAsync(match, subject);
        await scope.SameAgeAsync(context, metaOnly, plain, match);

        var items = await scope.GetItemsAsync(SimilarUrl(context));

        Assert.Equal(match, CardId(items[0]));
        Assert.Equal(ScoreOf(items, plain), ScoreOf(items, metaOnly), 6);
    }

    [DatabaseFact]
    public async Task SimilarIgnoresVotesFreshnessAndViewingProfile()
    {
        // Before, five daily likes added +8 to a card on every card's "Similar" list, as much as a
        // perfect content match, so a few liked cards filled most lists.
        await using var scope = await RecommendationScope.CreateAsync();
        var common = await scope.AddTagAsync("common look");
        var likedTag = await scope.AddTagAsync("liked genre");
        var context = await scope.AddCardAsync("media", "context");
        await scope.AssignTagAsync(context, common);
        var all = new List<long> { context };
        for (var i = 0; i < 6; i++)
        {
            var filler = await scope.AddCardAsync("media", $"common {i}");
            await scope.AssignTagAsync(filler, common);
            all.Add(filler);
        }
        var match = all[1];
        var liked = await scope.AddCardAsync("media", "liked, newest, unrelated");
        await scope.AssignTagAsync(liked, likedTag);
        for (var day = 1; day <= 5; day++) await scope.VoteAsync(liked, 1, DateTime.UtcNow.Date.AddDays(-day));
        await scope.SameAgeAsync(all.ToArray());

        var items = await scope.GetItemsAsync(SimilarUrl(context));

        Assert.True(IndexOf(items, match) < IndexOf(items, liked));
        Assert.True(ScoreOf(items, match) > 0);
        Assert.Equal(0, ScoreOf(items, liked), 6);
    }

    [DatabaseFact]
    public async Task SimilarResultsDifferBetweenUnrelatedSourceCards()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var shared = await scope.AddTagAsync("1girl");
        var groups = new List<long[]>();
        foreach (var name in new[] { "forest", "city" })
        {
            var artist = await scope.AddTagAsync($"{name} artist", "artist");
            var theme = await scope.AddTagAsync($"{name} theme");
            var cards = new long[4];
            for (var i = 0; i < cards.Length; i++)
            {
                cards[i] = await scope.AddCardAsync("media", $"{name} {i}");
                await scope.AssignTagAsync(cards[i], artist);
                await scope.AssignTagAsync(cards[i], theme);
                await scope.AssignTagAsync(cards[i], shared);
            }
            groups.Add(cards);
        }
        for (var i = 0; i < 3; i++)
        {
            var popular = await scope.AddCardAsync("media", $"popular {i}");
            await scope.AssignTagAsync(popular, shared);
            for (var day = 1; day <= 5; day++) await scope.VoteAsync(popular, 1, DateTime.UtcNow.Date.AddDays(-day));
        }

        var forest = (await scope.GetItemsAsync(SimilarUrl(groups[0][0], limit: 3))).Select(CardId).ToArray();
        var city = (await scope.GetItemsAsync(SimilarUrl(groups[1][0], limit: 3))).Select(CardId).ToArray();

        Assert.Equal(groups[0].Skip(1).Order(), forest.Order());
        Assert.Equal(groups[1].Skip(1).Order(), city.Order());
        Assert.Empty(forest.Intersect(city));
    }

    [DatabaseFact]
    public async Task HeavilyTaggedCardDoesNotOutrankFocusedMatch()
    {
        // A card with very many tags shares something with everything; the cosine keeps it from
        // topping every list.
        await using var scope = await RecommendationScope.CreateAsync();
        var series = await scope.AddTagAsync("series");
        var character = await scope.AddTagAsync("character", "character");
        var context = await scope.AddCardAsync("media", "context");
        var focused = await scope.AddCardAsync("media", "focused");
        var hub = await scope.AddCardAsync("media", "hub");
        foreach (var card in new[] { context, focused, hub })
        {
            await scope.AssignTagAsync(card, series);
            await scope.AssignTagAsync(card, character);
        }
        for (var i = 0; i < 40; i++) await scope.AssignTagAsync(hub, await scope.AddTagAsync($"extra {i}"));
        await scope.SameAgeAsync(context, focused, hub);

        var items = await scope.GetItemsAsync(SimilarUrl(context));

        Assert.True(IndexOf(items, focused) < IndexOf(items, hub));
        Assert.True(ScoreOf(items, focused) > ScoreOf(items, hub));
    }

    [DatabaseFact]
    public async Task SharedSourceMakesCardsSimilar()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var context = await scope.AddCardAsync("media", "context");
        var sameSource = await scope.AddCardAsync("media", "same source");
        var unrelated = await scope.AddCardAsync("media", "unrelated");
        var source = await scope.Connection.ExecuteScalarAsync<long>("""
            INSERT INTO sources (name, canonical_key) VALUES ('some gallery', 'some gallery');
            SELECT LAST_INSERT_ID();
            """);
        foreach (var card in new[] { context, sameSource })
            await scope.Connection.ExecuteAsync(
                "INSERT INTO card_sources (card_id, source_id) VALUES (@Card, @Source);",
                new { Card = card, Source = source });
        await scope.SameAgeAsync(context, sameSource, unrelated);

        var items = await scope.GetItemsAsync(SimilarUrl(context));

        Assert.Equal(sameSource, CardId(items[0]));
        Assert.True(ScoreOf(items, sameSource) > ScoreOf(items, unrelated));
    }

    [DatabaseFact]
    public async Task VisualFeaturesBreakATieBetweenEquallyTaggedCards()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var tag = await scope.AddTagAsync("shared");
        var context = await scope.AddCardAsync("media", "context");
        var lookalike = await scope.AddCardAsync("media", "looks alike");
        var different = new[] { await scope.AddCardAsync("media", "different 1"), await scope.AddCardAsync("media", "different 2") };
        foreach (var card in different.Append(context).Append(lookalike)) await scope.AssignTagAsync(card, tag);
        async Task FeaturesAsync(long card, int[] indices, float[] values) =>
            await scope.Connection.ExecuteAsync("""
                INSERT INTO card_visual_features (card_id, revision, model_key, dimension, features, updated_at_utc)
                VALUES (@Card, 'r1', 'wd-test', 100, @Features, UTC_TIMESTAMP(6));
                """, new { Card = card, Features = new VisualFeatureVector(100, indices, values).ToBytes() });
        await FeaturesAsync(context, [1, 2, 3], [0.9f, 0.8f, 0.7f]);
        await FeaturesAsync(lookalike, [1, 2, 3], [0.85f, 0.8f, 0.6f]);
        await FeaturesAsync(different[0], [1, 50, 60], [0.9f, 0.8f, 0.7f]);
        await FeaturesAsync(different[1], [1, 70, 80], [0.9f, 0.8f, 0.7f]);
        await scope.SameAgeAsync(different.Append(context).Append(lookalike).ToArray());

        var items = await scope.GetItemsAsync(SimilarUrl(context));

        Assert.Equal(lookalike, CardId(items[0]));
        Assert.All(different, card => Assert.True(ScoreOf(items, lookalike) > ScoreOf(items, card)));
    }

    [DatabaseFact]
    public async Task ClosePerceptualHashMakesUntaggedLookalikeSimilar()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var context = await scope.AddCardAsync("media", "context");
        var lookalike = await scope.AddCardAsync("media", "three bits away");
        var far = await scope.AddCardAsync("media", "far away");
        const long hash = 0x5A5A_1234_F0F0_0F0FL;
        foreach (var (card, value) in new[] { (context, hash), (lookalike, hash ^ 0b111), (far, ~hash) })
            await scope.Connection.ExecuteAsync("""
                INSERT INTO card_perceptual_hashes (card_id, content_sha256, hash, computed_at_utc)
                VALUES (@Card, REPEAT('0', 64), @Hash, UTC_TIMESTAMP(6));
                """, new { Card = card, Hash = value });
        await scope.SameAgeAsync(context, lookalike, far);

        var items = await scope.GetItemsAsync(SimilarUrl(context));

        Assert.Equal(lookalike, CardId(items[0]));
        Assert.True(ScoreOf(items, lookalike) > ScoreOf(items, far));
    }

    [DatabaseFact]
    public async Task PageContextExcludesItsCollectionAndSiblingsInBothModes()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var rare = await scope.AddTagAsync("rare motif");
        var comic = await scope.AddCardAsync("comic", "open comic");
        var openPage = await scope.AddCardAsync("media", "open page");
        var sibling = await scope.AddCardAsync("media", "sibling page");
        var otherComic = await scope.AddCardAsync("comic", "other comic");
        var otherPage = await scope.AddCardAsync("media", "other comic page");
        await scope.ContainAsync(comic, openPage, 0);
        await scope.ContainAsync(comic, sibling, 1);
        await scope.ContainAsync(otherComic, otherPage, 0);
        foreach (var card in new[] { openPage, sibling, otherPage }) await scope.AssignTagAsync(card, rare);

        var collections = await scope.GetItemsAsync(SimilarUrl(openPage, "collections"));
        var pages = await scope.GetItemsAsync(SimilarUrl(openPage, "pages"));

        Assert.Equal(otherComic, CardId(collections[0]));
        Assert.Equal(otherPage, CardId(pages[0]));
        foreach (var items in new[] { collections, pages })
            Assert.DoesNotContain(items, item => CardId(item) is var id && (id == comic || id == openPage || id == sibling));
        Assert.DoesNotContain(collections, item => CardId(item) == otherPage);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyApp.Api.Cards;
using MyApp.Api.Catalog;
using MyApp.Api.Decks;
using MyApp.Api.Interest;
using MyApp.Api.Recommendations;
using MyApp.Api.Storage;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class RecommendationTests
{
    [DatabaseFact]
    public async Task ColdStartReturnsEligibleCardsWithFiniteScores()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var first = await scope.AddCardAsync("media", "first");
        var second = await scope.AddCardAsync("media", "second");
        await scope.AddCardAsync("tag", "legacy tag");

        var response = await scope.Client.GetAsync("/api/recommendations?limit=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("collections", body.RootElement.GetProperty("mode").GetString());
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(new[] { first, second }, items.Select(CardId).Order().ToArray());
        Assert.All(items, item =>
        {
            Assert.True(double.IsFinite(item.GetProperty("score").GetDouble()));
            Assert.Equal("media", item.GetProperty("card").GetProperty("type").GetString());
        });
    }

    [DatabaseFact]
    public async Task ColdStartRanksUntaggedCardsByFreshnessWithoutInterestHistory()
    {
        var now = DateTime.UtcNow;
        var clock = new MutableTimeProvider(new DateTimeOffset(now));
        await using var scope = await RecommendationScope.CreateAsync(clock);
        var older = await scope.AddCardAsync("media", "older untagged");
        var newer = await scope.AddCardAsync("media", "newer untagged");
        await scope.SetCreatedAtAsync(older, now.AddDays(-30));
        await scope.SetCreatedAtAsync(newer, now.AddDays(-1));

        var first = await scope.GetItemsAsync("/api/recommendations?mode=pages&limit=2");
        var second = await scope.GetItemsAsync("/api/recommendations?mode=pages&limit=2");

        Assert.Equal(new[] { newer, older }, first.Select(CardId));
        Assert.All(first, item => Assert.True(double.IsFinite(item.GetProperty("score").GetDouble())));
        Assert.True(ScoreOf(first, newer) > ScoreOf(first, older));
        Assert.Equal(first.Select(CardId), second.Select(CardId));
        Assert.Equal(first.Select(item => item.GetProperty("score").GetDouble()),
            second.Select(item => item.GetProperty("score").GetDouble()));
    }

    [DatabaseFact]
    public async Task SharedAssignedTagRaisesCandidateAboveUnrelatedCard()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var context = await scope.AddCardAsync("media", "context");
        var matching = await scope.AddCardAsync("media", "matching");
        var unrelated = await scope.AddCardAsync("media", "unrelated");
        var tag = await scope.AddTagAsync("shared");
        await scope.AssignTagAsync(context, tag);
        await scope.AssignTagAsync(matching, tag);
        await scope.VoteAsync(context, 1, DateTime.UtcNow.Date);

        var items = await scope.GetItemsAsync($"/api/recommendations?mode=pages&contextCardId={context}");

        Assert.True(IndexOf(items, matching) < IndexOf(items, unrelated));
        Assert.True(ScoreOf(items, matching) > ScoreOf(items, unrelated));
    }

    [DatabaseFact]
    public async Task ComicContextUsesChildTagAndExcludesItsFamilyInBothModes()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var comic = await scope.AddCardAsync("comic", "current comic");
        var taggedPage = await scope.AddCardAsync("media", "tagged page");
        var otherPage = await scope.AddCardAsync("media", "other page");
        var matching = await scope.AddCardAsync("media", "unseen matching");
        var unrelated = await scope.AddCardAsync("media", "unseen unrelated");
        var tag = await scope.AddTagAsync("comic child tag");
        await scope.ContainAsync(comic, taggedPage, 0);
        await scope.ContainAsync(comic, otherPage, 1);
        await scope.AssignTagAsync(taggedPage, tag);
        await scope.AssignTagAsync(matching, tag);

        foreach (var mode in new[] { "collections", "pages" })
        {
            var items = await scope.GetItemsAsync($"/api/recommendations?mode={mode}&contextCardId={comic}");

            Assert.DoesNotContain(items, item => CardId(item) == comic ||
                CardId(item) == taggedPage || CardId(item) == otherPage);
            Assert.True(ScoreOf(items, matching) > ScoreOf(items, unrelated));
            Assert.True(IndexOf(items, matching) < IndexOf(items, unrelated));
        }
    }

    [DatabaseFact]
    public async Task PositiveVoteTagProfileFindsUnseenMatchingCardWithoutContext()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var liked = await scope.AddCardAsync("media", "liked");
        var matching = await scope.AddCardAsync("media", "unseen matching");
        var unrelated = await scope.AddCardAsync("media", "unseen unrelated");
        var tag = await scope.AddTagAsync("positive profile");
        await scope.AssignTagAsync(liked, tag);
        await scope.AssignTagAsync(matching, tag);
        await scope.VoteAsync(liked, 1, DateTime.UtcNow.Date);

        var items = await scope.GetItemsAsync("/api/recommendations?mode=pages");

        Assert.True(ScoreOf(items, matching) > ScoreOf(items, unrelated));
        Assert.True(IndexOf(items, matching) < IndexOf(items, unrelated));
    }

    [DatabaseFact]
    public async Task PositiveVotesOnSeparateDaysStrengthenUnseenTaggedCandidate()
    {
        var now = DateTime.UtcNow;
        var clock = new MutableTimeProvider(new DateTimeOffset(now));
        await using var scope = await RecommendationScope.CreateAsync(clock);
        var liked = await scope.AddCardAsync("media", "liked source");
        var matching = await scope.AddCardAsync("media", "unseen matching");
        var unrelated = await scope.AddCardAsync("media", "unseen unrelated");
        var tag = await scope.AddTagAsync("repeated vote profile");
        await scope.AssignTagAsync(liked, tag);
        await scope.AssignTagAsync(matching, tag);
        await scope.VoteAsync(liked, 1, now.Date.AddDays(-1));

        var url = $"/api/recommendations?mode=pages&contextCardId={liked}";
        var afterOneVote = await scope.GetItemsAsync(url);
        await scope.VoteAsync(liked, 1, now.Date);
        var afterTwoVotes = await scope.GetItemsAsync(url);

        Assert.DoesNotContain(afterOneVote, item => CardId(item) == liked);
        Assert.DoesNotContain(afterTwoVotes, item => CardId(item) == liked);
        Assert.True(ScoreOf(afterOneVote, matching) > ScoreOf(afterOneVote, unrelated));
        Assert.True(ScoreOf(afterTwoVotes, matching) > ScoreOf(afterOneVote, matching));
        Assert.Equal(ScoreOf(afterOneVote, unrelated), ScoreOf(afterTwoVotes, unrelated));
    }

    [DatabaseFact]
    public async Task LatestNegativeVoteExcludesCardAndItsCollection()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var excluded = await scope.AddCardAsync("media", "excluded");
        var parent = await scope.AddCardAsync("comic", "negative parent");
        var child = await scope.AddCardAsync("media", "child");
        var eligible = await scope.AddCardAsync("media", "eligible");
        var recovered = await scope.AddCardAsync("media", "recovered");
        await scope.ContainAsync(parent, child, 0);
        await scope.VoteAsync(excluded, 1, DateTime.UtcNow.Date.AddDays(-1));
        await scope.VoteAsync(excluded, -1, DateTime.UtcNow.Date);
        await scope.VoteAsync(parent, -1, DateTime.UtcNow.Date);
        await scope.VoteAsync(recovered, -1, DateTime.UtcNow.Date.AddDays(-1));
        await scope.VoteAsync(recovered, 1, DateTime.UtcNow.Date);

        var collections = await scope.GetItemsAsync("/api/recommendations?mode=collections");
        var pages = await scope.GetItemsAsync("/api/recommendations?mode=pages");

        Assert.Contains(collections, item => CardId(item) == eligible);
        Assert.Contains(collections, item => CardId(item) == recovered);
        Assert.DoesNotContain(collections, item => CardId(item) == excluded || CardId(item) == parent || CardId(item) == child);
        Assert.DoesNotContain(pages, item => CardId(item) == excluded);
    }

    [DatabaseFact]
    public async Task CollectionModeCollapsesChildrenWhilePageModeReturnsThemSeparately()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var parent = await scope.AddCardAsync("set", "parent");
        var first = await scope.AddCardAsync("media", "page one");
        var second = await scope.AddCardAsync("media", "page two");
        await scope.ContainAsync(parent, first, 0);
        await scope.ContainAsync(parent, second, 1);

        var collections = await scope.GetItemsAsync("/api/recommendations?mode=collections");
        var pages = await scope.GetItemsAsync("/api/recommendations?mode=pages");

        Assert.Contains(collections, item => CardId(item) == parent);
        Assert.DoesNotContain(collections, item => CardId(item) == first || CardId(item) == second);
        Assert.Contains(pages, item => CardId(item) == first);
        Assert.Contains(pages, item => CardId(item) == second);
    }

    [DatabaseFact]
    public async Task ManyComicPagesProduceOneCollectionAndAtMostThreeDistinctPages()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var comic = await scope.AddCardAsync("comic", "many page comic");
        var pages = new List<long>();
        for (var position = 0; position < 10; position++)
        {
            var page = await scope.AddCardAsync("media", $"page {position}");
            pages.Add(page);
            await scope.ContainAsync(comic, page, position);
            await scope.VoteAsync(page, 1, DateTime.UtcNow.Date);
        }

        var collections = await scope.GetItemsAsync("/api/recommendations?mode=collections&limit=50");
        var separatePages = await scope.GetItemsAsync("/api/recommendations?mode=pages&limit=50");

        Assert.Equal(new[] { comic }, collections.Select(CardId));
        Assert.Equal(3, separatePages.Length);
        Assert.All(separatePages, item => Assert.Contains(CardId(item), pages));
        Assert.Equal(3, separatePages.Select(CardId).Distinct().Count());
        Assert.DoesNotContain(separatePages, item => CardId(item) == comic);
    }

    [DatabaseFact]
    public async Task AdditionalEquallyLikedPagesDoNotRaiseCollectionScore()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var shortComic = await scope.AddCardAsync("comic", "three liked pages");
        var longComic = await scope.AddCardAsync("comic", "ten liked pages");
        var today = DateTime.UtcNow.Date;

        for (var position = 0; position < 10; position++)
        {
            var page = await scope.AddCardAsync("media", $"long page {position}");
            await scope.ContainAsync(longComic, page, position);
            await scope.VoteAsync(page, 1, today);
        }

        for (var position = 0; position < 3; position++)
        {
            var page = await scope.AddCardAsync("media", $"short page {position}");
            await scope.ContainAsync(shortComic, page, position);
            await scope.VoteAsync(page, 1, today);
        }

        var collections = await scope.GetItemsAsync("/api/recommendations?mode=collections");

        var shortScore = ScoreOf(collections, shortComic);
        Assert.True(shortScore > 0);
        Assert.Equal(shortScore, ScoreOf(collections, longComic));
    }

    [DatabaseFact]
    public async Task NegativeChildVoteSuppressesCollection()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var comic = await scope.AddCardAsync("comic", "comic with disliked page");
        var likedPage = await scope.AddCardAsync("media", "liked page");
        var dislikedPage = await scope.AddCardAsync("media", "disliked page");
        var eligible = await scope.AddCardAsync("media", "eligible");
        await scope.ContainAsync(comic, likedPage, 0);
        await scope.ContainAsync(comic, dislikedPage, 1);
        await scope.VoteAsync(likedPage, 1, DateTime.UtcNow.Date);
        await scope.VoteAsync(dislikedPage, -1, DateTime.UtcNow.Date);

        var collections = await scope.GetItemsAsync("/api/recommendations?mode=collections");

        Assert.Contains(collections, item => CardId(item) == eligible);
        Assert.DoesNotContain(collections, item => CardId(item) == comic);
    }

    [DatabaseFact]
    public async Task IdenticalRequestsKeepOrderAndScores()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        await scope.AddCardAsync("media", "one");
        await scope.AddCardAsync("media", "two");
        await scope.AddCardAsync("media", "three");

        var first = await scope.GetItemsAsync("/api/recommendations?mode=pages&limit=2");
        var second = await scope.GetItemsAsync("/api/recommendations?mode=pages&limit=2");

        Assert.Equal(first.Select(CardId), second.Select(CardId));
        Assert.Equal(first.Select(item => item.GetProperty("score").GetDouble()),
            second.Select(item => item.GetProperty("score").GetDouble()));
    }

    [DatabaseFact]
    public async Task RepeatedTagCandidatesMakeRoomForUnrelatedCardWithinLimit()
    {
        var now = DateTime.UtcNow;
        var clock = new MutableTimeProvider(new DateTimeOffset(now));
        await using var scope = await RecommendationScope.CreateAsync(clock);
        var liked = await scope.AddCardAsync("media", "liked source");
        var tag = await scope.AddTagAsync("repeated interest");
        await scope.AssignTagAsync(liked, tag);
        await scope.VoteAsync(liked, 1, now.Date);

        var matching = new List<long>();
        for (var index = 0; index < 20; index++)
        {
            var id = await scope.AddCardAsync("media", $"matching {index}");
            matching.Add(id);
            await scope.AssignTagAsync(id, tag);
        }
        var unrelated = await scope.AddCardAsync("media", "unrelated");
        foreach (var id in matching.Append(unrelated).Append(liked))
            await scope.SetCreatedAtAsync(id, now.AddDays(-7));

        var items = await scope.GetItemsAsync("/api/recommendations?mode=pages&limit=20");

        Assert.Equal(20, items.Length);
        Assert.Contains(items, item => CardId(item) == unrelated);
        Assert.Equal(liked, CardId(items[0]));
        Assert.True(ScoreOf(items, liked) > ScoreOf(items, unrelated));
    }

    [DatabaseFact]
    public async Task FreshnessBreaksEqualInterestSignalsWithoutAddingMoreThanTwoPoints()
    {
        var now = DateTime.UtcNow;
        var clock = new MutableTimeProvider(new DateTimeOffset(now));
        await using var scope = await RecommendationScope.CreateAsync(clock);
        var older = await scope.AddCardAsync("media", "older matching");
        var newer = await scope.AddCardAsync("media", "newer matching");
        await scope.VoteAsync(older, 1, now.Date);
        await scope.VoteAsync(newer, 1, now.Date);
        await scope.SetCreatedAtAsync(older, now.AddDays(-365));
        await scope.SetCreatedAtAsync(newer, now.AddHours(-1));

        var items = await scope.GetItemsAsync("/api/recommendations?mode=pages");

        Assert.True(IndexOf(items, newer) < IndexOf(items, older));
        var freshnessDifference = ScoreOf(items, newer) - ScoreOf(items, older);
        Assert.InRange(freshnessDifference, 0.001, 2.0);
    }

    [DatabaseFact]
    public async Task FixedClockKeepsDiversifiedOrderAndScoresStableAcrossRequests()
    {
        var now = DateTime.UtcNow;
        var clock = new MutableTimeProvider(new DateTimeOffset(now));
        await using var scope = await RecommendationScope.CreateAsync(clock);
        var liked = await scope.AddCardAsync("media", "liked source");
        var tag = await scope.AddTagAsync("stable interest");
        await scope.AssignTagAsync(liked, tag);
        await scope.VoteAsync(liked, 1, now.Date);
        for (var index = 0; index < 5; index++)
        {
            var id = await scope.AddCardAsync("media", $"similar {index}");
            await scope.AssignTagAsync(id, tag);
            await scope.SetCreatedAtAsync(id, now.AddDays(-1));
        }
        var unrelated = await scope.AddCardAsync("media", "different");
        await scope.SetCreatedAtAsync(unrelated, now.AddDays(-1));

        var first = await scope.GetItemsAsync("/api/recommendations?mode=pages&limit=5");
        var second = await scope.GetItemsAsync("/api/recommendations?mode=pages&limit=5");

        Assert.Equal(first.Select(CardId), second.Select(CardId));
        Assert.Equal(first.Select(item => item.GetProperty("score").GetDouble()),
            second.Select(item => item.GetProperty("score").GetDouble()));
    }

    [DatabaseFact]
    public async Task InvalidQueryReturnsBadRequestAndMissingContextReturnsNotFound()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        await scope.AddCardAsync("media", "available");

        foreach (var query in new[] { "mode=unknown", "limit=0", "limit=-1", "limit=not-an-integer" })
        {
            var response = await scope.Client.GetAsync($"/api/recommendations?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var missing = await scope.Client.GetAsync("/api/recommendations?contextCardId=9223372036854775807");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [DatabaseFact]
    public async Task DailyFeedIsStableWithinLocalDayAndReturnsDateAndZone()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        await using var scope = await RecommendationScope.CreateAsync(clock);
        for (var index = 0; index < 8; index++)
        {
            var id = await scope.AddCardAsync("media", $"daily {index}");
            await scope.SetCreatedAtAsync(id, new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));
        }

        var first = await scope.Client.GetAsync("/api/recommendations/daily?timeZone=Europe/Kyiv&limit=8");
        var second = await scope.Client.GetAsync("/api/recommendations/daily?timeZone=Europe/Kyiv&limit=8");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var firstBody = await ReadJsonAsync(first);
        using var secondBody = await ReadJsonAsync(second);
        Assert.Equal("2026-11-08", firstBody.RootElement.GetProperty("localDate").GetString());
        Assert.Equal("Europe/Kyiv", firstBody.RootElement.GetProperty("timeZone").GetString());
        var firstItems = firstBody.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var secondItems = secondBody.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(8, firstItems.Length);
        Assert.Equal(firstItems.Select(CardId), secondItems.Select(CardId));
        Assert.Equal(firstItems.Select(item => item.GetProperty("score").GetDouble()),
            secondItems.Select(item => item.GetProperty("score").GetDouble()));
    }

    [DatabaseFact]
    public async Task DailyFeedRollsOverAtLocalMidnight()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 8, 21, 59, 0, TimeSpan.Zero));
        await using var scope = await RecommendationScope.CreateAsync(clock);
        var old = await scope.AddCardAsync("media", "old");
        await scope.SetCreatedAtAsync(old, new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc));
        var newOnEighth = await scope.AddCardAsync("media", "created on local eighth");
        await scope.SetCreatedAtAsync(newOnEighth, new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc));

        var before = await scope.Client.GetAsync("/api/recommendations/daily?timeZone=Europe/Kyiv");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using var beforeBody = await ReadJsonAsync(before);
        Assert.Equal("2026-11-08", beforeBody.RootElement.GetProperty("localDate").GetString());
        Assert.Equal(new[] { old }, beforeBody.RootElement.GetProperty("items").EnumerateArray().Select(CardId));

        clock.SetUtcNow(new DateTimeOffset(2026, 11, 8, 22, 1, 0, TimeSpan.Zero));
        var after = await scope.Client.GetAsync("/api/recommendations/daily?timeZone=Europe/Kyiv");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        using var afterBody = await ReadJsonAsync(after);
        Assert.Equal("2026-11-09", afterBody.RootElement.GetProperty("localDate").GetString());
        Assert.Equal(new[] { old, newOnEighth },
            afterBody.RootElement.GetProperty("items").EnumerateArray().Select(CardId).Order());
    }

    [DatabaseFact]
    public async Task DailyFeedExcludesTodayAddedCardWhenOlderCandidateExists()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        await using var scope = await RecommendationScope.CreateAsync(clock);
        var older = await scope.AddCardAsync("media", "older");
        await scope.SetCreatedAtAsync(older, new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc));
        var today = await scope.AddCardAsync("media", "today");
        await scope.SetCreatedAtAsync(today, new DateTime(2026, 11, 8, 1, 0, 0, DateTimeKind.Utc));

        var items = await scope.GetItemsAsync("/api/recommendations/daily?timeZone=UTC");

        Assert.Equal(new[] { older }, items.Select(CardId));
    }

    [DatabaseFact]
    public async Task DailyFeedUsesColdStartFallbackWhenNoOlderCardExists()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        await using var scope = await RecommendationScope.CreateAsync(clock);
        var today = await scope.AddCardAsync("media", "first card today");
        await scope.SetCreatedAtAsync(today, new DateTime(2026, 11, 8, 1, 0, 0, DateTimeKind.Utc));

        var items = await scope.GetItemsAsync("/api/recommendations/daily?timeZone=UTC");

        Assert.Equal(new[] { today }, items.Select(CardId));
    }

    [DatabaseFact]
    public async Task DailyFeedSuppressesNegativeCollectionsAndReflectsDeletion()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        await using var scope = await RecommendationScope.CreateAsync(clock);
        var parent = await scope.AddCardAsync("comic", "negative child collection");
        var child = await scope.AddCardAsync("media", "negative child");
        var negativeParent = await scope.AddCardAsync("set", "negative parent");
        var retained = await scope.AddCardAsync("media", "retained");
        foreach (var id in new[] { parent, child, negativeParent, retained })
            await scope.SetCreatedAtAsync(id, new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc));
        await scope.ContainAsync(parent, child, 0);
        await scope.VoteAsync(child, -1, new DateTime(2026, 11, 8));
        await scope.VoteAsync(negativeParent, -1, new DateTime(2026, 11, 8));

        var beforeDeletion = await scope.GetItemsAsync("/api/recommendations/daily?timeZone=UTC");
        Assert.Equal(new[] { retained }, beforeDeletion.Select(CardId));

        await scope.DeleteCardAsync(retained);
        var afterDeletion = await scope.GetItemsAsync("/api/recommendations/daily?timeZone=UTC");
        Assert.DoesNotContain(afterDeletion, item => CardId(item) == retained);
    }

    [DatabaseFact]
    public async Task DailyFeedRejectsInvalidTimeZoneAndLimit()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        foreach (var query in new[]
        {
            "timeZone=Not/A_Zone", "timeZone=UTC&limit=0", "timeZone=UTC&limit=51",
            "timeZone=UTC&limit=not-an-integer"
        })
        {
            var response = await scope.Client.GetAsync($"/api/recommendations/daily?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task RankedFeedSkipsMissingTopCandidateAndFillsLimit()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var next = await scope.AddCardAsync("media", "available runner up");
        var missing = await scope.AddCardAsync("media", "missing top candidate");
        await scope.VoteAsync(missing, 1, DateTime.UtcNow.Date);

        var before = await scope.GetItemsAsync("/api/recommendations?mode=pages&limit=1");
        Assert.Equal(new[] { missing }, before.Select(CardId));

        scope.DeleteContent(missing);
        var after = await scope.GetItemsAsync("/api/recommendations?mode=pages&limit=1");

        Assert.Equal(new[] { next }, after.Select(CardId));
    }

    [DatabaseFact]
    public async Task DailyFeedSkipsMissingTopCardAndKeepsRemainingOrder()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        await using var scope = await RecommendationScope.CreateAsync(clock);
        for (var index = 0; index < 4; index++)
        {
            var id = await scope.AddCardAsync("media", $"daily candidate {index}");
            await scope.SetCreatedAtAsync(id, new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc));
        }

        var baseline = await scope.GetItemsAsync("/api/recommendations/daily?timeZone=UTC&limit=4");
        Assert.Equal(4, baseline.Length);
        scope.DeleteContent(CardId(baseline[0]));

        var first = await scope.GetItemsAsync("/api/recommendations/daily?timeZone=UTC&limit=2");
        var second = await scope.GetItemsAsync("/api/recommendations/daily?timeZone=UTC&limit=2");

        Assert.Equal(baseline.Skip(1).Take(2).Select(CardId), first.Select(CardId));
        Assert.Equal(first.Select(CardId), second.Select(CardId));
    }

    [DatabaseFact]
    public async Task CollectionsRequireReadableCoverAndChildContent()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var missingChildCollection = await scope.AddCardAsync("comic", "missing child content");
        var missingChild = await scope.AddCardAsync("media", "missing child");
        await scope.ContainAsync(missingChildCollection, missingChild, 0);
        scope.DeleteContent(missingChild);

        var missingCoverCollection = await scope.AddCardAsync("set", "missing cover");
        var coveredChild = await scope.AddCardAsync("media", "covered child");
        await scope.ContainAsync(missingCoverCollection, coveredChild, 0);
        scope.DeletePreview(missingCoverCollection);

        var availableCollection = await scope.AddCardAsync("comic", "available collection");
        var availableChild = await scope.AddCardAsync("media", "available child");
        await scope.ContainAsync(availableCollection, availableChild, 0);

        var collections = await scope.GetItemsAsync("/api/recommendations?mode=collections");
        var daily = await scope.GetItemsAsync("/api/recommendations/daily?timeZone=UTC");

        foreach (var items in new[] { collections, daily })
        {
            Assert.Contains(items, item => CardId(item) == availableCollection);
            Assert.DoesNotContain(items, item => CardId(item) == missingChildCollection);
            Assert.DoesNotContain(items, item => CardId(item) == missingCoverCollection);
        }
    }

    [DatabaseFact]
    public async Task DeclaredMissingPlaybackCopyExcludesMedia()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var missingPlayback = await scope.AddCardAsync("media", "missing playback copy");
        var available = await scope.AddCardAsync("media", "available");
        await scope.DeclarePlaybackAsync(missingPlayback);

        var ranked = await scope.GetItemsAsync("/api/recommendations?mode=pages");
        var daily = await scope.GetItemsAsync("/api/recommendations/daily?timeZone=UTC");

        foreach (var items in new[] { ranked, daily })
        {
            Assert.Contains(items, item => CardId(item) == available);
            Assert.DoesNotContain(items, item => CardId(item) == missingPlayback);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonDocument>()
        ?? throw new Xunit.Sdk.XunitException("Recommendation response did not contain JSON.");

    private static long CardId(JsonElement item) => item.GetProperty("card").GetProperty("id").GetInt64();
    private static int IndexOf(JsonElement[] items, long id) => Array.FindIndex(items, item => CardId(item) == id);
    private static double ScoreOf(JsonElement[] items, long id) =>
        items.Single(item => CardId(item) == id).GetProperty("score").GetDouble();

    private sealed class RecommendationScope : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;
        private readonly string _appDataRoot;
        private readonly WebApplication _app;
        private readonly MySqlConnection _connection;
        public HttpClient Client { get; }

        private RecommendationScope(string adminConnectionString, string databaseName, string appDataRoot,
            WebApplication app, MySqlConnection connection, HttpClient client)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            _appDataRoot = appDataRoot;
            _app = app;
            _connection = connection;
            Client = client;
        }

        public static async Task<RecommendationScope> CreateAsync(TimeProvider? timeProvider = null)
        {
            var builder = new MySqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!);
            if (!builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Recommendation tests require a dedicated test database.");
            var databaseName = $"nodeck_recommendations_{Guid.NewGuid():N}_test";
            var appDataRoot = Path.Combine(Path.GetTempPath(), "nodeck-recommendation-tests", Guid.NewGuid().ToString("N"));
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
                await new DeckDatabaseInitializer(configuration).InitializeAsync();
                await new CatalogDatabaseInitializer(configuration, NullLogger<CatalogDatabaseInitializer>.Instance)
                    .InitializeAsync();
                await new InterestDatabaseInitializer(configuration).InitializeAsync();

                var host = WebApplication.CreateBuilder();
                host.WebHost.UseUrls("http://127.0.0.1:0");
                host.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["ConnectionStrings:Default"] = builder.ConnectionString, ["NODECK_APP_DATA"] = appDataRoot });
                host.Services.AddSingleton<PreviewService>();
                host.Services.AddScoped<RecommendationRepository>();
                if (timeProvider is not null) host.Services.AddSingleton(timeProvider);
                app = host.Build();
                app.MapRecommendationEndpoints();
                await app.StartAsync();
                var address = app.Services.GetRequiredService<IServer>().Features
                    .Get<IServerAddressesFeature>()!.Addresses.Single();
                var client = new HttpClient { BaseAddress = new Uri(address) };
                return new RecommendationScope(adminConnectionString, databaseName, appDataRoot, app, connection, client);
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

        public async Task<long> AddCardAsync(string type, string title)
        {
            var cardId = await _connection.ExecuteScalarAsync<long>("""
            INSERT INTO cards (type, title, metadata)
            VALUES (@Type, @Title, JSON_OBJECT('created_at', DATE_FORMAT(UTC_TIMESTAMP(), '%Y-%m-%dT%H:%i:%sZ')));
            SELECT LAST_INSERT_ID();
            """, new { Type = type, Title = title });
            if (type is "media" or "comic" or "set")
            {
                var relativePath = type == "media" ? ContentPath(cardId) : PreviewPath(cardId);
                var fullPath = Path.Combine(_appDataRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllBytesAsync(fullPath, [1]);
                var metadataKey = type == "media" ? "content_path" : "preview_path";
                await _connection.ExecuteAsync("""
                    UPDATE cards SET metadata = JSON_SET(metadata, @MetadataPath, @RelativePath)
                    WHERE id = @CardId;
                    """, new { MetadataPath = $"$.{metadataKey}", RelativePath = relativePath, CardId = cardId });
                if (type == "media")
                    await _connection.ExecuteAsync("""
                        UPDATE cards SET metadata = JSON_SET(metadata, '$.playback_path', JSON_EXTRACT('null', '$'))
                        WHERE id = @CardId;
                        """, new { CardId = cardId });
            }
            return cardId;
        }

        public void DeleteContent(long cardId) => File.Delete(Path.Combine(_appDataRoot, ContentPath(cardId)));
        public void DeletePreview(long cardId) => File.Delete(Path.Combine(_appDataRoot, PreviewPath(cardId)));

        public Task DeclarePlaybackAsync(long cardId) => _connection.ExecuteAsync("""
            UPDATE cards SET metadata = JSON_SET(metadata, '$.playback_path', @PlaybackPath)
            WHERE id = @CardId;
            """, new { CardId = cardId, PlaybackPath = $"cards/{cardId}/playback.mp4" });

        private static string ContentPath(long cardId) => $"cards/{cardId}/original.jpg";
        private static string PreviewPath(long cardId) => $"cache/cards/{cardId}/preview.webp";

        public Task SetCreatedAtAsync(long cardId, DateTime createdAtUtc) => _connection.ExecuteAsync("""
            UPDATE cards SET metadata = JSON_SET(metadata, '$.created_at', @CreatedAt)
            WHERE id = @CardId;
            """, new { CardId = cardId, CreatedAt = createdAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") });

        public Task DeleteCardAsync(long cardId) => _connection.ExecuteAsync(
            "DELETE FROM cards WHERE id = @CardId;", new { CardId = cardId });

        public Task<long> AddTagAsync(string name) => _connection.ExecuteScalarAsync<long>("""
            INSERT INTO tags (name, canonical_key) VALUES (@Name, @Name);
            SELECT LAST_INSERT_ID();
            """, new { Name = name });

        public Task AssignTagAsync(long cardId, long tagId) => _connection.ExecuteAsync("""
            INSERT INTO card_tags (card_id, tag_id, assignment_source) VALUES (@CardId, @TagId, 'manual');
            """, new { CardId = cardId, TagId = tagId });

        public Task ContainAsync(long parentId, long childId, int position) => _connection.ExecuteAsync("""
            INSERT INTO card_relations (from_card_id, to_card_id, relation_type, properties, metadata)
            VALUES (@ParentId, @ChildId, 'contains', JSON_OBJECT('position', @Position), JSON_OBJECT());
            """, new { ParentId = parentId, ChildId = childId, Position = position });

        public Task VoteAsync(long cardId, int vote, DateTime date) => _connection.ExecuteAsync("""
            INSERT INTO card_daily_votes (card_id, vote_date, vote, zone_id, updated_at_utc)
            VALUES (@CardId, @Date, @Vote, 'UTC', UTC_TIMESTAMP(6));
            """, new { CardId = cardId, Date = date, Vote = vote });

        public async Task<JsonElement[]> GetItemsAsync(string url)
        {
            var response = await Client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await ReadJsonAsync(response);
            return body.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
        }

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

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}

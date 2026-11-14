using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Xunit.Abstractions;

namespace MyApp.Api.Tests;

/// <summary>Runs only with NODECK_SIMILAR_BENCHMARK=1 (and a test database): it seeds a library shaped
/// like a real one and prints how "Similar" behaves across many source cards.</summary>
public sealed class SimilarBenchmarkFactAttribute : FactAttribute
{
    public SimilarBenchmarkFactAttribute()
    {
        Skip = new DatabaseFactAttribute().Skip;
        if (Skip is null && Environment.GetEnvironmentVariable("NODECK_SIMILAR_BENCHMARK") != "1")
            Skip = "Set NODECK_SIMILAR_BENCHMARK=1 to run the Similar benchmark.";
    }
}

public sealed partial class RecommendationTests
{
    private readonly ITestOutputHelper? _output;

    public RecommendationTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 6,000 top-level images and 50 comics of 10 pages. Every card belongs to one of 60 latent
    /// topics that pick its artist, characters, series and most general tags; ~9 very common
    /// tags, 3 noise tags and system meta tags (image, horizontal, highres...) come on top.
    /// 30 "hub" cards carry 150+ tags, 40 cards were liked on five days, 500 recent views.
    /// </summary>
    [SimilarBenchmarkFact]
    public async Task SimilarBenchmark()
    {
        await using var scope = await RecommendationScope.CreateAsync();
        var db = scope.Connection;
        var rng = new Random(42);
        var now = DateTime.UtcNow;

        // Tags: ids are assigned explicitly so the seed is plain SQL.
        const int generalCount = 3000, artistCount = 300, characterCount = 400, copyrightCount = 100;
        var tagRows = new List<(long Id, string Name, string Category, string Origin, string Key)>();
        long nextTag = 1;
        long[] AddTags(string prefix, string category, int count) =>
            Enumerable.Range(0, count).Select(i =>
            {
                var id = nextTag++;
                tagRows.Add((id, $"{prefix}{i}", category, "user", $"{prefix}{i}"));
                return id;
            }).ToArray();
        var general = AddTags("g", "general", generalCount);
        var artists = AddTags("a", "artist", artistCount);
        var characters = AddTags("ch", "character", characterCount);
        var copyrights = AddTags("cp", "copyright", copyrightCount);
        var system = new Dictionary<string, long>();
        foreach (var name in new[] { "image", "horizontal", "vertical", "square_image", "highres", "absurdres", "lowres", "comic_book" })
        {
            system[name] = nextTag;
            tagRows.Add((nextTag++, name, "meta", "system", "system-meta:" + name));
        }
        foreach (var chunk in tagRows.Chunk(500))
            await db.ExecuteAsync("INSERT INTO tags (id, name, canonical_key, category, origin) VALUES " +
                string.Join(",", chunk.Select(t => $"({t.Id},'{t.Name}','{t.Key}','{t.Category}','{t.Origin}')")) + ";");

        const int topicCount = 60;
        var topics = Enumerable.Range(0, topicCount).Select(_ => new
        {
            General = general.Skip(100).OrderBy(_ => rng.Next()).Take(40).ToArray(),
            Artists = artists.OrderBy(_ => rng.Next()).Take(5).ToArray(),
            Characters = characters.OrderBy(_ => rng.Next()).Take(3).ToArray(),
            Copyright = copyrights[rng.Next(copyrights.Length)],
        }).ToArray();

        var cardTopic = new Dictionary<long, int>();
        var cardTags = new Dictionary<long, HashSet<long>>();
        var cardRows = new List<(long Id, string Type, string Metadata)>();
        var relations = new List<(long Parent, long Child, int Position)>();
        long nextCard = 1;
        HashSet<long> TagMedia(int topic, bool hub)
        {
            var t = topics[topic];
            var tags = new HashSet<long>();
            for (var i = 0; i < 100; i++)
                if (rng.NextDouble() < 0.6 / (1 + i / 5.0)) tags.Add(general[i]);
            foreach (var tag in t.General.OrderBy(_ => rng.Next()).Take(15)) tags.Add(tag);
            for (var i = 0; i < 3; i++) tags.Add(general[100 + rng.Next(generalCount - 100)]);
            if (rng.NextDouble() < 0.8)
                tags.Add(rng.NextDouble() < 0.7 ? t.Artists[rng.Next(t.Artists.Length)] : artists[rng.Next(artistCount)]);
            if (rng.NextDouble() < 0.6) tags.Add(t.Characters[rng.Next(t.Characters.Length)]);
            if (rng.NextDouble() < 0.5) tags.Add(t.Copyright);
            if (hub)
                for (var i = 0; i < 130; i++) tags.Add(general[rng.Next(generalCount)]);
            tags.Add(system["image"]);
            tags.Add(rng.NextDouble() < 0.5 ? system["horizontal"] : system["vertical"]);
            var size = rng.NextDouble();
            if (size < 0.15) tags.Add(system["lowres"]);
            else if (size > 0.4) tags.Add(system["highres"]);
            if (size > 0.85) tags.Add(system["absurdres"]);
            return tags;
        }
        string Metadata(long id, string type, DateTime created) => type == "media"
            ? $"{{\"created_at\":\"{created:yyyy-MM-ddTHH:mm:ssZ}\",\"content_path\":\"cards/{id}/original.jpg\",\"playback_path\":null}}"
            : $"{{\"created_at\":\"{created:yyyy-MM-ddTHH:mm:ssZ}\",\"preview_path\":\"cache/cards/{id}/preview.webp\"}}";
        DateTime RandomCreated() => now.AddDays(-2 - rng.NextDouble() * 700);

        var hubs = new HashSet<long>();
        var comicIndexes = Enumerable.Range(0, 6050).OrderBy(_ => rng.Next()).Take(50).ToHashSet();
        for (var i = 0; i < 6050; i++)
        {
            var topic = rng.Next(topicCount);
            if (comicIndexes.Contains(i))
            {
                var comicId = nextCard++;
                cardRows.Add((comicId, "comic", Metadata(comicId, "comic", RandomCreated())));
                cardTopic[comicId] = topic;
                cardTags[comicId] = [topics[topic].Copyright, system["comic_book"]];
                for (var page = 0; page < 10; page++)
                {
                    var pageId = nextCard++;
                    cardRows.Add((pageId, "media", Metadata(pageId, "media", RandomCreated())));
                    cardTopic[pageId] = topic;
                    cardTags[pageId] = TagMedia(topic, false);
                    relations.Add((comicId, pageId, page));
                }
                continue;
            }
            var id = nextCard++;
            var hub = hubs.Count < 30 && rng.NextDouble() < 0.006;
            if (hub) hubs.Add(id);
            cardRows.Add((id, "media", Metadata(id, "media", RandomCreated())));
            cardTopic[id] = topic;
            cardTags[id] = TagMedia(topic, hub);
        }
        foreach (var chunk in cardRows.Chunk(500))
            await db.ExecuteAsync("INSERT INTO cards (id, type, title, metadata) VALUES " +
                string.Join(",", chunk.Select(c => $"({c.Id},'{c.Type}','card {c.Id}','{c.Metadata}')")) + ";");
        foreach (var chunk in relations.Chunk(500))
            await db.ExecuteAsync("INSERT INTO card_relations (from_card_id, to_card_id, relation_type, properties, metadata) VALUES " +
                string.Join(",", chunk.Select(r => $"({r.Parent},{r.Child},'contains',JSON_OBJECT('position',{r.Position}),JSON_OBJECT())")) + ";");
        var systemIds = system.Values.ToHashSet();
        var assignments = cardTags.SelectMany(pair => pair.Value.Select(tag => (Card: pair.Key, Tag: tag))).ToArray();
        foreach (var chunk in assignments.Chunk(2000))
            await db.ExecuteAsync("INSERT INTO card_tags (card_id, tag_id, assignment_source) VALUES " +
                string.Join(",", chunk.Select(a => $"({a.Card},{a.Tag},'{(systemIds.Contains(a.Tag) ? "system" : "manual")}')")) + ";");
        foreach (var card in cardRows)
        {
            var relative = card.Type == "media" ? $"cards/{card.Id}/original.jpg" : $"cache/cards/{card.Id}/preview.webp";
            var full = Path.Combine(scope.AppDataRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, [1]);
        }

        var pageIds = relations.Select(r => r.Child).ToHashSet();
        var topLevel = cardRows.Where(c => !pageIds.Contains(c.Id)).Select(c => c.Id).ToArray();
        var liked = topLevel.Where(id => cardRows[(int)id - 1].Type == "media").OrderBy(_ => rng.Next()).Take(40).ToHashSet();
        var votes = liked.SelectMany(id => Enumerable.Range(1, 5).Select(day => (id, now.Date.AddDays(-day * 7))));
        await db.ExecuteAsync("INSERT INTO card_daily_votes (card_id, vote_date, vote, zone_id, updated_at_utc) VALUES " +
            string.Join(",", votes.Select(v => $"({v.id},'{v.Item2:yyyy-MM-dd}',1,'UTC','{v.Item2:yyyy-MM-dd} 12:00:00')")) + ";");
        var viewed = topLevel.OrderBy(_ => rng.Next()).Take(300).ToArray();
        var views = Enumerable.Range(0, 500).Select(i => (Card: viewed[rng.Next(viewed.Length)],
            At: now.AddDays(-(i < 100 ? rng.NextDouble() * 3 : 3 + rng.NextDouble() * 27))));
        await db.ExecuteAsync("INSERT INTO card_view_events (event_id, card_id, viewed_at_utc, duration_ms) VALUES " +
            string.Join(",", views.Select(v => $"(UUID(),{v.Card},'{v.At:yyyy-MM-dd HH:mm:ss}',5000)")) + ";");

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"cards={cardRows.Count} card_tags={assignments.Length} hubs={hubs.Count}");
        var sources = topLevel.Where(id => cardRows[(int)id - 1].Type == "media").OrderBy(_ => rng.Next()).Take(200).ToArray();
        foreach (var mode in new[] { "collections", "pages" })
        {
            const int shown = 24;
            var appearances = new Dictionary<long, int>();
            double topicHits = 0, jaccard = 0, slots = 0, likedSlots = 0, hubSlots = 0, recentSlots = 0;
            var times = new List<double>();
            var newest = topLevel.OrderByDescending(id => id).Take(200).ToHashSet();
            foreach (var source in mode == "pages" ? sources.Take(100) : sources)
            {
                var watch = Stopwatch.StartNew();
                var items = await scope.GetItemsAsync(
                    $"/api/recommendations?mode={mode}&contextCardId={source}&limit={shown}");
                times.Add(watch.Elapsed.TotalMilliseconds);
                var sourceTags = cardTags[source].Where(t => !systemIds.Contains(t)).ToHashSet();
                foreach (var item in items)
                {
                    var id = item.GetProperty("card").GetProperty("id").GetInt64();
                    appearances[id] = appearances.GetValueOrDefault(id) + 1;
                    slots++;
                    if (cardTopic[id] == cardTopic[source]) topicHits++;
                    if (liked.Contains(id)) likedSlots++;
                    if (hubs.Contains(id)) hubSlots++;
                    if (newest.Contains(id)) recentSlots++;
                    var tags = (cardRows[(int)id - 1].Type == "media" ? cardTags[id]
                            : relations.Where(r => r.Parent == id).SelectMany(r => cardTags[r.Child]).Concat(cardTags[id]).ToHashSet())
                        .Where(t => !systemIds.Contains(t)).ToHashSet();
                    var union = sourceTags.Union(tags).Count();
                    jaccard += union == 0 ? 0 : (double)sourceTags.Intersect(tags).Count() / union;
                }
            }
            var sourceCount = mode == "pages" ? 100 : sources.Length;
            var sticky = appearances.Where(pair => pair.Value >= sourceCount / 10).ToArray();
            times.Sort();
            report.AppendLine(CultureInfo.InvariantCulture, $"""
                [{mode}] sources={sourceCount} slots={slots}
                  distinct cards shown: {appearances.Count} ({appearances.Count / slots:P1} of slots)
                  most frequent card: in {appearances.Values.Max()} of {sourceCount} lists ({(double)appearances.Values.Max() / sourceCount:P1})
                  cards in >=10% of lists: {sticky.Length}, taking {sticky.Sum(pair => pair.Value) / slots:P1} of slots
                  same topic as source: {topicHits / slots:P1} (random would be ~{1.0 / topicCount:P1})
                  mean Jaccard of non-system tags: {jaccard / slots:F3}
                  liked cards: {likedSlots / slots:P1} of slots (random ~{liked.Count / (double)topLevel.Length:P1}); hubs: {hubSlots / slots:P1}; 200 newest: {recentSlots / slots:P1}
                  latency ms: mean {times.Average():F0}, p95 {times[(int)(times.Count * 0.95)]:F0}
                """);
        }
        _output?.WriteLine(report.ToString());
        var outPath = Environment.GetEnvironmentVariable("NODECK_SIMILAR_BENCHMARK_OUT");
        if (!string.IsNullOrEmpty(outPath)) await File.WriteAllTextAsync(outPath, report.ToString());
    }
}

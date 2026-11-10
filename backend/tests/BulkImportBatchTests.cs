using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using MyApp.Api.Storage;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class BulkImportBatchTests
{
    private const int ValidCount = 90;
    private const int CorruptedCount = 5;
    private const int Concurrency = 4;

    private sealed record Item(string Key, byte[] Bytes, string FileName, string Title, bool Valid, int? ReplayOf);

    private static List<Item> BuildBatch()
    {
        var items = new List<Item>();
        for (var i = 0; i < ValidCount; i++)
        {
            var isGif = i % 3 == 2;
            items.Add(new Item(Guid.NewGuid().ToString("D"), isGif ? ApiTestHost.Gif(i) : ApiTestHost.Png(i),
                isGif ? $"item{i}.gif" : $"item{i}.png", $"item {i}", true, null));
        }

        // Corrupted uploads at fixed positions: truncated PNG, garbage with image names, text posing as GIF.
        var truncated = ApiTestHost.Png(1_000)[..24];
        var garbage = new byte[512];
        new Random(7).NextBytes(garbage);
        var corrupted = new[]
        {
            new Item(Guid.NewGuid().ToString("D"), truncated, "broken-truncated.png", "broken 0", false, null),
            new Item(Guid.NewGuid().ToString("D"), garbage, "broken-garbage.png", "broken 1", false, null),
            new Item(Guid.NewGuid().ToString("D"), "not a gif"u8.ToArray(), "broken-text.gif", "broken 2", false, null),
            new Item(Guid.NewGuid().ToString("D"), garbage[..100], "broken-short.jpg", "broken 3", false, null),
            new Item(Guid.NewGuid().ToString("D"), truncated, "broken-again.png", "broken 4", false, null),
        };
        Assert.Equal(CorruptedCount, corrupted.Length);
        for (var i = 0; i < corrupted.Length; i++)
            items.Insert(10 + i * 17, corrupted[i]);

        // Replays of earlier valid keys, placed well after their originals.
        var replayOriginals = new[] { 0, 5, 12, 20, 31 };
        for (var i = 0; i < replayOriginals.Length; i++)
        {
            var original = items.First(item => item.Valid && item.Title == $"item {replayOriginals[i]}");
            items.Add(original with { ReplayOf = items.IndexOf(original) });
        }
        return items;
    }

    private static async Task<(HttpStatusCode Status, long? Id)> PostAsync(
        ApiTestHost host, Item item, CancellationToken cancellationToken = default)
    {
        using var response = await host.PostMediaAsync(item.Bytes, item.FileName, item.Title, item.Key, cancellationToken);
        long? id = null;
        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
            id = await ApiTestHost.IdAsync(response);
        return (response.StatusCode, id);
    }

    private static string[] LeftoverDirectories(ApiTestHost host, IEnumerable<long> knownIds) =>
        OrphanStorageScanner.FindOrphans(host.AppDataRoot, knownIds.ToHashSet(), TimeSpan.Zero,
            DateTime.UtcNow.AddMinutes(5)).ToArray();

    private static async Task<HashSet<long>> AllCardIdsAsync(ApiTestHost host) =>
        (await host.Connection.QueryAsync<long>("SELECT id FROM cards;")).ToHashSet();

    [DatabaseFact]
    public async Task MixedBatchWithBoundedConcurrencyCreatesExactlyTheDistinctValidCards()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var items = BuildBatch();
        Assert.Equal(ValidCount + CorruptedCount + 5, items.Count);
        var results = new Task<(HttpStatusCode Status, long? Id)>[items.Count];
        using var gate = new SemaphoreSlim(Concurrency);
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            results[index] = Task.Run(async () =>
            {
                // A replay waits for its original to finish so 201 versus 200 is deterministic.
                if (items[index].ReplayOf is { } original) await results[original];
                await gate.WaitAsync();
                try { return await PostAsync(host, items[index]); }
                finally { gate.Release(); }
            });
        }
        await Task.WhenAll(results);

        var createdIds = new Dictionary<string, long>();
        for (var i = 0; i < items.Count; i++)
        {
            var (status, id) = results[i].Result;
            var item = items[i];
            if (!item.Valid)
            {
                Assert.True(status == HttpStatusCode.BadRequest, $"{item.FileName}: {status}");
                continue;
            }
            if (item.ReplayOf is not null)
            {
                Assert.True(status == HttpStatusCode.OK, $"replay of {item.Title}: {status}");
                Assert.Equal(createdIds[item.Key], id);
                continue;
            }
            Assert.True(status == HttpStatusCode.Created, $"{item.Title}: {status}");
            createdIds[item.Key] = id!.Value;
        }

        Assert.Equal(ValidCount, createdIds.Count);
        Assert.Equal(ValidCount, createdIds.Values.Distinct().Count());
        Assert.Equal(ValidCount, await host.CountCardsAsync());
        Assert.Equal(ValidCount, await host.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(DISTINCT content_sha256) FROM cards WHERE content_sha256 IS NOT NULL;"));
        Assert.Equal(ValidCount, await host.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_creation_requests WHERE scope = 'media';"));
        Assert.Empty(LeftoverDirectories(host, createdIds.Values));
        foreach (var id in createdIds.Values)
            Assert.True(Directory.Exists(Path.Combine(host.AppDataRoot, "cards", id.ToString())), $"cards/{id} is missing.");

        // Build a comic and a set from the created ids.
        var ordered = createdIds.Values.Order().ToArray();
        var comicChildren = ordered[..25];
        var setChildren = ordered[25..50];
        var comic = await host.CreateCollectionAsync("comic", comicChildren);
        var set = await host.CreateCollectionAsync("set", setChildren);
        Assert.Equal(comicChildren, await host.ChildIdsAsync(comic));
        Assert.Equal(setChildren, await host.ChildIdsAsync(set));
        Assert.Equal(25, (await host.ParentStateAsync(comic)).ContainedCount);

        using var claiming = await host.Client.PostAsJsonAsync("/api/cards/collections",
            new { type = "set", title = "steals a child", childCardIds = new[] { ordered[60], comicChildren[3] } });
        Assert.Equal(HttpStatusCode.BadRequest, claiming.StatusCode);
        Assert.Equal(ValidCount + 2, await host.CountCardsAsync());
        Assert.Equal(comicChildren, await host.ChildIdsAsync(comic));
        Assert.Equal(0, await host.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_relations WHERE relation_type = 'contains' AND to_card_id = @Id AND from_card_id <> @Parent;",
            new { Id = comicChildren[3], Parent = comic }));
    }

    [DatabaseFact]
    public async Task CancellingMidBatchAndRepeatingAllKeysCreatesNoExtraCards()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var items = BuildBatch().Where(item => item.ReplayOf is null).ToList();
        using var cancellation = new CancellationTokenSource();
        var completed = 0;
        using var gate = new SemaphoreSlim(Concurrency);
        var tasks = items.Select(item => Task.Run(async () =>
        {
            try
            {
                await gate.WaitAsync(cancellation.Token);
                try
                {
                    await PostAsync(host, item, cancellation.Token);
                    if (Interlocked.Increment(ref completed) >= 30) cancellation.Cancel();
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.True(cancellation.IsCancellationRequested);

        // Let requests that were already aborted on the client finish on the server.
        var lastCount = -1;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var count = await host.CountCardsAsync();
            if (count == lastCount) break;
            lastCount = count;
            await Task.Delay(500);
        }

        var ids = new Dictionary<string, long>();
        using var repeatGate = new SemaphoreSlim(Concurrency);
        var repeats = items.Select(item => Task.Run(async () =>
        {
            await repeatGate.WaitAsync();
            try
            {
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    var (status, id) = await PostAsync(host, item);
                    if (status is HttpStatusCode.Created or HttpStatusCode.OK)
                    {
                        Assert.True(item.Valid, $"{item.FileName} must not be accepted.");
                        lock (ids) ids[item.Key] = id!.Value;
                        return;
                    }
                    if (!item.Valid)
                    {
                        Assert.Equal(HttpStatusCode.BadRequest, status);
                        return;
                    }
                    Assert.True(status is (HttpStatusCode)425 or HttpStatusCode.ServiceUnavailable,
                        $"{item.Title}: unexpected {status}");
                    await Task.Delay(300);
                }
                Assert.Fail($"{item.Title} never succeeded.");
            }
            finally { repeatGate.Release(); }
        })).ToArray();
        await Task.WhenAll(repeats);

        Assert.Equal(ValidCount, ids.Count);
        Assert.Equal(ValidCount, ids.Values.Distinct().Count());
        Assert.Equal(ValidCount, await host.CountCardsAsync());
        Assert.Equal(ids.Values.Order().ToArray(),
            (await host.Connection.QueryAsync<long>("SELECT id FROM cards ORDER BY id;")).ToArray());
        Assert.Empty(LeftoverDirectories(host, await AllCardIdsAsync(host)));
        foreach (var (key, id) in ids)
        {
            using var lookup = await host.Client.GetAsync($"/api/cards/creation-requests/media/{key}");
            Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
            Assert.Equal(id, await ApiTestHost.IdAsync(lookup));
        }
    }
}

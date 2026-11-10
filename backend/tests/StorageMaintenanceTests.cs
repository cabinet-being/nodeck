using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyApp.Api.Storage;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class ContentHashBackfillTests
{
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<long> InsertMediaAsync(ApiTestHost host, string? contentPath, string type = "media", string? hash = null)
    {
        var metadata = contentPath is null ? "{}" : JsonSerializer.Serialize(new { content_path = contentPath });
        return await host.Connection.ExecuteScalarAsync<long>("""
            INSERT INTO cards (type, title, metadata, content_sha256) VALUES (@Type, 'x', @Metadata, @Hash);
            SELECT LAST_INSERT_ID();
            """, new { Type = type, Metadata = metadata, Hash = hash });
    }

    private static ContentHashBackfillService CreateService(ApiTestHost host) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = host.ConnectionString,
            ["NODECK_APP_DATA"] = host.AppDataRoot,
        }).Build(), NullLogger<ContentHashBackfillService>.Instance);

    [DatabaseFact]
    public async Task RunOnceHashesReadableFilesAndSkipsUnreadableOnes()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var preHashedBytes = new byte[] { 9, 9 };
        Directory.CreateDirectory(Path.Combine(host.AppDataRoot, "cards", "1"));
        await File.WriteAllBytesAsync(Path.Combine(host.AppDataRoot, "cards", "1", "original.png"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(host.AppDataRoot, "outside-cards.bin"), preHashedBytes);
        var readable = await InsertMediaAsync(host, "cards/1/original.png");
        var missing = await InsertMediaAsync(host, "cards/2/original.png");
        var noPath = await InsertMediaAsync(host, null);
        var traversal = await InsertMediaAsync(host, "../outside.bin");
        var absolute = await InsertMediaAsync(host, "/etc/hostname");
        var alreadyHashed = await InsertMediaAsync(host, "cards/1/original.png", hash: new string('c', 64));
        var comic = await InsertMediaAsync(host, "cards/1/original.png", type: "comic");

        var (hashed, skipped) = await CreateService(host).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, hashed);
        Assert.Equal(4, skipped);
        Assert.Equal(Sha(bytes), await host.HashAsync(readable));
        foreach (var id in new[] { missing, noPath, traversal, absolute, comic })
            Assert.Null(await host.HashAsync(id));
        Assert.Equal(new string('c', 64), await host.HashAsync(alreadyHashed));

        // Second run only revisits the still-unhashed media cards and changes nothing.
        var (hashedAgain, skippedAgain) = await CreateService(host).RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, hashedAgain);
        Assert.Equal(4, skippedAgain);
        Assert.Equal(Sha(bytes), await host.HashAsync(readable));
    }

    [DatabaseFact]
    public async Task RunOnceProcessesMoreCardsThanOneBatchAndPicksUpLateFiles()
    {
        await using var host = await ApiTestHost.CreateAsync();
        var expected = new Dictionary<long, string>();
        for (var i = 0; i < 45; i++)
        {
            var bytes = new[] { (byte)i, (byte)(i * 3), (byte)7 };
            var relative = $"cards/b{i}/original.bin";
            Directory.CreateDirectory(Path.Combine(host.AppDataRoot, "cards", $"b{i}"));
            await File.WriteAllBytesAsync(Path.Combine(host.AppDataRoot, relative), bytes);
            expected[await InsertMediaAsync(host, relative)] = Sha(bytes);
        }
        var late = await InsertMediaAsync(host, "cards/late/original.bin");

        var (hashed, skipped) = await CreateService(host).RunOnceAsync(CancellationToken.None);
        Assert.Equal(45, hashed);
        Assert.Equal(1, skipped);

        Directory.CreateDirectory(Path.Combine(host.AppDataRoot, "cards", "late"));
        await File.WriteAllBytesAsync(Path.Combine(host.AppDataRoot, "cards", "late", "original.bin"), [42]);
        var (hashedLate, skippedLate) = await CreateService(host).RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, hashedLate);
        Assert.Equal(0, skippedLate);

        foreach (var (id, hash) in expected) Assert.Equal(hash, await host.HashAsync(id));
        Assert.Equal(Sha([42]), await host.HashAsync(late));
    }

    [DatabaseFact]
    public async Task RunOnceHonorsCancellation()
    {
        await using var host = await ApiTestHost.CreateAsync();
        Directory.CreateDirectory(Path.Combine(host.AppDataRoot, "cards", "1"));
        await File.WriteAllBytesAsync(Path.Combine(host.AppDataRoot, "cards", "1", "original.bin"), [1]);
        var id = await InsertMediaAsync(host, "cards/1/original.bin");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(host).RunOnceAsync(cancelled.Token));

        Assert.Null(await host.HashAsync(id));
    }
}

public sealed class OrphanStorageScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nodeck-orphan-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

    private string MakeDirectory(string relative, TimeSpan age)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    [Fact]
    public void MissingRootsYieldNothing()
    {
        Directory.CreateDirectory(_root);
        Assert.Empty(OrphanStorageScanner.FindOrphans(_root, new HashSet<long>(), TimeSpan.Zero, Now));
    }

    [Fact]
    public void ReportsOnlyOldNumericDirectoriesWithoutCardRowsInBothRoots()
    {
        var oldOrphan = MakeDirectory("cards/10", TimeSpan.FromDays(2));
        var oldCacheOrphan = MakeDirectory("cache/cards/11", TimeSpan.FromDays(2));
        MakeDirectory("cards/12", TimeSpan.FromDays(2));          // known id
        MakeDirectory("cache/cards/12", TimeSpan.FromDays(2));    // known id in cache
        MakeDirectory("cards/13", TimeSpan.FromMinutes(5));       // too recent
        MakeDirectory("cache/cards/14", TimeSpan.FromMinutes(5)); // too recent
        MakeDirectory("cards/not-a-number", TimeSpan.FromDays(2));
        MakeDirectory("cards/-5", TimeSpan.FromDays(2));
        MakeDirectory("cards/1.5", TimeSpan.FromDays(2));
        MakeDirectory("cache/cards/tmp", TimeSpan.FromDays(2));
        MakeDirectory("cards/99999999999999999999", TimeSpan.FromDays(2)); // overflows long
        File.WriteAllText(Path.Combine(_root, "cards", "15"), "a file, not a directory");

        var orphans = OrphanStorageScanner.FindOrphans(_root, new HashSet<long> { 12 }, TimeSpan.FromHours(1), Now);

        Assert.Equal(new[] { oldOrphan, oldCacheOrphan }.Order(StringComparer.Ordinal).ToArray(), orphans.ToArray());
    }

    [Fact]
    public void OnlyDirectoriesStrictlyOlderThanMinimumAgeAreReportedAndNothingIsDeleted()
    {
        var older = MakeDirectory("cards/20", TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        File.WriteAllText(Path.Combine(older, "original.png"), "data");
        Directory.SetLastWriteTimeUtc(older, Now - TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
        var atCutoff = MakeDirectory("cards/21", TimeSpan.FromHours(1));
        var newer = MakeDirectory("cards/22", TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));

        var orphans = OrphanStorageScanner.FindOrphans(_root, new HashSet<long>(), TimeSpan.FromHours(1), Now);

        Assert.Equal(new[] { older }, orphans.ToArray());
        Assert.True(Directory.Exists(older));
        Assert.True(File.Exists(Path.Combine(older, "original.png")));
        Assert.True(Directory.Exists(atCutoff));
        Assert.True(Directory.Exists(newer));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

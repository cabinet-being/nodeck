using System.Security.Cryptography;
using Dapper;
using MySqlConnector;

namespace MyApp.Api.Storage;

/// <summary>Hashes originals of media cards that have no content_sha256 yet, one file at a time.</summary>
public sealed class ContentHashBackfillService : BackgroundService
{
    private const int BatchSize = 20;
    private readonly string _connectionString;
    private readonly string _appDataRoot;
    private readonly ILogger<ContentHashBackfillService> _logger;
    private readonly bool _enabled;

    public ContentHashBackfillService(IConfiguration configuration, ILogger<ContentHashBackfillService> logger)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
        _appDataRoot = configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _logger = logger;
        _enabled = !string.Equals(configuration["Media:BackfillContentHash"], "false", StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        await Task.Yield();
        try
        {
            var (hashed, skipped) = await RunOnceAsync(stoppingToken);
            if (hashed + skipped > 0)
                _logger.LogInformation("Content hash backfill finished: {Hashed} hashed, {Skipped} skipped.", hashed, skipped);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Content hash backfill failed.");
        }
    }

    /// <summary>Processes every media card with NULL content_sha256 once; unreadable files are skipped.</summary>
    public async Task<(int Hashed, int Skipped)> RunOnceAsync(CancellationToken cancellationToken)
    {
        int hashed = 0, skipped = 0;
        long lastId = 0;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        while (true)
        {
            var batch = (await connection.QueryAsync<(long Id, string? Path)>(new CommandDefinition(
                """
                SELECT id, JSON_UNQUOTE(JSON_EXTRACT(metadata, '$.content_path'))
                FROM cards
                WHERE type = 'media' AND content_sha256 IS NULL AND id > @LastId
                ORDER BY id LIMIT @Limit;
                """, new { LastId = lastId, Limit = BatchSize }, cancellationToken: cancellationToken))).ToList();
            if (batch.Count == 0) return (hashed, skipped);

            foreach (var (id, path) in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastId = id;
                try
                {
                    var hash = await HashFileAsync(path, cancellationToken);
                    if (hash is null)
                    {
                        skipped++;
                        _logger.LogWarning(
                            "Content hash backfill skipped card {CardId}: original file {Path} is missing or outside app data.",
                            id, path);
                        continue;
                    }
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE cards SET content_sha256 = @Hash WHERE id = @Id AND content_sha256 IS NULL;",
                        new { Hash = hash, Id = id }, cancellationToken: cancellationToken));
                    hashed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    skipped++;
                    _logger.LogWarning(exception, "Content hash backfill skipped card {CardId}.", id);
                }
            }
            _logger.LogInformation("Content hash backfill progress: {Hashed} hashed, {Skipped} skipped, last card {CardId}.",
                hashed, skipped, lastId);
        }
    }

    private async Task<string?> HashFileAsync(string? relativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var root = Path.GetFullPath(_appDataRoot);
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var fromRoot = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(fromRoot) || fromRoot == ".." ||
            fromRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || !File.Exists(full))
            return null;
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}

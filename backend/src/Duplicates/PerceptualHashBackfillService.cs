using Dapper;
using MySqlConnector;

namespace MyApp.Api.Duplicates;

/// <summary>
/// Computes perceptual hashes of image and GIF previews for near-duplicate detection. A hash belongs to the
/// content hash it was computed for, so a replaced file is hashed again. Runs at startup and then periodically.
/// </summary>
public sealed class PerceptualHashBackfillService : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly string _connectionString;
    private readonly string _appDataRoot;
    private readonly ILogger<PerceptualHashBackfillService> _logger;
    private readonly bool _enabled;

    public PerceptualHashBackfillService(IConfiguration configuration, ILogger<PerceptualHashBackfillService> logger)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
        _appDataRoot = configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _logger = logger;
        _enabled = !string.Equals(configuration["Media:BackfillPerceptualHash"], "false", StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (hashed, skipped) = await RunOnceAsync(stoppingToken);
                if (hashed + skipped > 0)
                    _logger.LogInformation("Perceptual hash backfill: {Hashed} hashed, {Skipped} without a usable preview.",
                        hashed, skipped);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Perceptual hash backfill failed.");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Hashes every image or GIF card whose hash is missing or stale; videos are not hashed.</summary>
    public async Task<(int Hashed, int Skipped)> RunOnceAsync(CancellationToken cancellationToken)
    {
        int hashed = 0, skipped = 0;
        long lastId = 0;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        while (true)
        {
            var batch = (await connection.QueryAsync<(long Id, string Sha, string? Path)>(new CommandDefinition(
                """
                SELECT c.id, c.content_sha256,
                       COALESCE(JSON_UNQUOTE(JSON_EXTRACT(c.metadata, '$.preview_path')), c.preview)
                FROM cards c
                LEFT JOIN card_perceptual_hashes p ON p.card_id = c.id
                WHERE c.type = 'media' AND c.content_sha256 IS NOT NULL AND c.id > @LastId
                  AND JSON_UNQUOTE(JSON_EXTRACT(c.metadata, '$.media_type')) IN ('image', 'gif')
                  AND (p.card_id IS NULL OR p.content_sha256 <> c.content_sha256)
                ORDER BY c.id LIMIT @Limit;
                """, new { LastId = lastId, Limit = BatchSize }, cancellationToken: cancellationToken))).ToList();
            if (batch.Count == 0) return (hashed, skipped);

            foreach (var (id, sha, path) in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastId = id;
                long? hash = null;
                try
                {
                    var full = ResolvePath(path);
                    if (full is not null) hash = await PerceptualHash.ComputeAsync(full, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "Perceptual hash skipped card {CardId}.", id);
                }
                if (hash is null) skipped++; else hashed++;
                // A missing hash is stored too, so an unreadable preview is not retried until the file changes.
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO card_perceptual_hashes (card_id, content_sha256, hash, computed_at_utc)
                    SELECT id, @Sha, @Hash, UTC_TIMESTAMP(6) FROM cards WHERE id = @Id AND content_sha256 = @Sha
                    ON DUPLICATE KEY UPDATE content_sha256 = VALUES(content_sha256), hash = VALUES(hash),
                        computed_at_utc = VALUES(computed_at_utc);
                    """, new { Id = id, Sha = sha, Hash = hash }, cancellationToken: cancellationToken));
            }
        }
    }

    private string? ResolvePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var root = Path.GetFullPath(_appDataRoot);
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var fromRoot = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(fromRoot) || fromRoot == ".." ||
            fromRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || !File.Exists(full))
            return null;
        return full;
    }
}

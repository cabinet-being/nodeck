using Dapper;
using MyApp.Api.Catalog;
using MySqlConnector;

namespace MyApp.Api.Analysis;

public sealed record AnalysisJobLease(long CardId, string Revision, string LeaseToken, int Attempts);
public sealed record AnalysisJobStatus(string State, int Attempts, DateTime AvailableAtUtc, string? LastError);

/// <summary>Durable leases and transactional publication of image analysis results.</summary>
public sealed class AnalysisQueueRepository
{
    private const int MaxAttempts = 5;
    private readonly string _connectionString;

    public AnalysisQueueRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
    }

    public async Task<MySqlConnection> OpenLockConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public static async Task<bool> TryAcquireInferenceLockAsync(MySqlConnection connection,
        CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT GET_LOCK('nodeck:image-analysis-inference', 0);",
            cancellationToken: cancellationToken)) == 1;

    public static async Task ReleaseInferenceLockAsync(MySqlConnection connection) =>
        _ = await connection.ExecuteScalarAsync<int?>(
            "SELECT RELEASE_LOCK('nodeck:image-analysis-inference');");

    public async Task<bool> IsVocabularyReadyAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM danbooru_import_state
            WHERE kind IN ('tags', 'wd_vocabulary') AND completed = TRUE;
            """, cancellationToken: cancellationToken)) > 0;
    }

    public async Task<AnalysisJobStatus?> GetStatusAsync(long cardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<AnalysisJobStatus>(new CommandDefinition("""
            SELECT state, attempts, available_at_utc AS AvailableAtUtc,
                last_error AS LastError
            FROM card_image_analysis_jobs WHERE card_id = @CardId;
            """, new { CardId = cardId }, cancellationToken: cancellationToken));
    }

    public async Task<AnalysisJobLease?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition("""
            SELECT j.card_id AS CardId, j.revision, j.attempts
            FROM card_image_analysis_jobs j
            LEFT JOIN cards c ON c.id = j.card_id
            WHERE (j.state = 'pending' AND j.available_at_utc <= UTC_TIMESTAMP(6))
               OR (j.state = 'leased' AND j.lease_until_utc <= UTC_TIMESTAMP(6))
            -- Background backfill jobs (priority 1) wait for every regular job. Still images take
            -- about a second, videos and GIFs minutes (many frames): analyse images first so a bulk
            -- import gets tags quickly.
            ORDER BY j.priority, CASE
                    WHEN JSON_UNQUOTE(JSON_EXTRACT(c.metadata, '$.media_type')) = 'image'
                     AND COALESCE(JSON_UNQUOTE(JSON_EXTRACT(c.metadata, '$.mime_type')), '') <> 'image/gif'
                    THEN 0 ELSE 1 END,
                j.available_at_utc, j.card_id
            LIMIT 1 FOR UPDATE OF j SKIP LOCKED;
            """, transaction: transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (row.Attempts >= MaxAttempts)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE card_image_analysis_jobs SET state = 'failed', lease_token = NULL,
                    lease_until_utc = NULL, last_error = 'Analysis attempts exhausted.'
                WHERE card_id = @CardId;
                """, new { row.CardId }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var token = Guid.NewGuid().ToString();
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE card_image_analysis_jobs
            SET state = 'leased', attempts = attempts + 1, lease_token = @Token,
                lease_until_utc = UTC_TIMESTAMP(6) + INTERVAL 5 MINUTE, last_error = NULL
            WHERE card_id = @CardId;
            """, new { row.CardId, Token = token }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new AnalysisJobLease(row.CardId, row.Revision, token, row.Attempts + 1);
    }

    public async Task<bool> CompleteAsync(AnalysisJobLease lease, PreparedCardImageAnalysis prepared,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Card writers lock cards before jobs. Keep this order to avoid deadlocks.
        var metadata = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT metadata FROM cards WHERE id = @CardId FOR UPDATE;",
            new { lease.CardId }, transaction, cancellationToken: cancellationToken));
        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition("""
            SELECT card_id AS CardId, revision, state, lease_token AS LeaseToken
            FROM card_image_analysis_jobs WHERE card_id = @CardId FOR UPDATE;
            """, new { lease.CardId }, transaction, cancellationToken: cancellationToken));
        if (metadata is null || metadata != prepared.Metadata || row is null ||
            row.Revision != lease.Revision || row.State != "leased" || row.LeaseToken != lease.LeaseToken)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await CardCatalogRepository.UpsertSuggestionsAsync(connection, transaction, lease.CardId,
            prepared.Proposals, cancellationToken);
        if (prepared.Features is not null && prepared.FeatureModelKey is not null)
            await LearnedTags.LearnedTagRepository.SaveFeaturesAsync(connection, transaction, lease.CardId,
                lease.Revision, prepared.FeatureModelKey, prepared.Features, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE card_image_analysis_jobs SET state = 'done', lease_token = NULL,
                lease_until_utc = NULL, last_error = NULL WHERE card_id = @CardId;
            """, new { lease.CardId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> FailAsync(AnalysisJobLease lease, Exception error,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var delaySeconds = Math.Min(3600, 30 * (1 << Math.Min(lease.Attempts - 1, 6)));
        var message = error.ToString();
        if (message.Length > 512) message = message[..512];
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE card_image_analysis_jobs
            SET state = @State, available_at_utc = UTC_TIMESTAMP(6) + INTERVAL @DelaySeconds SECOND,
                lease_token = NULL, lease_until_utc = NULL, last_error = @Message
            WHERE card_id = @CardId AND revision = @Revision AND state = 'leased'
              AND lease_token = @LeaseToken;
            """, new { lease.CardId, lease.Revision, lease.LeaseToken,
                State = lease.Attempts >= MaxAttempts ? "failed" : "pending",
                DelaySeconds = delaySeconds, Message = message }, cancellationToken: cancellationToken)) == 1;
    }

    private sealed class JobRow
    {
        public long CardId { get; set; }
        public string Revision { get; set; } = "";
        public string State { get; set; } = "";
        public string? LeaseToken { get; set; }
        public int Attempts { get; set; }
    }
}

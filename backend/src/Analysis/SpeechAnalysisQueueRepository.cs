using System.Text;
using System.Text.Json;
using Dapper;
using MyApp.Api.Catalog;
using MySqlConnector;

namespace MyApp.Api.Analysis;

public sealed record SpeechAnalysisJobLease(long CardId, string Revision, string LeaseToken, int Attempts);

public sealed record SpeechAnalysisJobStatus(
    string State, int Attempts, DateTime AvailableAtUtc, string? LastError,
    bool? SpeechDetected, bool? EnglishSpeechDetected, string? Language,
    decimal? ProcessedSeconds, bool Truncated, string? ModelKey);

public sealed record PreparedCardSpeechAnalysis(
    string Metadata, bool? SpeechDetected, bool? EnglishSpeechDetected,
    string? Language, decimal? ProcessedSeconds, bool Truncated,
    string ModelKey, string? EvidenceJson,
    IReadOnlyList<TagSuggestionProposal>? Proposals = null);

/// <summary>Durable speech-analysis leases, independent of visual tagging jobs.</summary>
public sealed class SpeechAnalysisQueueRepository
{
    public const string SpeechProposalModelKey = "speech-exact-phrase-v1";
    private const int MaxAttempts = 5;
    private readonly string _connectionString;

    public SpeechAnalysisQueueRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
    }

    /// <summary>Refreshes the speech job within the card writer's transaction.</summary>
    public static async Task RefreshAsync(MySqlConnection connection, MySqlTransaction transaction,
        long cardId, bool analyzeAudio, CancellationToken cancellationToken = default)
    {
        if (cardId <= 0) throw new ArgumentOutOfRangeException(nameof(cardId));
        await RemovePendingSpeechAssignmentsAsync(connection, transaction, cardId, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM card_tag_suggestions
            WHERE card_id = @CardId AND state = 'pending' AND suggestion_source = 'speech';
            """, new { CardId = cardId }, transaction, cancellationToken: cancellationToken));
        if (!analyzeAudio)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM card_speech_analysis_jobs WHERE card_id = @CardId;",
                new { CardId = cardId }, transaction, cancellationToken: cancellationToken));
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO card_speech_analysis_jobs
                (card_id, revision, state, attempts, available_at_utc,
                 lease_token, lease_until_utc, last_error, speech_detected,
                 english_speech_detected, language, processed_seconds,
                 truncated, model_key, evidence_json)
            VALUES (@CardId, @Revision, 'pending', 0, UTC_TIMESTAMP(6),
                NULL, NULL, NULL, NULL, NULL, NULL, NULL, FALSE, NULL, NULL)
            ON DUPLICATE KEY UPDATE
                revision = VALUES(revision), state = 'pending', attempts = 0,
                available_at_utc = UTC_TIMESTAMP(6), lease_token = NULL,
                lease_until_utc = NULL, last_error = NULL, speech_detected = NULL,
                english_speech_detected = NULL, language = NULL,
                processed_seconds = NULL, truncated = FALSE, model_key = NULL,
                evidence_json = NULL;
            """, new { CardId = cardId, Revision = Guid.NewGuid().ToString("D") },
            transaction, cancellationToken: cancellationToken));
    }

    public async Task<SpeechAnalysisJobStatus?> GetStatusAsync(long cardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<SpeechAnalysisJobStatus>(new CommandDefinition("""
            SELECT state, attempts, available_at_utc AS AvailableAtUtc,
                last_error AS LastError, speech_detected AS SpeechDetected,
                english_speech_detected AS EnglishSpeechDetected, language,
                processed_seconds AS ProcessedSeconds, truncated, model_key AS ModelKey
            FROM card_speech_analysis_jobs WHERE card_id = @CardId;
            """, new { CardId = cardId }, cancellationToken: cancellationToken));
    }

    public async Task<SpeechAnalysisJobLease?> ClaimAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition("""
            SELECT card_id AS CardId, revision, attempts
            FROM card_speech_analysis_jobs
            WHERE (state = 'pending' AND available_at_utc <= UTC_TIMESTAMP(6))
               OR (state = 'leased' AND lease_until_utc <= UTC_TIMESTAMP(6))
            ORDER BY available_at_utc, card_id LIMIT 1 FOR UPDATE SKIP LOCKED;
            """, transaction: transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (row.Attempts >= MaxAttempts)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE card_speech_analysis_jobs SET state = 'failed', lease_token = NULL,
                    lease_until_utc = NULL, last_error = 'Speech analysis attempts exhausted.'
                WHERE card_id = @CardId;
                """, new { row.CardId }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var token = Guid.NewGuid().ToString("D");
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE card_speech_analysis_jobs
            SET state = 'leased', attempts = attempts + 1, lease_token = @Token,
                lease_until_utc = UTC_TIMESTAMP(6) + INTERVAL 90 SECOND,
                last_error = NULL
            WHERE card_id = @CardId;
            """, new { row.CardId, Token = token }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new SpeechAnalysisJobLease(row.CardId, row.Revision, token, row.Attempts + 1);
    }

    public async Task<bool> RenewAsync(SpeechAnalysisJobLease lease,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE card_speech_analysis_jobs
            SET lease_until_utc = UTC_TIMESTAMP(6) + INTERVAL 90 SECOND
            WHERE card_id = @CardId AND revision = @Revision AND state = 'leased'
              AND lease_token = @LeaseToken AND lease_until_utc > UTC_TIMESTAMP(6);
            """, new { lease.CardId, lease.Revision, lease.LeaseToken },
            cancellationToken: cancellationToken)) == 1;
    }

    public async Task<bool> CompleteAsync(SpeechAnalysisJobLease lease,
        PreparedCardSpeechAnalysis prepared, CancellationToken cancellationToken = default)
    {
        ValidatePrepared(prepared);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Card writers lock cards before jobs; use that order for publication too.
        var metadata = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT metadata FROM cards WHERE id = @CardId FOR UPDATE;",
            new { lease.CardId }, transaction, cancellationToken: cancellationToken));
        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition("""
            SELECT card_id AS CardId, revision, state, lease_token AS LeaseToken,
                lease_until_utc > UTC_TIMESTAMP(6) AS LeaseIsValid
            FROM card_speech_analysis_jobs WHERE card_id = @CardId FOR UPDATE;
            """, new { lease.CardId }, transaction, cancellationToken: cancellationToken));
        if (metadata is null || metadata != prepared.Metadata || row is null ||
            row.Revision != lease.Revision || row.State != "leased" ||
            row.LeaseToken != lease.LeaseToken || !row.LeaseIsValid)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await PublishSpeechSuggestionsAsync(connection, transaction, lease.CardId,
            prepared.Proposals ?? [], cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE card_speech_analysis_jobs
            SET state = 'done', lease_token = NULL, lease_until_utc = NULL,
                last_error = NULL, speech_detected = @SpeechDetected,
                english_speech_detected = @EnglishSpeechDetected,
                language = @Language, processed_seconds = @ProcessedSeconds,
                truncated = @Truncated, model_key = @ModelKey,
                evidence_json = @EvidenceJson
            WHERE card_id = @CardId;
            """, new { lease.CardId, prepared.SpeechDetected,
                prepared.EnglishSpeechDetected, prepared.Language,
                prepared.ProcessedSeconds, prepared.Truncated,
                prepared.ModelKey, prepared.EvidenceJson }, transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task PublishSpeechSuggestionsAsync(MySqlConnection connection,
        MySqlTransaction transaction, long cardId,
        IReadOnlyList<TagSuggestionProposal> proposals, CancellationToken cancellationToken)
    {
        await RemovePendingSpeechAssignmentsAsync(connection, transaction, cardId, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM card_tag_suggestions
            WHERE card_id = @CardId AND state = 'pending' AND suggestion_source = 'speech';
            """, new { CardId = cardId }, transaction, cancellationToken: cancellationToken));
        if (proposals.Count == 0) return;
        var ids = proposals.Select(item => item.TagId).ToArray();
        var validIds = (await connection.QueryAsync<long>(new CommandDefinition("""
            SELECT t.id FROM tags t
            WHERE t.id IN @Ids AND t.legacy_card_id IS NULL AND t.name_conflict = FALSE
              AND ((t.origin = 'danbooru' AND t.category = 'general') OR
                   (t.origin = 'user' AND t.category = 'custom' AND EXISTS (
                       SELECT 1 FROM card_tags ct WHERE ct.tag_id = t.id
                         AND ct.assignment_source IN ('manual', 'confirmed'))));
            """, new { Ids = ids }, transaction, cancellationToken: cancellationToken))).ToHashSet();
        foreach (var proposal in proposals.Where(item => validIds.Contains(item.TagId)))
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO card_tag_suggestions
                    (card_id, tag_id, state, confidence, model_key, suggestion_source,
                     created_at_utc, updated_at_utc)
                SELECT @CardId, @TagId, 'pending', @Confidence, @ModelKey, 'speech',
                       UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
                WHERE NOT EXISTS (
                    SELECT 1 FROM card_tags WHERE card_id = @CardId AND tag_id = @TagId)
                ON DUPLICATE KEY UPDATE
                    confidence = IF(state = 'pending' AND suggestion_source = 'speech',
                        VALUES(confidence), confidence),
                    model_key = IF(state = 'pending' AND suggestion_source = 'speech',
                        VALUES(model_key), model_key),
                    updated_at_utc = IF(state = 'pending' AND suggestion_source = 'speech',
                        UTC_TIMESTAMP(6), updated_at_utc);
                """, new { CardId = cardId, proposal.TagId, proposal.Confidence,
                    proposal.ModelKey }, transaction, cancellationToken: cancellationToken));
        }
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO card_tags (card_id, tag_id, assignment_source, confidence)
            SELECT s.card_id, s.tag_id, 'auto', s.confidence
            FROM card_tag_suggestions s
            WHERE s.card_id = @CardId AND s.state = 'pending' AND s.suggestion_source = 'speech'
              AND NOT EXISTS (SELECT 1 FROM card_tags ct
                  WHERE ct.card_id = s.card_id AND ct.tag_id = s.tag_id);
            """, new { CardId = cardId }, transaction, cancellationToken: cancellationToken));
    }

    private static async Task RemovePendingSpeechAssignmentsAsync(MySqlConnection connection,
        MySqlTransaction transaction, long cardId, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE ct FROM card_tags ct
            JOIN card_tag_suggestions s ON s.card_id = ct.card_id AND s.tag_id = ct.tag_id
            WHERE s.card_id = @CardId AND s.state = 'pending'
              AND s.suggestion_source = 'speech' AND ct.assignment_source = 'auto';
            """, new { CardId = cardId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<bool> FailAsync(SpeechAnalysisJobLease lease, Exception error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var delaySeconds = Math.Min(3600, 30 * (1 << Math.Min(lease.Attempts - 1, 6)));
        // Process errors may contain recognized words. Persist only the exception type.
        var message = error.GetType().Name;
        if (message.Length > 512) message = message[..512];
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE card_speech_analysis_jobs
            SET state = @State, available_at_utc = UTC_TIMESTAMP(6) + INTERVAL @DelaySeconds SECOND,
                lease_token = NULL, lease_until_utc = NULL, last_error = @Message
            WHERE card_id = @CardId AND revision = @Revision AND state = 'leased'
              AND lease_token = @LeaseToken AND lease_until_utc > UTC_TIMESTAMP(6);
            """, new { lease.CardId, lease.Revision, lease.LeaseToken,
                State = lease.Attempts >= MaxAttempts ? "failed" : "pending",
                DelaySeconds = delaySeconds, Message = message },
            cancellationToken: cancellationToken)) == 1;
    }

    private static void ValidatePrepared(PreparedCardSpeechAnalysis prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (string.IsNullOrWhiteSpace(prepared.Metadata))
            throw new ArgumentException("Card metadata is required.", nameof(prepared));
        if (prepared.EnglishSpeechDetected == true && prepared.SpeechDetected != true)
            throw new ArgumentException("English speech requires detected speech.", nameof(prepared));
        if (prepared.Language is { Length: > 16 } ||
            prepared.ProcessedSeconds is < 0 or > 600 ||
            string.IsNullOrWhiteSpace(prepared.ModelKey) || prepared.ModelKey.Length > 128)
            throw new ArgumentException("Speech analysis result is invalid.", nameof(prepared));
        var proposals = prepared.Proposals ?? [];
        if ((proposals.Count > 0 && prepared.EnglishSpeechDetected != true) ||
            proposals.Count > 10 ||
            proposals.Any(item => item.TagId <= 0 || item.Confidence is < 0 or > 1 ||
                item.ModelKey != SpeechProposalModelKey) ||
            proposals.Select(item => item.TagId).Distinct().Count() != proposals.Count)
            throw new ArgumentException("Speech tag proposals are invalid.", nameof(prepared));
        if (prepared.EvidenceJson is null) return;
        if (Encoding.UTF8.GetByteCount(prepared.EvidenceJson) > 16_384)
            throw new ArgumentException("Speech evidence exceeds its size limit.", nameof(prepared));
        using var document = JsonDocument.Parse(prepared.EvidenceJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() > 100)
            throw new ArgumentException("Speech evidence must be a bounded array.", nameof(prepared));
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                item.EnumerateObject().Any(property => property.Name is not
                    ("tag_id" or "rule_id" or "start" or "end")))
                throw new ArgumentException("Speech evidence has an unexpected field.", nameof(prepared));
            if (!item.TryGetProperty("tag_id", out var tagId) || tagId.ValueKind != JsonValueKind.Number ||
                !tagId.TryGetInt64(out var id) || id <= 0 ||
                !item.TryGetProperty("rule_id", out var ruleId) || ruleId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(ruleId.GetString()) || ruleId.GetString()!.Length > 128 ||
                !item.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Number ||
                !start.TryGetDecimal(out var from) || from < 0 ||
                !item.TryGetProperty("end", out var end) || end.ValueKind != JsonValueKind.Number ||
                !end.TryGetDecimal(out var to) || to < from)
                throw new ArgumentException("Speech evidence item is invalid.", nameof(prepared));
        }
    }

    private sealed class JobRow
    {
        public long CardId { get; set; }
        public string Revision { get; set; } = "";
        public string State { get; set; } = "";
        public string? LeaseToken { get; set; }
        public bool LeaseIsValid { get; set; }
        public int Attempts { get; set; }
    }
}

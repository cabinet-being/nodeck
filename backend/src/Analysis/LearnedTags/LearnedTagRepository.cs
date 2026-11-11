using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;

namespace MyApp.Api.Analysis.LearnedTags;

public sealed record LearnedTagTrainingSummary(long Generation, int FeatureCards, int TagsTrained, int TagsEnabled);
public sealed record LearnedTagModelStatus(long TagId, string Name, int Positives, int Negatives,
    int HardNegatives, decimal? Threshold, decimal? Precision, decimal? Recall, bool Enabled, DateTime TrainedAtUtc);
public sealed record LearnedTagStatus(long Generation, DateTime? TrainedAtUtc, string? FeatureModelKey,
    long FeatureCards, long PendingCards, IReadOnlyList<LearnedTagModelStatus> Models);

/// <summary>
/// Learns visual suggestions for custom tags from cards that already carry them. Features are the tagger's
/// sparse probability vectors stored per analysis revision; proposals go through card_tag_suggestions with
/// suggestion_source 'learned', so review, Accept all and the decision log work as for other proposals.
/// </summary>
public sealed class LearnedTagRepository
{
    public const string ProposalModelPrefix = "learned-lr-v1";
    public const int MaxProposalsPerCard = 10;
    public const int NegativeSampleLimit = 3000;

    // A custom tag that may be learned, the same rule speech matching uses for custom tags.
    private const string EligibleTag = """
        t.origin = 'user' AND t.category = 'custom' AND t.legacy_card_id IS NULL AND t.name_conflict = FALSE
        """;

    private readonly string _connectionString;

    public LearnedTagRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
    }

    public static string ProposalModelKey(long generation) => $"{ProposalModelPrefix}:g{generation}";

    /// <summary>Stores the features of one analysed revision; a new revision must be scored again.</summary>
    public static Task SaveFeaturesAsync(MySqlConnection connection, MySqlTransaction transaction, long cardId,
        string revision, string modelKey, VisualFeatureVector features, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelKey) || modelKey.Length > 128)
            throw new ArgumentException("A feature model key of at most 128 characters is required.", nameof(modelKey));
        return connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO card_visual_features
                (card_id, revision, model_key, dimension, features, scored_generation, updated_at_utc)
            VALUES (@CardId, @Revision, @ModelKey, @Dimension, @Features, NULL, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE revision = VALUES(revision), model_key = VALUES(model_key),
                dimension = VALUES(dimension), features = VALUES(features), scored_generation = NULL,
                updated_at_utc = VALUES(updated_at_utc);
            """, new { CardId = cardId, Revision = revision, ModelKey = modelKey, features.Dimension,
                Features = features.ToBytes() }, transaction, cancellationToken: cancellationToken));
    }

    /// <summary>Changes whenever examples, decisions, or current features change.</summary>
    public async Task<string> ComputeFingerprintAsync(string featureModelKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var parts = await connection.QuerySingleAsync<FingerprintRow>(new CommandDefinition($"""
            SELECT
                (SELECT COUNT(*) FROM card_tags ct JOIN tags t ON t.id = ct.tag_id
                 WHERE {EligibleTag} AND ct.assignment_source IN ('manual', 'confirmed')) AS Positives,
                (SELECT COALESCE(SUM(ct.id), 0) FROM card_tags ct JOIN tags t ON t.id = ct.tag_id
                 WHERE {EligibleTag} AND ct.assignment_source IN ('manual', 'confirmed')) AS PositiveIds,
                (SELECT COUNT(*) FROM card_tag_suggestions s JOIN tags t ON t.id = s.tag_id
                 WHERE {EligibleTag} AND s.state = 'rejected') AS Rejected,
                (SELECT COALESCE(MAX(id), 0) FROM card_tag_decisions) AS LastDecision,
                (SELECT COUNT(*) FROM card_visual_features WHERE model_key = @ModelKey) AS Features,
                (SELECT COALESCE(DATE_FORMAT(MAX(updated_at_utc), '%Y%m%d%H%i%s%f'), '')
                 FROM card_visual_features WHERE model_key = @ModelKey) AS LastFeature;
            """, new { ModelKey = featureModelKey }, cancellationToken: cancellationToken));
        var text = string.Join('|', featureModelKey, parts.Positives, parts.PositiveIds, parts.Rejected,
            parts.LastDecision, parts.Features, parts.LastFeature);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public async Task<TrainingState?> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TrainingState>(new CommandDefinition("""
            SELECT generation, feature_model_key AS FeatureModelKey, fingerprint,
                trained_at_utc AS TrainedAtUtc
            FROM learned_tag_state WHERE id = 1;
            """, cancellationToken: cancellationToken));
    }

    /// <summary>Retrains every eligible custom tag and starts a new generation, which rescoring then publishes.</summary>
    public async Task<LearnedTagTrainingSummary> TrainAsync(string featureModelKey,
        CancellationToken cancellationToken = default)
    {
        // Taken first, so changes made while training lead to another run.
        var fingerprint = await ComputeFingerprintAsync(featureModelKey, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var features = await LoadCurrentFeaturesAsync(connection, featureModelKey, null, cancellationToken);
        var dimension = features.Count == 0 ? 0 : features.Values.GroupBy(item => item.Dimension)
            .OrderByDescending(group => group.Count()).First().Key;
        foreach (var id in features.Where(item => item.Value.Dimension != dimension).Select(item => item.Key).ToArray())
            features.Remove(id);

        var positives = await PairsAsync(connection, $"""
            SELECT ct.tag_id AS TagId, ct.card_id AS CardId FROM card_tags ct JOIN tags t ON t.id = ct.tag_id
            WHERE {EligibleTag} AND ct.assignment_source IN ('manual', 'confirmed');
            """, cancellationToken);
        // Unreviewed proposals, and pages of a collection that carries the tag, are neither positive nor negative.
        var unsure = await PairsAsync(connection, $"""
            SELECT ct.tag_id AS TagId, ct.card_id AS CardId FROM card_tags ct JOIN tags t ON t.id = ct.tag_id
            WHERE {EligibleTag} AND ct.assignment_source = 'auto'
            UNION
            SELECT s.tag_id, s.card_id FROM card_tag_suggestions s JOIN tags t ON t.id = s.tag_id
            WHERE {EligibleTag} AND s.state = 'pending'
            UNION
            SELECT ct.tag_id, r.to_card_id FROM card_tags ct JOIN tags t ON t.id = ct.tag_id
            JOIN card_relations r ON r.from_card_id = ct.card_id AND r.relation_type = 'contains'
            WHERE {EligibleTag} AND ct.assignment_source IN ('manual', 'confirmed');
            """, cancellationToken);
        var hardNegatives = await PairsAsync(connection, $"""
            SELECT s.tag_id AS TagId, s.card_id AS CardId FROM card_tag_suggestions s JOIN tags t ON t.id = s.tag_id
            WHERE {EligibleTag} AND s.state = 'rejected'
            UNION
            SELECT d.suggested_tag_id, d.card_id FROM card_tag_decisions d JOIN tags t ON t.id = d.suggested_tag_id
            WHERE {EligibleTag} AND d.decision IN ('rejected', 'corrected');
            """, cancellationToken);

        var featuredIds = features.Keys.Order().ToArray();
        var models = new List<(long TagId, LearnedTagModel Model)>();
        foreach (var (tagId, tagPositives) in positives.OrderBy(item => item.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var positiveIds = tagPositives.Where(features.ContainsKey).ToHashSet();
            if (positiveIds.Count < LearnedTagClassifier.MinPositives) continue;
            var skip = unsure.GetValueOrDefault(tagId) ?? [];
            var hardIds = (hardNegatives.GetValueOrDefault(tagId) ?? [])
                .Where(id => features.ContainsKey(id) && !positiveIds.Contains(id) && !skip.Contains(id))
                .ToHashSet();
            var ordinary = featuredIds.Where(id => !positiveIds.Contains(id) && !hardIds.Contains(id) &&
                !skip.Contains(id)).ToArray();
            var sampled = ordinary;
            if (ordinary.Length > NegativeSampleLimit)
            {
                sampled = (long[])ordinary.Clone();
                new Random(unchecked((int)tagId)).Shuffle(sampled);
                sampled = sampled[..NegativeSampleLimit];
            }
            var examples = positiveIds.Order().Select(id => new LearnedTagExample(features[id], true))
                .Concat(hardIds.Order().Select(id => new LearnedTagExample(features[id], false, true)))
                .Concat(sampled.Order().Select(id => new LearnedTagExample(features[id], false)))
                .ToArray();
            var scale = sampled.Length == 0 ? 1 : (double)ordinary.Length / sampled.Length;
            models.Add((tagId, LearnedTagClassifier.Train(dimension, examples, scale, unchecked((int)tagId))));
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var generation = (await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT generation FROM learned_tag_state WHERE id = 1 FOR UPDATE;",
            transaction: transaction, cancellationToken: cancellationToken)) ?? 0) + 1;
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM learned_tag_models;",
            transaction: transaction, cancellationToken: cancellationToken));
        foreach (var (tagId, model) in models)
            // Tags deleted meanwhile are skipped instead of failing the whole generation.
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO learned_tag_models
                    (tag_id, feature_model_key, dimension, weights, bias, threshold, positives, negatives,
                     hard_negatives, cv_precision, cv_recall, generation, trained_at_utc)
                SELECT @TagId, @ModelKey, @Dimension, @Weights, @Bias, @Threshold, @Positives, @Negatives,
                    @HardNegatives, @Precision, @Recall, @Generation, UTC_TIMESTAMP(6)
                FROM tags WHERE id = @TagId;
                """, new { TagId = tagId, ModelKey = featureModelKey, model.Dimension,
                    Weights = EncodeWeights(model.Weights), model.Bias, model.Threshold, model.Positives,
                    model.Negatives, model.HardNegatives, model.Precision, model.Recall, Generation = generation },
                transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO learned_tag_state (id, generation, feature_model_key, fingerprint, trained_at_utc)
            VALUES (1, @Generation, @ModelKey, @Fingerprint, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE generation = VALUES(generation), feature_model_key = VALUES(feature_model_key),
                fingerprint = VALUES(fingerprint), trained_at_utc = VALUES(trained_at_utc);
            """, new { Generation = generation, ModelKey = featureModelKey, Fingerprint = fingerprint },
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new LearnedTagTrainingSummary(generation, features.Count, models.Count,
            models.Count(item => item.Model.Enabled));
    }

    /// <summary>
    /// Scores up to <paramref name="batchSize"/> cards not yet scored by the current generation and publishes
    /// their learned proposals. Returns the number of cards handled; 0 means everything is current.
    /// </summary>
    public async Task<int> ScoreAsync(string featureModelKey, int batchSize = 200,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var state = await ReadStateAsync(cancellationToken);
        if (state is null) return 0;
        await using var connection = await OpenAsync(cancellationToken);
        var models = (await connection.QueryAsync<ModelRow>(new CommandDefinition("""
            SELECT tag_id AS TagId, dimension, weights, bias, threshold
            FROM learned_tag_models
            WHERE feature_model_key = @ModelKey AND generation = @Generation AND threshold IS NOT NULL;
            """, new { ModelKey = featureModelKey, state.Generation }, cancellationToken: cancellationToken)))
            .Select(row => (row.TagId, Model: new LearnedTagModel(row.Dimension,
                DecodeWeights(row.Dimension, row.Weights), row.Bias, row.Threshold, 0, 0, 0, null, null)))
            .ToArray();
        var cards = await LoadCurrentFeaturesAsync(connection, featureModelKey,
            (state.Generation, batchSize), cancellationToken);
        if (cards.Count == 0) return 0;
        var cardIds = cards.Keys.ToArray();
        var revisions = (await connection.QueryAsync<(long CardId, string Revision)>(new CommandDefinition("""
            SELECT card_id, revision FROM card_visual_features WHERE card_id IN @Ids;
            """, new { Ids = cardIds }, cancellationToken: cancellationToken))).ToDictionary(row => row.CardId,
            row => row.Revision);
        var modelTagIds = models.Select(item => item.TagId).ToArray();
        // Tags already decided on a card (assigned by hand, accepted, or rejected) are not proposed again.
        var decided = modelTagIds.Length == 0 ? new HashSet<(long, long)>() :
            (await connection.QueryAsync<(long CardId, long TagId)>(new CommandDefinition("""
                SELECT card_id, tag_id FROM card_tags
                WHERE card_id IN @Ids AND tag_id IN @TagIds AND assignment_source <> 'auto'
                UNION
                SELECT card_id, tag_id FROM card_tag_suggestions
                WHERE card_id IN @Ids AND tag_id IN @TagIds AND state <> 'pending';
                """, new { Ids = cardIds, TagIds = modelTagIds }, cancellationToken: cancellationToken)))
            .ToHashSet();
        var existing = (await connection.QueryAsync<(long CardId, long TagId)>(new CommandDefinition("""
            SELECT card_id, tag_id FROM card_tag_suggestions
            WHERE card_id IN @Ids AND state = 'pending' AND suggestion_source = 'learned';
            """, new { Ids = cardIds }, cancellationToken: cancellationToken)))
            .ToLookup(row => row.CardId, row => row.TagId);

        var modelKey = ProposalModelKey(state.Generation);
        foreach (var (cardId, features) in cards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proposals = models
                .Where(item => item.Model.Dimension == features.Dimension && !decided.Contains((cardId, item.TagId)))
                .Select(item => (item.TagId, Score: item.Model.Score(features), item.Model.Threshold))
                .Where(item => item.Score >= (double)item.Threshold!.Value)
                .OrderByDescending(item => item.Score).ThenBy(item => item.TagId)
                .Take(MaxProposalsPerCard)
                .Select(item => (item.TagId, Confidence: decimal.Round((decimal)item.Score, 5, MidpointRounding.ToZero)))
                .ToArray();
            if (proposals.Length == 0 && !existing[cardId].Any())
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE card_visual_features SET scored_generation = @Generation
                    WHERE card_id = @CardId AND revision = @Revision;
                    """, new { state.Generation, CardId = cardId, Revision = revisions[cardId] },
                    cancellationToken: cancellationToken));
                continue;
            }
            await PublishAsync(connection, cardId, revisions[cardId], state.Generation, modelKey, proposals,
                cancellationToken);
        }
        return cards.Count;
    }

    private static async Task PublishAsync(MySqlConnection connection, long cardId, string revision,
        long generation, string modelKey, IReadOnlyList<(long TagId, decimal Confidence)> proposals,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Card writers lock cards before jobs; keep that order.
        var cardExists = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM cards WHERE id = @CardId FOR UPDATE;", new { CardId = cardId }, transaction,
            cancellationToken: cancellationToken)) is not null;
        var jobRevision = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT revision FROM card_image_analysis_jobs WHERE card_id = @CardId FOR UPDATE;",
            new { CardId = cardId }, transaction, cancellationToken: cancellationToken));
        var featureRevision = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT revision FROM card_visual_features WHERE card_id = @CardId FOR UPDATE;",
            new { CardId = cardId }, transaction, cancellationToken: cancellationToken));
        if (!cardExists || jobRevision != revision || featureRevision != revision)
        {
            // The media changed after these features were read; its next analysis is scored instead.
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        var keep = proposals.Select(item => item.TagId).ToArray();
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE ct FROM card_tags ct
            JOIN card_tag_suggestions s ON s.card_id = ct.card_id AND s.tag_id = ct.tag_id
            WHERE s.card_id = @CardId AND s.state = 'pending' AND s.suggestion_source = 'learned'
              AND ct.assignment_source = 'auto' AND s.tag_id NOT IN @Keep;
            """, new { CardId = cardId, Keep = keep.Length == 0 ? new[] { 0L } : keep }, transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM card_tag_suggestions
            WHERE card_id = @CardId AND state = 'pending' AND suggestion_source = 'learned'
              AND tag_id NOT IN @Keep;
            """, new { CardId = cardId, Keep = keep.Length == 0 ? new[] { 0L } : keep }, transaction,
            cancellationToken: cancellationToken));
        foreach (var (tagId, confidence) in proposals)
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO card_tag_suggestions
                    (card_id, tag_id, state, confidence, model_key, suggestion_source,
                     created_at_utc, updated_at_utc)
                SELECT @CardId, @TagId, 'pending', @Confidence, @ModelKey, 'learned',
                       UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
                FROM tags WHERE id = @TagId AND NOT EXISTS (
                    SELECT 1 FROM card_tags WHERE card_id = @CardId AND tag_id = @TagId
                      AND assignment_source <> 'auto')
                ON DUPLICATE KEY UPDATE
                    confidence = IF(state = 'pending' AND suggestion_source = 'learned',
                        VALUES(confidence), confidence),
                    model_key = IF(state = 'pending' AND suggestion_source = 'learned',
                        VALUES(model_key), model_key),
                    updated_at_utc = IF(state = 'pending' AND suggestion_source = 'learned',
                        UTC_TIMESTAMP(6), updated_at_utc);
                """, new { CardId = cardId, TagId = tagId, Confidence = confidence, ModelKey = modelKey },
                transaction, cancellationToken: cancellationToken));
        // Like other proposals, a learned tag shows on the card at once as an unreviewed automatic tag.
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO card_tags (card_id, tag_id, assignment_source, confidence)
            SELECT s.card_id, s.tag_id, 'auto', s.confidence
            FROM card_tag_suggestions s
            WHERE s.card_id = @CardId AND s.state = 'pending' AND s.suggestion_source = 'learned'
              AND NOT EXISTS (SELECT 1 FROM card_tags ct
                  WHERE ct.card_id = s.card_id AND ct.tag_id = s.tag_id);
            """, new { CardId = cardId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE card_visual_features SET scored_generation = @Generation
            WHERE card_id = @CardId AND revision = @Revision;
            """, new { Generation = generation, CardId = cardId, Revision = revision }, transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Queues low-priority analysis for media cards without current features, a batch at a time and only when
    /// the previous backfill batch has finished. Regular jobs always run first (see AnalysisQueueRepository).
    /// </summary>
    public async Task<int> EnqueueBackfillAsync(string featureModelKey, int batchSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var connection = await OpenAsync(cancellationToken);
        if (await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT COUNT(*) FROM card_image_analysis_jobs WHERE priority = 1 AND state IN ('pending', 'leased');
            """, cancellationToken: cancellationToken)) > 0)
            return 0;
        var candidates = (await connection.QueryAsync<(long CardId, string? State)>(new CommandDefinition("""
            SELECT c.id, j.state
            FROM cards c
            LEFT JOIN card_image_analysis_jobs j ON j.card_id = c.id
            LEFT JOIN card_visual_features f
              ON f.card_id = c.id AND f.model_key = @ModelKey AND f.revision = j.revision
            WHERE c.type = 'media'
              AND JSON_UNQUOTE(JSON_EXTRACT(c.metadata, '$.media_type')) IN ('image', 'gif', 'video')
              AND (j.card_id IS NULL OR j.state = 'done') AND f.card_id IS NULL
            ORDER BY c.id
            LIMIT @BatchSize;
            """, new { ModelKey = featureModelKey, BatchSize = batchSize },
            cancellationToken: cancellationToken))).ToArray();
        if (candidates.Length == 0) return 0;
        var done = candidates.Where(item => item.State is not null).Select(item => item.CardId).ToArray();
        var queued = 0;
        if (done.Length > 0)
            queued += await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE card_image_analysis_jobs
                SET state = 'pending', attempts = 0, available_at_utc = UTC_TIMESTAMP(6), lease_token = NULL,
                    lease_until_utc = NULL, last_error = NULL, priority = 1
                WHERE card_id IN @Ids AND state = 'done';
                """, new { Ids = done }, cancellationToken: cancellationToken));
        foreach (var cardId in candidates.Where(item => item.State is null).Select(item => item.CardId))
            queued += await connection.ExecuteAsync(new CommandDefinition("""
                INSERT IGNORE INTO card_image_analysis_jobs
                    (card_id, revision, state, attempts, available_at_utc, priority)
                VALUES (@CardId, @Revision, 'pending', 0, UTC_TIMESTAMP(6), 1);
                """, new { CardId = cardId, Revision = Guid.NewGuid().ToString("D") },
                cancellationToken: cancellationToken));
        return queued;
    }

    public async Task<LearnedTagStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var counts = await connection.QuerySingleAsync<(long Cards, long Pending)>(new CommandDefinition("""
            SELECT COUNT(*),
                CAST(COALESCE(SUM(f.scored_generation IS NULL OR f.scored_generation <> @Generation), 0) AS SIGNED)
            FROM card_visual_features f
            JOIN card_image_analysis_jobs j ON j.card_id = f.card_id AND j.revision = f.revision
            WHERE f.model_key = @ModelKey;
            """, new { Generation = state?.Generation ?? 0, ModelKey = state?.FeatureModelKey ?? "" },
            cancellationToken: cancellationToken));
        var models = (await connection.QueryAsync<StatusRow>(new CommandDefinition("""
            SELECT m.tag_id AS TagId, t.name, m.positives, m.negatives, m.hard_negatives AS HardNegatives,
                m.threshold, m.cv_precision AS `Precision`, m.cv_recall AS Recall,
                m.trained_at_utc AS TrainedAtUtc
            FROM learned_tag_models m JOIN tags t ON t.id = m.tag_id
            ORDER BY t.name, m.tag_id;
            """, cancellationToken: cancellationToken)))
            .Select(row => new LearnedTagModelStatus(row.TagId, row.Name, row.Positives, row.Negatives,
                row.HardNegatives, row.Threshold, row.Precision, row.Recall, row.Threshold is not null,
                row.TrainedAtUtc))
            .ToArray();
        return new LearnedTagStatus(state?.Generation ?? 0, state?.TrainedAtUtc, state?.FeatureModelKey,
            counts.Cards, counts.Pending, models);
    }

    /// <summary>Features of each card's current analysis revision; optionally only cards not yet scored.</summary>
    private static async Task<Dictionary<long, VisualFeatureVector>> LoadCurrentFeaturesAsync(
        MySqlConnection connection, string featureModelKey, (long Generation, int Limit)? unscored,
        CancellationToken cancellationToken)
    {
        var filter = unscored is null ? "" : """
            AND (f.scored_generation IS NULL OR f.scored_generation <> @Generation)
            ORDER BY f.card_id LIMIT @Limit
            """;
        var rows = await connection.QueryAsync<FeatureRow>(new CommandDefinition($"""
            SELECT f.card_id AS CardId, f.dimension, f.features
            FROM card_visual_features f
            JOIN card_image_analysis_jobs j ON j.card_id = f.card_id AND j.revision = f.revision
            WHERE f.model_key = @ModelKey
            {filter};
            """, new { ModelKey = featureModelKey, unscored?.Generation, unscored?.Limit },
            cancellationToken: cancellationToken));
        return rows.ToDictionary(row => row.CardId, row => VisualFeatureVector.FromBytes(row.Dimension, row.Features));
    }

    private static async Task<Dictionary<long, HashSet<long>>> PairsAsync(MySqlConnection connection, string sql,
        CancellationToken cancellationToken) =>
        (await connection.QueryAsync<(long TagId, long CardId)>(new CommandDefinition(sql,
            cancellationToken: cancellationToken)))
        .GroupBy(row => row.TagId)
        .ToDictionary(group => group.Key, group => group.Select(row => row.CardId).ToHashSet());

    /// <summary>Little-endian pairs of int32 index and float32 weight, non-zero weights only.</summary>
    private static byte[] EncodeWeights(float[] weights)
    {
        var nonZero = Enumerable.Range(0, weights.Length).Where(i => weights[i] != 0).ToArray();
        var bytes = new byte[nonZero.Length * 8];
        for (var k = 0; k < nonZero.Length; k++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(k * 8), nonZero[k]);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(k * 8 + 4), weights[nonZero[k]]);
        }
        return bytes;
    }

    private static float[] DecodeWeights(int dimension, byte[] bytes)
    {
        if (bytes.Length % 8 != 0) throw new InvalidDataException("Invalid learned tag weights.");
        var weights = new float[dimension];
        for (var k = 0; k < bytes.Length / 8; k++)
        {
            var index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(k * 8));
            if (index < 0 || index >= dimension) throw new InvalidDataException("Invalid learned tag weights.");
            weights[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(k * 8 + 4));
        }
        return weights;
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public sealed class TrainingState
    {
        public long Generation { get; set; }
        public string FeatureModelKey { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public DateTime TrainedAtUtc { get; set; }
    }

    private sealed class FingerprintRow
    {
        public long Positives { get; set; }
        public decimal PositiveIds { get; set; }
        public long Rejected { get; set; }
        public long LastDecision { get; set; }
        public long Features { get; set; }
        public string LastFeature { get; set; } = "";
    }

    private sealed class FeatureRow
    {
        public long CardId { get; set; }
        public int Dimension { get; set; }
        public byte[] Features { get; set; } = [];
    }

    private sealed class StatusRow
    {
        public long TagId { get; set; }
        public string Name { get; set; } = "";
        public int Positives { get; set; }
        public int Negatives { get; set; }
        public int HardNegatives { get; set; }
        public decimal? Threshold { get; set; }
        public decimal? Precision { get; set; }
        public decimal? Recall { get; set; }
        public DateTime TrainedAtUtc { get; set; }
    }

    private sealed class ModelRow
    {
        public long TagId { get; set; }
        public int Dimension { get; set; }
        public byte[] Weights { get; set; } = [];
        public double Bias { get; set; }
        public decimal? Threshold { get; set; }
    }
}

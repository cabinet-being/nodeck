using Dapper;
using MySqlConnector;

namespace MyApp.Api.Catalog;

public sealed class CardCatalogRepository
{
    private readonly string _connectionString;

    public CardCatalogRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
    }

    /// <summary>Activates proposals made before automatic tag assignment was enabled.</summary>
    public async Task<int> ActivatePendingSuggestionsAsync()
    {
        await using var connection = await OpenAsync();
        const string lockName = "nodeck:auto-tag-activation";
        if (await connection.ExecuteScalarAsync<int?>("SELECT GET_LOCK(@LockName, 30);",
            new { LockName = lockName }) != 1)
            throw new InvalidOperationException("Could not acquire the automatic tag activation lock.");
        try
        {
            await using var transaction = await connection.BeginTransactionAsync();
            var inserted = await connection.ExecuteAsync("""
                INSERT INTO card_tags (card_id, tag_id, assignment_source, confidence)
                SELECT s.card_id, s.tag_id, 'auto', s.confidence
                FROM card_tag_suggestions s
                WHERE s.state = 'pending'
                  AND NOT EXISTS (SELECT 1 FROM card_tags ct
                      WHERE ct.card_id = s.card_id AND ct.tag_id = s.tag_id);
                """, transaction: transaction);
            await transaction.CommitAsync();
            return inserted;
        }
        finally
        {
            await connection.ExecuteAsync("SELECT RELEASE_LOCK(@LockName);", new { LockName = lockName });
        }
    }

    public async Task<IReadOnlyList<CardTagEntry>> ListTagsAsync(long cardId)
    {
        await using var connection = await OpenAsync();
        await RequireCardAsync(connection, cardId);
        return (await connection.QueryAsync<CardTagEntry>("""
            SELECT t.id, t.name, t.category, t.origin,
                   COALESCE((SELECT ct.assignment_source FROM card_tags ct
                             WHERE ct.card_id = @CardId AND ct.tag_id = t.id
                               AND ct.legacy_relation_id IS NULL
                             ORDER BY ct.id LIMIT 1), 'manual') AS AssignmentSource
            FROM tags t
            WHERE EXISTS (
                SELECT 1 FROM card_tags ct
                WHERE ct.card_id = @CardId AND ct.tag_id = t.id AND ct.legacy_relation_id IS NULL
            ) OR EXISTS (
                SELECT 1 FROM card_relations r
                WHERE r.from_card_id = @CardId AND r.relation_type = 'tagged_with'
                  AND r.to_card_id = t.legacy_card_id
            )
            ORDER BY t.name, t.id;
            """, new { CardId = cardId })).ToList();
    }

    public async Task SetTagAsync(long cardId, long tagId)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RequireContentCardAsync(connection, transaction, cardId);
        var legacyCardId = await connection.ExecuteScalarAsync<long?>(
            "SELECT legacy_card_id FROM tags WHERE id = @TagId;", new { TagId = tagId }, transaction);
        if (legacyCardId is null)
        {
            if (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tags WHERE id = @TagId;",
                new { TagId = tagId }, transaction) == 0)
                throw new CatalogMissingException("Tag not found.");
            var count = await connection.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM card_tags
                WHERE card_id = @CardId AND tag_id = @TagId AND legacy_relation_id IS NULL;
                """, new { CardId = cardId, TagId = tagId }, transaction);
            if (count == 0)
                await connection.ExecuteAsync("""
                    INSERT INTO card_tags (card_id, tag_id, assignment_source)
                    VALUES (@CardId, @TagId, 'manual');
                    """, new { CardId = cardId, TagId = tagId }, transaction);
            else
                await connection.ExecuteAsync("""
                    UPDATE card_tags SET assignment_source = 'manual', confidence = NULL
                    WHERE card_id = @CardId AND tag_id = @TagId AND legacy_relation_id IS NULL;
                    """, new { CardId = cardId, TagId = tagId }, transaction);
        }
        else
        {
            var relationId = await connection.ExecuteScalarAsync<long?>("""
                SELECT id FROM card_relations
                WHERE from_card_id = @CardId AND to_card_id = @LegacyCardId
                  AND relation_type = 'tagged_with' LIMIT 1;
                """, new { CardId = cardId, LegacyCardId = legacyCardId }, transaction);
            if (relationId is null)
                relationId = await connection.ExecuteScalarAsync<long>("""
                    INSERT INTO card_relations (from_card_id, to_card_id, relation_type, metadata)
                    VALUES (@CardId, @LegacyCardId, 'tagged_with', JSON_OBJECT('created_at',
                        DATE_FORMAT(UTC_TIMESTAMP(), '%Y-%m-%dT%H:%i:%sZ')));
                    SELECT LAST_INSERT_ID();
                    """, new { CardId = cardId, LegacyCardId = legacyCardId }, transaction);
            await connection.ExecuteAsync("""
                INSERT INTO card_tags (card_id, tag_id, assignment_source, legacy_relation_id)
                VALUES (@CardId, @TagId, 'manual', @RelationId)
                ON DUPLICATE KEY UPDATE card_id = VALUES(card_id), tag_id = VALUES(tag_id),
                    assignment_source = 'manual', confidence = NULL;
                """, new { CardId = cardId, TagId = tagId, RelationId = relationId }, transaction);
        }
        await connection.ExecuteAsync("""
            DELETE FROM card_tag_suggestions WHERE card_id = @CardId AND tag_id = @TagId;
            """, new { CardId = cardId, TagId = tagId }, transaction);
        await transaction.CommitAsync();
    }

    public async Task<bool> DeleteTagAsync(long cardId, long tagId)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RequireCardAsync(connection, cardId, transaction);
        var legacyCardId = await connection.ExecuteScalarAsync<long?>(
            "SELECT legacy_card_id FROM tags WHERE id = @TagId;", new { TagId = tagId }, transaction);
        int affected;
        if (legacyCardId is null)
            affected = await connection.ExecuteAsync("""
                DELETE FROM card_tags WHERE card_id = @CardId AND tag_id = @TagId AND legacy_relation_id IS NULL;
                """, new { CardId = cardId, TagId = tagId }, transaction);
        else
        {
            await connection.ExecuteAsync("""
                DELETE ct FROM card_tags ct JOIN card_relations r ON r.id = ct.legacy_relation_id
                WHERE r.from_card_id = @CardId AND r.to_card_id = @LegacyCardId
                  AND r.relation_type = 'tagged_with';
                """, new { CardId = cardId, LegacyCardId = legacyCardId }, transaction);
            affected = await connection.ExecuteAsync("""
                DELETE FROM card_relations WHERE from_card_id = @CardId AND to_card_id = @LegacyCardId
                    AND relation_type = 'tagged_with';
                """, new { CardId = cardId, LegacyCardId = legacyCardId }, transaction);
        }
        if (affected > 0)
            await connection.ExecuteAsync("""
                INSERT INTO card_tag_suggestions
                    (card_id, tag_id, state, confidence, model_key, created_at_utc, updated_at_utc)
                VALUES (@CardId, @TagId, 'rejected', NULL, NULL, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE state = 'rejected', confidence = NULL,
                    model_key = NULL, updated_at_utc = UTC_TIMESTAMP(6);
                """, new { CardId = cardId, TagId = tagId }, transaction);
        await transaction.CommitAsync();
        return affected > 0;
    }

    public async Task<IReadOnlyList<TagSuggestionEntry>> ListPendingSuggestionsAsync(long cardId)
    {
        await using var connection = await OpenAsync();
        await RequireCardAsync(connection, cardId);
        return (await connection.QueryAsync<TagSuggestionEntry>("""
            SELECT s.tag_id AS TagId, t.name, t.category, t.origin,
                   s.confidence, s.model_key AS ModelKey
            FROM card_tag_suggestions s JOIN tags t ON t.id = s.tag_id
            WHERE s.card_id = @CardId AND s.state = 'pending'
              AND NOT EXISTS (
                  SELECT 1 FROM card_tags ct
                  WHERE ct.card_id = s.card_id AND ct.tag_id = s.tag_id
                    AND ct.assignment_source <> 'auto')
            ORDER BY s.confidence DESC, t.name, t.id LIMIT 200;
            """, new { CardId = cardId })).ToList();
    }

    public async Task UpsertSuggestionsAsync(long cardId, IReadOnlyCollection<TagSuggestionProposal> proposals)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RequireContentCardAsync(connection, transaction, cardId);
        await UpsertSuggestionsAsync(connection, transaction, cardId, proposals);
        await transaction.CommitAsync();
    }

    public static async Task UpsertSuggestionsAsync(MySqlConnection connection, MySqlTransaction transaction,
        long cardId, IReadOnlyCollection<TagSuggestionProposal> proposals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        var items = proposals.ToArray();
        if (items.Any(item => item.TagId <= 0 || item.Confidence is < 0 or > 1 ||
                              string.IsNullOrWhiteSpace(item.ModelKey) || item.ModelKey.Length > 128) ||
            items.Select(item => item.TagId).Distinct().Count() != items.Length)
            throw new CatalogValidationException("Suggestions need distinct tags, confidence from 0 to 1, and a model key.");
        foreach (var item in items)
        {
            var tag = await connection.QuerySingleOrDefaultAsync<TagIdentity>(new CommandDefinition("""
                SELECT id, legacy_card_id AS LegacyCardId FROM tags WHERE id = @TagId;
                """, new { item.TagId }, transaction, cancellationToken: cancellationToken));
            if (tag is null)
                throw new CatalogMissingException($"Tag {item.TagId} not found.");
            if (tag.LegacyCardId is not null)
                throw new CatalogValidationException("Legacy tag cards cannot be proposed by the tagger.");
            if (await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                SELECT COUNT(*) FROM card_tags WHERE card_id = @CardId AND tag_id = @TagId
                  AND assignment_source <> 'auto';
                """, new { CardId = cardId, item.TagId }, transaction, cancellationToken: cancellationToken)) != 0)
                continue;
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO card_tag_suggestions
                    (card_id, tag_id, state, confidence, model_key, suggestion_source,
                     created_at_utc, updated_at_utc)
                VALUES (@CardId, @TagId, 'pending', @Confidence, @ModelKey, 'visual',
                    UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE
                    confidence = IF(state = 'pending', VALUES(confidence), confidence),
                    model_key = IF(state = 'pending', VALUES(model_key), model_key),
                    suggestion_source = IF(state = 'pending', 'visual', suggestion_source),
                    updated_at_utc = IF(state = 'pending', UTC_TIMESTAMP(6), updated_at_utc);
                """, new { CardId = cardId, item.TagId, item.Confidence, item.ModelKey }, transaction,
                cancellationToken: cancellationToken));
        }
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO card_tags (card_id, tag_id, assignment_source, confidence)
            SELECT s.card_id, s.tag_id, 'auto', s.confidence
            FROM card_tag_suggestions s
            WHERE s.card_id = @CardId AND s.state = 'pending'
              AND NOT EXISTS (SELECT 1 FROM card_tags ct
                  WHERE ct.card_id = s.card_id AND ct.tag_id = s.tag_id);
            """, new { CardId = cardId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<bool> DecideSuggestionAsync(long cardId, long tagId, string decision)
    {
        ValidateDecision(decision);
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RequireContentCardAsync(connection, transaction, cardId);
        var suggestion = await connection.QuerySingleOrDefaultAsync<SuggestionState>("""
            SELECT tag_id AS TagId, state, confidence FROM card_tag_suggestions
            WHERE card_id = @CardId AND tag_id = @TagId FOR UPDATE;
            """, new { CardId = cardId, TagId = tagId }, transaction);
        if (suggestion is null) return false;
        if (suggestion.State == decision) return true;
        if (suggestion.State != "pending")
            throw new CatalogConflictException("This suggestion already has a different decision.");
        await ApplyDecisionAsync(connection, transaction, cardId, suggestion, decision);
        await transaction.CommitAsync();
        return true;
    }

    public async Task<int> DecideSuggestionsAsync(long cardId, IReadOnlyCollection<long>? tagIds, string decision)
    {
        ValidateDecision(decision);
        var ids = tagIds?.ToArray();
        if (ids is { Length: 0 or > 200 } || ids is not null &&
            (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length))
            throw new CatalogValidationException("Choose one to 200 distinct tag IDs, or omit tagIds to review all pending tags.");
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RequireContentCardAsync(connection, transaction, cardId);
        var rows = (ids is null
            ? await connection.QueryAsync<SuggestionState>("""
                SELECT tag_id AS TagId, state, confidence FROM card_tag_suggestions
                WHERE card_id = @CardId AND state = 'pending' FOR UPDATE;
                """, new { CardId = cardId }, transaction)
            : await connection.QueryAsync<SuggestionState>("""
                SELECT tag_id AS TagId, state, confidence FROM card_tag_suggestions
                WHERE card_id = @CardId AND tag_id IN @Ids FOR UPDATE;
                """, new { CardId = cardId, Ids = ids }, transaction)).ToArray();
        if (ids is not null && rows.Length != ids.Length)
            throw new CatalogMissingException("One or more tag suggestions were not found.");
        if (rows.Any(row => row.State != "pending" && row.State != decision))
            throw new CatalogConflictException("One or more suggestions already have a different decision.");
        var pending = rows.Where(row => row.State == "pending").ToArray();
        foreach (var row in pending)
            await ApplyDecisionAsync(connection, transaction, cardId, row, decision);
        await transaction.CommitAsync();
        return pending.Length;
    }

    private static void ValidateDecision(string decision)
    {
        if (decision is not ("accepted" or "rejected"))
            throw new CatalogValidationException("Decision must be accepted or rejected.");
    }

    private static async Task ApplyDecisionAsync(MySqlConnection connection, MySqlTransaction transaction,
        long cardId, SuggestionState suggestion, string decision)
    {
        if (decision == "accepted")
        {
            var assignmentSource = await connection.ExecuteScalarAsync<string?>("""
                SELECT assignment_source FROM card_tags
                WHERE card_id = @CardId AND tag_id = @TagId LIMIT 1 FOR UPDATE;
                """, new { CardId = cardId, suggestion.TagId }, transaction);
            if (assignmentSource is null)
                await connection.ExecuteAsync("""
                    INSERT INTO card_tags (card_id, tag_id, assignment_source, confidence)
                    VALUES (@CardId, @TagId, 'confirmed', @Confidence);
                    """, new { CardId = cardId, suggestion.TagId, suggestion.Confidence }, transaction);
            else if (assignmentSource == "auto")
                await connection.ExecuteAsync("""
                    UPDATE card_tags SET assignment_source = 'confirmed', confidence = @Confidence
                    WHERE card_id = @CardId AND tag_id = @TagId AND assignment_source = 'auto';
                    """, new { CardId = cardId, suggestion.TagId, suggestion.Confidence }, transaction);
        }
        else
            await connection.ExecuteAsync("""
                DELETE FROM card_tags WHERE card_id = @CardId AND tag_id = @TagId
                  AND assignment_source = 'auto';
                """, new { CardId = cardId, suggestion.TagId }, transaction);
        await connection.ExecuteAsync("""
            UPDATE card_tag_suggestions SET state = @Decision, updated_at_utc = UTC_TIMESTAMP(6)
            WHERE card_id = @CardId AND tag_id = @TagId;
            """, new { CardId = cardId, suggestion.TagId, Decision = decision }, transaction);
    }

    public async Task<IReadOnlyList<CardSourceEntry>> ListSourcesAsync(long cardId)
    {
        await using var connection = await OpenAsync();
        await RequireCardAsync(connection, cardId);
        return (await connection.QueryAsync<CardSourceEntry>("""
            SELECT s.id, s.name, cs.original_url AS OriginalUrl
            FROM card_sources cs JOIN sources s ON s.id = cs.source_id
            WHERE cs.card_id = @CardId AND cs.legacy_relation_id IS NULL
            UNION ALL
            SELECT s.id, s.name, JSON_UNQUOTE(JSON_EXTRACT(r.properties, '$.original_url')) AS OriginalUrl
            FROM card_relations r JOIN sources s ON s.legacy_card_id = r.to_card_id
            WHERE r.from_card_id = @CardId AND r.relation_type = 'sourced_from'
            ORDER BY name, id;
            """, new { CardId = cardId })).ToList();
    }

    public async Task SetSourceAsync(long cardId, long sourceId, CardSourceRequest request)
    {
        var url = NormalizeUrl(request.OriginalUrl);
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RequireContentCardAsync(connection, transaction, cardId);
        var source = await connection.QuerySingleOrDefaultAsync<SourceIdentity>(
            "SELECT id, legacy_card_id AS LegacyCardId FROM sources WHERE id = @SourceId;",
            new { SourceId = sourceId }, transaction);
        if (source is null) throw new CatalogMissingException("Source not found.");
        if (source.LegacyCardId is not null)
        {
            var relationId = await connection.ExecuteScalarAsync<long?>("""
                SELECT id FROM card_relations
                WHERE from_card_id = @CardId AND to_card_id = @LegacyCardId
                  AND relation_type = 'sourced_from' LIMIT 1;
                """, new { CardId = cardId, LegacyCardId = source.LegacyCardId }, transaction);
            if (relationId is null)
                relationId = await connection.ExecuteScalarAsync<long>("""
                    INSERT INTO card_relations (from_card_id, to_card_id, relation_type, properties, metadata)
                    VALUES (@CardId, @LegacyCardId, 'sourced_from', JSON_OBJECT('original_url', @Url),
                        JSON_OBJECT('created_at', DATE_FORMAT(UTC_TIMESTAMP(), '%Y-%m-%dT%H:%i:%sZ')));
                    SELECT LAST_INSERT_ID();
                    """, new { CardId = cardId, LegacyCardId = source.LegacyCardId, Url = url }, transaction);
            else
                await connection.ExecuteAsync("""
                    UPDATE card_relations SET properties = JSON_SET(COALESCE(properties, JSON_OBJECT()),
                        '$.original_url', @Url) WHERE id = @RelationId;
                    """, new { RelationId = relationId, Url = url }, transaction);
            await connection.ExecuteAsync("""
                INSERT INTO card_sources (card_id, source_id, original_url, legacy_relation_id)
                VALUES (@CardId, @SourceId, @Url, @RelationId)
                ON DUPLICATE KEY UPDATE original_url = VALUES(original_url);
                """, new { CardId = cardId, SourceId = sourceId, Url = url, RelationId = relationId }, transaction);
        }
        else
        {
            var linkId = await connection.ExecuteScalarAsync<long?>("""
                SELECT id FROM card_sources
                WHERE card_id = @CardId AND source_id = @SourceId AND legacy_relation_id IS NULL LIMIT 1;
                """, new { CardId = cardId, SourceId = sourceId }, transaction);
            if (linkId is null)
                await connection.ExecuteAsync("""
                    INSERT INTO card_sources (card_id, source_id, original_url)
                    VALUES (@CardId, @SourceId, @Url);
                    """, new { CardId = cardId, SourceId = sourceId, Url = url }, transaction);
            else
                await connection.ExecuteAsync("UPDATE card_sources SET original_url = @Url WHERE id = @LinkId;",
                    new { LinkId = linkId, Url = url }, transaction);
        }
        await transaction.CommitAsync();
    }

    public async Task<bool> DeleteSourceAsync(long cardId, long sourceId)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RequireCardAsync(connection, cardId, transaction);
        var legacyCardId = await connection.ExecuteScalarAsync<long?>(
            "SELECT legacy_card_id FROM sources WHERE id = @SourceId;", new { SourceId = sourceId }, transaction);
        int affected;
        if (legacyCardId is null)
            affected = await connection.ExecuteAsync("""
                DELETE FROM card_sources WHERE card_id = @CardId AND source_id = @SourceId
                    AND legacy_relation_id IS NULL;
                """, new { CardId = cardId, SourceId = sourceId }, transaction);
        else
        {
            await connection.ExecuteAsync("""
                DELETE cs FROM card_sources cs JOIN card_relations r ON r.id = cs.legacy_relation_id
                WHERE r.from_card_id = @CardId AND r.to_card_id = @LegacyCardId
                  AND r.relation_type = 'sourced_from';
                """, new { CardId = cardId, LegacyCardId = legacyCardId }, transaction);
            affected = await connection.ExecuteAsync("""
                DELETE FROM card_relations WHERE from_card_id = @CardId AND to_card_id = @LegacyCardId
                    AND relation_type = 'sourced_from';
                """, new { CardId = cardId, LegacyCardId = legacyCardId }, transaction);
        }
        await transaction.CommitAsync();
        return affected > 0;
    }

    private async Task<MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task RequireCardAsync(MySqlConnection connection, long cardId,
        MySqlTransaction? transaction = null)
    {
        var sql = transaction is null
            ? "SELECT id FROM cards WHERE id = @CardId;"
            : "SELECT id FROM cards WHERE id = @CardId FOR UPDATE;";
        if (await connection.ExecuteScalarAsync<long?>(sql,
            new { CardId = cardId }, transaction) is null)
            throw new CatalogMissingException("Card not found.");
    }

    private static async Task RequireContentCardAsync(MySqlConnection connection, MySqlTransaction transaction,
        long cardId)
    {
        var type = await connection.ExecuteScalarAsync<string?>("SELECT type FROM cards WHERE id = @CardId FOR UPDATE;",
            new { CardId = cardId }, transaction);
        if (type is null) throw new CatalogMissingException("Card not found.");
        if (type is not ("media" or "comic" or "set"))
            throw new CatalogValidationException("Only media, comic, and set cards can use catalog tags or sources.");
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var url = value.Trim();
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
            throw new CatalogValidationException("Original URL must be an absolute HTTP or HTTPS URL.");
        return url;
    }

    private sealed class SourceIdentity
    {
        public long Id { get; set; }
        public long? LegacyCardId { get; set; }
    }

    private sealed class TagIdentity
    {
        public long Id { get; set; }
        public long? LegacyCardId { get; set; }
    }

    private sealed class SuggestionState
    {
        public long TagId { get; set; }
        public string State { get; set; } = "";
        public decimal? Confidence { get; set; }
    }
}

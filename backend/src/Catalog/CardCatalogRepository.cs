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

    public async Task<IReadOnlyList<CardTagEntry>> ListTagsAsync(long cardId)
    {
        await using var connection = await OpenAsync();
        await RequireCardAsync(connection, cardId);
        return (await connection.QueryAsync<CardTagEntry>("""
            SELECT t.id, t.name, t.category, t.origin FROM tags t
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
                ON DUPLICATE KEY UPDATE card_id = VALUES(card_id), tag_id = VALUES(tag_id);
                """, new { CardId = cardId, TagId = tagId, RelationId = relationId }, transaction);
        }
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
        await transaction.CommitAsync();
        return affected > 0;
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
        if (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM cards WHERE id = @CardId;",
            new { CardId = cardId }, transaction) == 0)
            throw new CatalogMissingException("Card not found.");
    }

    private static async Task RequireContentCardAsync(MySqlConnection connection, MySqlTransaction transaction,
        long cardId)
    {
        var type = await connection.ExecuteScalarAsync<string?>("SELECT type FROM cards WHERE id = @CardId;",
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
}

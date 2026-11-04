using Dapper;
using MySqlConnector;

namespace MyApp.Api.Catalog;

public sealed class CatalogDatabaseInitializer
{
    private readonly string _connectionString;
    private readonly ILogger<CatalogDatabaseInitializer> _logger;

    public CatalogDatabaseInitializer(IConfiguration configuration, ILogger<CatalogDatabaseInitializer> logger)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        // These tables are additive. Legacy cards and relations remain the active API's source of truth.
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS tags (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                name VARCHAR(255) NOT NULL,
                canonical_key VARCHAR(320) NOT NULL,
                category VARCHAR(32) NOT NULL DEFAULT 'general',
                origin VARCHAR(32) NOT NULL DEFAULT 'user',
                danbooru_id BIGINT NULL,
                legacy_card_id BIGINT NULL,
                name_conflict BOOLEAN NOT NULL DEFAULT FALSE,
                UNIQUE KEY uq_tags_key (canonical_key),
                UNIQUE KEY uq_tags_danbooru (danbooru_id),
                UNIQUE KEY uq_tags_legacy (legacy_card_id),
                KEY idx_tags_name (name)
            );
            """);
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS tag_aliases (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                tag_id BIGINT NOT NULL,
                alias VARCHAR(255) NOT NULL,
                UNIQUE KEY uq_tag_alias (alias),
                KEY idx_tag_alias_tag (tag_id),
                CONSTRAINT fk_tag_alias_tag FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
            );
            """);
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS sources (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                name VARCHAR(255) NOT NULL,
                canonical_key VARCHAR(320) NOT NULL,
                legacy_card_id BIGINT NULL,
                name_conflict BOOLEAN NOT NULL DEFAULT FALSE,
                UNIQUE KEY uq_sources_key (canonical_key),
                UNIQUE KEY uq_sources_legacy (legacy_card_id),
                KEY idx_sources_name (name)
            );
            """);
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS card_tags (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                card_id BIGINT NOT NULL,
                tag_id BIGINT NOT NULL,
                assignment_source VARCHAR(32) NOT NULL DEFAULT 'manual',
                confidence DECIMAL(6,5) NULL,
                legacy_relation_id BIGINT NULL,
                UNIQUE KEY uq_card_tags_legacy (legacy_relation_id),
                KEY idx_card_tags_card (card_id),
                KEY idx_card_tags_tag (tag_id),
                CONSTRAINT fk_card_tags_card FOREIGN KEY (card_id) REFERENCES cards(id) ON DELETE CASCADE,
                CONSTRAINT fk_card_tags_tag FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
            );
            """);
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS card_sources (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                card_id BIGINT NOT NULL,
                source_id BIGINT NOT NULL,
                original_url TEXT NULL,
                legacy_relation_id BIGINT NULL,
                UNIQUE KEY uq_card_sources_legacy (legacy_relation_id),
                KEY idx_card_sources_card (card_id),
                KEY idx_card_sources_source (source_id),
                CONSTRAINT fk_card_sources_card FOREIGN KEY (card_id) REFERENCES cards(id) ON DELETE CASCADE,
                CONSTRAINT fk_card_sources_source FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
            );
            """);

        await using var transaction = await connection.BeginTransactionAsync();
        var committed = false;
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO tags (name, canonical_key, category, origin, legacy_card_id)
                SELECT COALESCE(NULLIF(TRIM(title), ''), CONCAT('Legacy tag #', id)),
                       CONCAT('legacy-tag:', id), 'general', 'user', id
                FROM cards WHERE type = 'tag'
                ON DUPLICATE KEY UPDATE name = tags.name;
                """, transaction: transaction);
            await connection.ExecuteAsync("""
                INSERT INTO sources (name, canonical_key, legacy_card_id)
                SELECT COALESCE(NULLIF(TRIM(title), ''), CONCAT('Legacy source #', id)),
                       CONCAT('legacy-source:', id), id
                FROM cards WHERE type = 'source'
                ON DUPLICATE KEY UPDATE name = sources.name;
                """, transaction: transaction);

            await connection.ExecuteAsync("""
                UPDATE tags t JOIN cards c ON c.id = t.legacy_card_id
                SET t.name = COALESCE(NULLIF(TRIM(c.title), ''), CONCAT('Legacy tag #', c.id))
                WHERE c.type = 'tag';
                """, transaction: transaction);
            await connection.ExecuteAsync("""
                UPDATE sources s JOIN cards c ON c.id = s.legacy_card_id
                SET s.name = COALESCE(NULLIF(TRIM(c.title), ''), CONCAT('Legacy source #', c.id))
                WHERE c.type = 'source';
                """, transaction: transaction);

            await connection.ExecuteAsync("""
                UPDATE tags SET name_conflict = FALSE WHERE legacy_card_id IS NOT NULL;
                """, transaction: transaction);
            await connection.ExecuteAsync("""
                UPDATE sources SET name_conflict = FALSE WHERE legacy_card_id IS NOT NULL;
                """, transaction: transaction);
            await connection.ExecuteAsync("""
                UPDATE tags t JOIN tags duplicate
                    ON duplicate.name = t.name AND duplicate.id <> t.id
                    AND duplicate.legacy_card_id IS NOT NULL
                SET t.name_conflict = TRUE WHERE t.legacy_card_id IS NOT NULL;
                """, transaction: transaction);
            await connection.ExecuteAsync("""
                UPDATE sources s JOIN sources duplicate
                    ON duplicate.name = s.name AND duplicate.id <> s.id
                    AND duplicate.legacy_card_id IS NOT NULL
                SET s.name_conflict = TRUE WHERE s.legacy_card_id IS NOT NULL;
                """, transaction: transaction);

            await connection.ExecuteAsync("""
                INSERT INTO card_tags (card_id, tag_id, assignment_source, legacy_relation_id)
                SELECT r.from_card_id, t.id, 'manual', r.id
                FROM card_relations r JOIN tags t ON t.legacy_card_id = r.to_card_id
                WHERE r.relation_type = 'tagged_with'
                ON DUPLICATE KEY UPDATE card_id = VALUES(card_id), tag_id = VALUES(tag_id);
                """, transaction: transaction);
            await connection.ExecuteAsync("""
                INSERT INTO card_sources (card_id, source_id, original_url, legacy_relation_id)
                SELECT r.from_card_id, s.id,
                       JSON_UNQUOTE(JSON_EXTRACT(r.properties, '$.original_url')), r.id
                FROM card_relations r JOIN sources s ON s.legacy_card_id = r.to_card_id
                WHERE r.relation_type = 'sourced_from'
                ON DUPLICATE KEY UPDATE card_id = VALUES(card_id), source_id = VALUES(source_id),
                                        original_url = VALUES(original_url);
                """, transaction: transaction);

            await connection.ExecuteAsync("""
                DELETE ct FROM card_tags ct LEFT JOIN card_relations r ON r.id = ct.legacy_relation_id
                WHERE ct.legacy_relation_id IS NOT NULL
                  AND (r.id IS NULL OR r.relation_type <> 'tagged_with');
                """, transaction: transaction);
            await connection.ExecuteAsync("""
                DELETE cs FROM card_sources cs LEFT JOIN card_relations r ON r.id = cs.legacy_relation_id
                WHERE cs.legacy_relation_id IS NOT NULL
                  AND (r.id IS NULL OR r.relation_type <> 'sourced_from');
                """, transaction: transaction);
            await connection.ExecuteAsync("""
                DELETE t FROM tags t LEFT JOIN cards c ON c.id = t.legacy_card_id AND c.type = 'tag'
                WHERE t.legacy_card_id IS NOT NULL AND c.id IS NULL;
                """, transaction: transaction);
            await connection.ExecuteAsync("""
                DELETE s FROM sources s LEFT JOIN cards c ON c.id = s.legacy_card_id AND c.type = 'source'
                WHERE s.legacy_card_id IS NOT NULL AND c.id IS NULL;
                """, transaction: transaction);

            var legacyTagLinks = await connection.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM card_relations r JOIN cards c ON c.id = r.to_card_id
                WHERE r.relation_type = 'tagged_with' AND c.type = 'tag';
                """, transaction: transaction);
            var copiedTagLinks = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM card_tags WHERE legacy_relation_id IS NOT NULL;", transaction: transaction);
            var legacySourceLinks = await connection.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM card_relations r JOIN cards c ON c.id = r.to_card_id
                WHERE r.relation_type = 'sourced_from' AND c.type = 'source';
                """, transaction: transaction);
            var copiedSourceLinks = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM card_sources WHERE legacy_relation_id IS NOT NULL;", transaction: transaction);
            var legacyTags = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM cards WHERE type = 'tag';", transaction: transaction);
            var copiedTags = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tags WHERE legacy_card_id IS NOT NULL;", transaction: transaction);
            var legacySources = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM cards WHERE type = 'source';", transaction: transaction);
            var copiedSources = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sources WHERE legacy_card_id IS NOT NULL;", transaction: transaction);
            if (legacyTags != copiedTags || legacySources != copiedSources ||
                legacyTagLinks != copiedTagLinks || legacySourceLinks != copiedSourceLinks)
                throw new InvalidOperationException("Catalog migration count mismatch; legacy data was not changed.");

            var nonstandard = await connection.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM card_relations r
                JOIN cards from_card ON from_card.id = r.from_card_id
                JOIN cards to_card ON to_card.id = r.to_card_id
                WHERE (from_card.type IN ('tag', 'source') OR to_card.type IN ('tag', 'source'))
                  AND NOT ((r.relation_type = 'tagged_with' AND to_card.type = 'tag')
                           OR (r.relation_type = 'sourced_from' AND to_card.type = 'source'));
                """, transaction: transaction);
            var deckMemberships = await connection.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM deck_cards dc JOIN cards c ON c.id = dc.card_id
                WHERE c.type IN ('tag', 'source');
                """, transaction: transaction);
            await transaction.CommitAsync();
            committed = true;
            _logger.LogInformation("Catalog migration: {Tags} tags, {Sources} sources, {TagLinks} tag links, {SourceLinks} source links; legacy special relations {SpecialLinks}, deck memberships {DeckLinks} retained.",
                copiedTags, copiedSources, copiedTagLinks, copiedSourceLinks, nonstandard, deckMemberships);
        }
        catch
        {
            if (!committed)
                await transaction.RollbackAsync();
            throw;
        }
    }
}

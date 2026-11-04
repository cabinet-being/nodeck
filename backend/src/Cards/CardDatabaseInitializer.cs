using Dapper;
using MySqlConnector;

namespace MyApp.Api.Cards;

public sealed class CardDatabaseInitializer
{
    private readonly string _connectionString;

    public CardDatabaseInitializer(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
    }

    public async Task InitializeAsync()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var relationTypeConstraint = await connection.QuerySingleOrDefaultAsync<string>(
            """
            SELECT cc.CHECK_CLAUSE
            FROM information_schema.CHECK_CONSTRAINTS cc
            JOIN information_schema.TABLE_CONSTRAINTS tc
              ON tc.CONSTRAINT_SCHEMA = cc.CONSTRAINT_SCHEMA
             AND tc.CONSTRAINT_NAME = cc.CONSTRAINT_NAME
            WHERE tc.TABLE_SCHEMA = DATABASE()
              AND tc.TABLE_NAME = 'card_relations'
              AND tc.CONSTRAINT_NAME = 'chk_card_relations_type';
            """);

        if (relationTypeConstraint?.Contains("next_in_sequence", StringComparison.OrdinalIgnoreCase) != true)
        {
            if (relationTypeConstraint is not null)
            {
                await connection.ExecuteAsync("ALTER TABLE card_relations DROP CHECK chk_card_relations_type;");
            }

            await connection.ExecuteAsync(
                """
                ALTER TABLE card_relations
                ADD CONSTRAINT chk_card_relations_type CHECK (
                    relation_type IN ('tagged_with', 'sourced_from', 'contains', 'related_to', 'preview_for', 'next_in_sequence')
                );
                """);
        }

        var ownershipColumnKind = await connection.QuerySingleOrDefaultAsync<string>(
            """
            SELECT EXTRA FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'card_relations' AND COLUMN_NAME = 'contained_child_id';
            """);

        var ownershipIndex = await connection.QuerySingleAsync<long>(
            """
            SELECT COUNT(*) FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'card_relations'
              AND INDEX_NAME = 'uq_card_relations_contained_child' AND NON_UNIQUE = 0;
            """);
        if (ownershipColumnKind?.Contains("VIRTUAL GENERATED", StringComparison.OrdinalIgnoreCase) == true && ownershipIndex != 0)
            return;

        var invalid = await connection.QueryFirstOrDefaultAsync<string>(
            """
            SELECT CONCAT('relation ', r.id, ' (parent ', r.from_card_id, ', child ', r.to_card_id, ')')
            FROM card_relations r
            JOIN cards p ON p.id = r.from_card_id
            JOIN cards c ON c.id = r.to_card_id
            WHERE r.relation_type = 'contains'
              AND (p.type NOT IN ('comic','set') OR c.type <> 'media')
            LIMIT 1;
            """);
        if (invalid is not null) throw new InvalidOperationException($"Invalid collection ownership: {invalid}.");

        var duplicate = await connection.QueryFirstOrDefaultAsync<long?>(
            """
            SELECT to_card_id FROM card_relations
            WHERE relation_type = 'contains'
            GROUP BY to_card_id HAVING COUNT(*) > 1 LIMIT 1;
            """);
        if (duplicate is not null) throw new InvalidOperationException($"Media card {duplicate} has multiple collection parents; resolve before migration.");

        var malformedPosition = await connection.QueryFirstOrDefaultAsync<long?>(
            """
            SELECT id FROM card_relations
            WHERE relation_type = 'contains'
              AND (JSON_TYPE(JSON_EXTRACT(properties, '$.position')) IS NULL
                   OR JSON_TYPE(JSON_EXTRACT(properties, '$.position')) <> 'INTEGER'
                   OR CAST(JSON_UNQUOTE(JSON_EXTRACT(properties, '$.position')) AS SIGNED) < 0)
            LIMIT 1;
            """);
        if (malformedPosition is not null) throw new InvalidOperationException($"Collection relation {malformedPosition} has an invalid position; resolve before migration.");

        var badPosition = await connection.QueryFirstOrDefaultAsync<long?>(
            """
            SELECT r.from_card_id FROM card_relations r
            WHERE r.relation_type = 'contains'
            GROUP BY r.from_card_id
            HAVING COUNT(*) <> COUNT(DISTINCT CAST(JSON_UNQUOTE(JSON_EXTRACT(r.properties, '$.position')) AS SIGNED))
            LIMIT 1;
            """);
        if (badPosition is not null) throw new InvalidOperationException($"Collection {badPosition} has duplicate or missing positions; resolve before migration.");

        if (ownershipColumnKind is null)
            await connection.ExecuteAsync(
                """
                ALTER TABLE card_relations
                ADD COLUMN contained_child_id BIGINT GENERATED ALWAYS AS (CASE WHEN relation_type = 'contains' THEN to_card_id ELSE NULL END) VIRTUAL;
                """);
        else if (!ownershipColumnKind.Contains("VIRTUAL GENERATED", StringComparison.OrdinalIgnoreCase))
        {
            if (!ownershipColumnKind.Contains("STORED GENERATED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected contained_child_id column definition; resolve before migration.");
            if (ownershipIndex != 0)
                await connection.ExecuteAsync("ALTER TABLE card_relations DROP INDEX uq_card_relations_contained_child;");
            await connection.ExecuteAsync(
                """
                ALTER TABLE card_relations
                DROP COLUMN contained_child_id;
                """);
            await connection.ExecuteAsync(
                """
                ALTER TABLE card_relations
                ADD COLUMN contained_child_id BIGINT GENERATED ALWAYS AS (CASE WHEN relation_type = 'contains' THEN to_card_id ELSE NULL END) VIRTUAL;
                """);
            ownershipIndex = 0;
        }
        if (ownershipIndex == 0)
            await connection.ExecuteAsync(
                "ALTER TABLE card_relations ADD UNIQUE INDEX uq_card_relations_contained_child (contained_child_id);");
    }
}

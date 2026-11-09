using System.Data;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using static MyApp.Api.Cards.CardMapping;
using static MyApp.Api.Cards.CardMetadata;
using static MyApp.Api.Cards.CardSql;
using static MyApp.Api.Cards.CardTypes;

namespace MyApp.Api.Cards;

public sealed partial class CardRepository
{
    public async Task<CardRelationsResponse?> GetRelationsFeedAsync(long id)
    {
        await using var connection = new MySqlConnection(_connectionString);

        var cardExists = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT id
            FROM cards
            WHERE id = @Id;
            """,
            new { Id = id });

        if (cardExists is null)
        {
            return null;
        }

        var rows = await GetRelationLinkRowsAsync(connection, id);

        return new CardRelationsResponse(
            rows.Where(row => string.Equals(row.Direction, "outgoing", StringComparison.OrdinalIgnoreCase))
                .Select(ToRelationEntry)
                .ToList(),
            rows.Where(row => string.Equals(row.Direction, "incoming", StringComparison.OrdinalIgnoreCase))
                .Select(ToRelationEntry)
                .ToList());
    }

    public async Task<CardRelationEntry> CreateRelationAsync(long cardId, CreateCardRelationRequest request)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var relation = await CreateRelationAsync(connection, transaction, cardId, request.ToCardId, request.RelationType, request.Properties);
        await transaction.CommitAsync();

        return relation;
    }

    public async Task<CardRelationEntry?> UpdateRelationAsync(long relationId, UpdateCardRelationRequest request)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var relation = await connection.QuerySingleOrDefaultAsync<DbCardRelation>(
            """
            SELECT
                id AS Id,
                from_card_id AS FromCardId,
                to_card_id AS ToCardId,
                relation_type AS RelationType,
                properties AS Properties,
                metadata AS Metadata
            FROM card_relations
            WHERE id = @Id
            FOR UPDATE;
            """,
            new { Id = relationId },
            transaction);

        if (relation is null)
        {
            await transaction.RollbackAsync();
            return null;
        }

        if (!IsGenericEditableRelationType(relation.RelationType))
        {
            throw new InvalidOperationException($"Relation type '{relation.RelationType}' cannot be edited generically.");
        }

        var propertiesJson = request.Properties?.ToJsonString();

        await connection.ExecuteAsync(
            """
            UPDATE card_relations
            SET properties = @Properties
            WHERE id = @Id;
            """,
            new { Id = relationId, Properties = propertiesJson },
            transaction);

        await transaction.CommitAsync();

        return await GetRelationEntryAsync(connection, relationId, relation.FromCardId);
    }

    public async Task<bool> DeleteRelationAsync(long relationId)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var relation = await connection.QuerySingleOrDefaultAsync<DbCardRelation>(
            """
            SELECT
                id AS Id,
                from_card_id AS FromCardId,
                to_card_id AS ToCardId,
                relation_type AS RelationType,
                properties AS Properties,
                metadata AS Metadata
            FROM card_relations
            WHERE id = @Id
            FOR UPDATE;
            """,
            new { Id = relationId },
            transaction);

        if (relation is null)
        {
            await transaction.RollbackAsync();
            return false;
        }

        if (!IsGenericEditableRelationType(relation.RelationType))
        {
            throw new InvalidOperationException($"Relation type '{relation.RelationType}' cannot be deleted generically.");
        }

        var affectedRows = await connection.ExecuteAsync(
            """
            DELETE FROM card_relations
            WHERE id = @Id;
            """,
            new { Id = relationId },
            transaction);

        await transaction.CommitAsync();

        return affectedRows > 0;
    }

    private static async Task<IReadOnlyList<CardRelationResponse>> GetRelationsAsync(
        IDbConnection connection,
        string condition,
        long id,
        IDbTransaction? transaction = null)
    {
        var rows = await connection.QueryAsync<DbCardRelation>(
            $"""
            SELECT
                id AS Id,
                from_card_id AS FromCardId,
                to_card_id AS ToCardId,
                relation_type AS RelationType,
                properties AS Properties,
                metadata AS Metadata
            FROM card_relations
            WHERE {condition}
            ORDER BY id ASC;
            """,
            new { Id = id },
            transaction);

        return rows.Select(ToRelation).ToList();
    }

    private static async Task<IReadOnlyList<ContainedCardResponse>> GetContainedCardsAsync(
        IDbConnection connection,
        long id,
        IDbTransaction? transaction = null)
    {
        var rows = await connection.QueryAsync<ContainedCardRow>(
            $"""
            SELECT
                c.id AS Id,
                c.type AS Type,
                c.title AS Title,
                c.preview AS Preview,
                c.metadata AS Metadata,
                {FavoriteSelectSql("c.id")} AS IsFavorite,
                CAST(JSON_UNQUOTE(JSON_EXTRACT(r.properties, '$.position')) AS SIGNED) AS Position
            FROM card_relations r
            JOIN cards c ON c.id = r.to_card_id
            WHERE r.from_card_id = @Id
              AND r.relation_type = 'contains'
            ORDER BY Position ASC, r.id ASC;
            """,
            new { Id = id },
            transaction);

        return rows.Select(ToContainedCard).ToList();
    }

    private static async Task InsertRelationsAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        long fromCardId,
        IReadOnlyList<CreateRelationData> relations,
        bool validateRelations = true)
    {
        foreach (var relation in relations)
        {
            if (validateRelations)
            {
                await ValidateRelationAsync(connection, transaction, fromCardId, relation.ToCardId, relation.RelationType, null);
            }
            await connection.ExecuteAsync(
                """
                INSERT INTO card_relations (
                    from_card_id,
                    to_card_id,
                    relation_type,
                    properties,
                    metadata
                )
                VALUES (
                    @FromCardId,
                    @ToCardId,
                    @RelationType,
                    @Properties,
                    @Metadata
                );
                """,
                new
                {
                    FromCardId = fromCardId,
                    relation.ToCardId,
                    relation.RelationType,
                    Properties = relation.PropertiesJson,
                    Metadata = relation.Metadata.ToJsonString(),
                },
                transaction);
        }
    }

    private async Task<CardRelationEntry> CreateRelationAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        long fromCardId,
        long toCardId,
        string relationType,
        JsonObject? properties)
    {
        await ValidateRelationAsync(connection, transaction, fromCardId, toCardId, relationType, null);

        var relationId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO card_relations (
                from_card_id,
                to_card_id,
                relation_type,
                properties,
                metadata
            )
            VALUES (
                @FromCardId,
                @ToCardId,
                @RelationType,
                @Properties,
                @Metadata
            );
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                FromCardId = fromCardId,
                ToCardId = toCardId,
                RelationType = relationType,
                Properties = properties?.ToJsonString(),
                Metadata = CreateMetadata().ToJsonString(),
            },
            transaction);

        return await GetRelationEntryAsync(connection, relationId, fromCardId)
            ?? throw new InvalidOperationException("Created relation could not be read.");
    }

    private static async Task ValidateRelationAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        long fromCardId,
        long toCardId,
        string relationType,
        long? ignoreRelationId)
    {
        if (!IsGenericEditableRelationType(relationType))
        {
            throw new InvalidOperationException($"Relation type '{relationType}' cannot be managed through the generic relation API.");
        }

        if (fromCardId == toCardId)
        {
            throw new InvalidOperationException("Self relations are not allowed.");
        }

        var endpoints = await connection.QueryAsync<DbCardType>(
            """
            SELECT
                id AS Id,
                type AS Type
            FROM cards
            WHERE id IN @Ids;
            """,
            new { Ids = new[] { fromCardId, toCardId } },
            transaction);

        var types = endpoints.ToDictionary(card => card.Id, card => card.Type);
        if (!types.TryGetValue(fromCardId, out var fromType) || !types.TryGetValue(toCardId, out var toType))
        {
            throw new InvalidOperationException("Relations must reference existing cards.");
        }

        if (relationType == "tagged_with")
        {
            if (!IsTaggableCardType(fromType))
            {
                throw new InvalidOperationException("Only media, comic, set, and source cards can be tagged.");
            }

            if (toType != "tag")
            {
                throw new InvalidOperationException("Tagged relations must point to a tag card.");
            }
        }
        else if (relationType == "sourced_from")
        {
            if (!IsSourceableCardType(fromType))
            {
                throw new InvalidOperationException("Only media, comic, and set cards can have sources.");
            }

            if (toType != "source")
            {
                throw new InvalidOperationException("Source relations must point to a source card.");
            }
        }
        else if (relationType == "related_to")
        {
            // Any card type is allowed, but reverse duplicates are rejected below.
        }
        else
        {
            throw new InvalidOperationException($"Relation type '{relationType}' cannot be managed through the generic relation API.");
        }

        var existingCount = relationType == "related_to"
            ? await connection.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*)
                FROM card_relations
                WHERE relation_type = @RelationType
                  AND (
                    (from_card_id = @FromCardId AND to_card_id = @ToCardId)
                    OR (from_card_id = @ToCardId AND to_card_id = @FromCardId)
                  )
                  AND (@IgnoreRelationId IS NULL OR id <> @IgnoreRelationId);
                """,
                new
                {
                    FromCardId = fromCardId,
                    ToCardId = toCardId,
                    RelationType = relationType,
                    IgnoreRelationId = ignoreRelationId,
                },
                transaction)
            : await connection.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*)
                FROM card_relations
                WHERE from_card_id = @FromCardId
                  AND to_card_id = @ToCardId
                  AND relation_type = @RelationType
                  AND (@IgnoreRelationId IS NULL OR id <> @IgnoreRelationId);
                """,
                new
                {
                    FromCardId = fromCardId,
                    ToCardId = toCardId,
                    RelationType = relationType,
                    IgnoreRelationId = ignoreRelationId,
                },
                transaction);

        if (existingCount > 0)
        {
            throw new InvalidOperationException("This relation already exists.");
        }
    }

    private static async Task<CardRelationEntry?> GetRelationEntryAsync(
        IDbConnection connection,
        long relationId,
        long currentCardId,
        IDbTransaction? transaction = null)
    {
        var rows = await GetRelationLinkRowsAsync(connection, currentCardId, transaction, relationId);
        return rows.Select(ToRelationEntry).FirstOrDefault();
    }

    private static async Task<IReadOnlyList<DbRelationLinkRow>> GetRelationLinkRowsAsync(
        IDbConnection connection,
        long cardId,
        IDbTransaction? transaction = null,
        long? relationId = null)
    {
        var rows = await connection.QueryAsync<DbRelationLinkRow>(
            $"""
            SELECT
                r.id AS Id,
                r.relation_type AS RelationType,
                CASE
                    WHEN r.from_card_id = @CardId THEN 'outgoing'
                    ELSE 'incoming'
                END AS Direction,
                CASE
                    WHEN r.from_card_id = @CardId THEN r.to_card_id
                    ELSE r.from_card_id
                END AS RelatedCardId,
                c.type AS RelatedCardType,
                c.title AS RelatedCardTitle,
                c.preview AS RelatedCardPreview,
                c.metadata AS RelatedCardMetadata,
                r.properties AS Properties
            FROM card_relations r
            JOIN cards c ON c.id = CASE
                WHEN r.from_card_id = @CardId THEN r.to_card_id
                ELSE r.from_card_id
            END
            WHERE (r.from_card_id = @CardId OR r.to_card_id = @CardId)
              AND r.relation_type <> 'preview_for'
              {(relationId is null ? "" : "AND r.id = @RelationId")}
            ORDER BY
                CASE r.relation_type
                    WHEN 'tagged_with' THEN 1
                    WHEN 'sourced_from' THEN 2
                    WHEN 'related_to' THEN 3
                    WHEN 'contains' THEN 4
                    WHEN 'next_in_sequence' THEN 5
                    ELSE 6
                END,
                r.id ASC;
            """,
            relationId is null
                ? new { CardId = cardId }
                : new { CardId = cardId, RelationId = relationId },
            transaction);

        return rows.ToList();
    }
}

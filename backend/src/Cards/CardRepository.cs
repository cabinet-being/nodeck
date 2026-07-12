using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;

namespace MyApp.Api.Cards;

public sealed class CardRepository
{
    private readonly string _connectionString;

    public CardRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
    }

    public async Task<CardDetails> CreateAsync(
        CreateCardData data,
        Func<long, Task<CardFileAssets?>> createAssets,
        Action<long> cleanupAssets)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        long cardId = 0;

        try
        {
            cardId = await InsertCardAsync(connection, transaction, data.Type, data.Title, data.PropertiesJson, data.Metadata);
            var assets = await createAssets(cardId);
            var previewPath = assets?.PreviewPath;
            var metadata = data.Metadata;

            if (assets is not null)
            {
                ApplyAssetMetadata(metadata, assets);
            }

            await UpdateCardAssetsAsync(connection, transaction, cardId, previewPath, metadata);
            await InsertRelationsAsync(connection, transaction, cardId, data.Relations);
            await transaction.CommitAsync();

            return BuildCreatedCardDetails(
                cardId,
                data.Type,
                data.Title,
                previewPath,
                metadata,
                data.PropertiesJson,
                data.Relations);
        }
        catch
        {
            await transaction.RollbackAsync();

            if (cardId > 0)
            {
                cleanupAssets(cardId);
            }

            throw;
        }
    }

    public async Task<CardDetails> CreateCollectionAsync(
        CreateCardCollectionData data,
        Func<long, IFormFile, Task<CardFileAssets>> createAssets,
        Action<long> cleanupAssets)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var createdCardIds = new List<long>();

        try
        {
            var parentId = await InsertCardAsync(connection, transaction, data.Type, data.Title, data.PropertiesJson, data.Metadata);
            createdCardIds.Add(parentId);
            long? previousMediaId = null;
            string? parentPreviewPath = null;

            for (var index = 0; index < data.Images.Count; index++)
            {
                var mediaMetadata = CreateMetadata();
                var mediaId = await InsertCardAsync(connection, transaction, "media", null, null, mediaMetadata);
                createdCardIds.Add(mediaId);

                var assets = await createAssets(mediaId, data.Images[index]);
                ApplyAssetMetadata(mediaMetadata, assets);
                await UpdateCardAssetsAsync(connection, transaction, mediaId, assets.PreviewPath, mediaMetadata);

                parentPreviewPath ??= assets.PreviewPath;

                await InsertRelationsAsync(
                    connection,
                    transaction,
                    parentId,
                    [
                        new CreateRelationData(
                            mediaId,
                            "contains",
                            new JsonObject { ["position"] = index }.ToJsonString(),
                            CreateMetadata()),
                    ],
                    validateRelations: false);

                if (data.Type == "comic" && previousMediaId is not null)
                {
                    await InsertRelationsAsync(
                        connection,
                        transaction,
                        previousMediaId.Value,
                        [
                            new CreateRelationData(
                                mediaId,
                                "next_in_sequence",
                                null,
                                CreateMetadata()),
                        ],
                        validateRelations: false);
                }

                previousMediaId = mediaId;
            }

            data.Metadata["preview_path"] = parentPreviewPath;
            data.Metadata["contained_count"] = data.Images.Count;
            await UpdateCardAssetsAsync(connection, transaction, parentId, parentPreviewPath, data.Metadata);
            await InsertRelationsAsync(connection, transaction, parentId, data.Relations);
            await transaction.CommitAsync();

            var containedCards = createdCardIds
                .Skip(1)
                .Select((mediaCardId, index) => new ContainedCardResponse(
                    mediaCardId,
                    "media",
                    null,
                    parentPreviewPath is null ? null : $"/api/cards/{mediaCardId}/preview",
                    $"/api/cards/{mediaCardId}/content",
                    false,
                    index))
                .ToList();

            var relations = BuildCollectionRelations(parentId, createdCardIds, data);

            return new CardDetails(
                parentId,
                data.Type,
                data.Title,
                parentPreviewPath is null ? null : $"/api/cards/{parentId}/preview",
                null,
                ParseOptionalJson(data.PropertiesJson),
                data.Metadata,
                false,
                relations,
                [],
                containedCards);
        }
        catch
        {
            await transaction.RollbackAsync();

            foreach (var cardId in createdCardIds)
            {
                cleanupAssets(cardId);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<CardSummary>> ListAsync(
        IReadOnlyList<string> types,
        string? mediaType,
        string? search,
        bool excludeContainedMedia,
        IReadOnlyList<long> tagIds,
        long? sourceId,
        string sort,
        string order)
    {
        await using var connection = new MySqlConnection(_connectionString);

        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (types.Count > 0)
        {
            conditions.Add("type IN @Types");
            parameters.Add("Types", types);
        }

        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            conditions.Add("JSON_UNQUOTE(JSON_EXTRACT(metadata, '$.media_type')) = @MediaType");
            parameters.Add("MediaType", mediaType);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("(CAST(id AS CHAR) = @Search OR title LIKE @SearchLike)");
            parameters.Add("Search", search);
            parameters.Add("SearchLike", $"%{search}%");
        }

        if (excludeContainedMedia)
        {
            conditions.Add("""
                NOT EXISTS (
                    SELECT 1
                    FROM card_relations r
                    WHERE r.to_card_id = cards.id
                      AND r.relation_type = 'contains'
                )
                """);
        }

        for (var index = 0; index < tagIds.Count; index++)
        {
            var parameterName = $"TagId{index}";
            conditions.Add($"""
                EXISTS (
                    SELECT 1
                    FROM card_relations relation_tags
                    WHERE relation_tags.from_card_id = cards.id
                      AND relation_tags.relation_type = 'tagged_with'
                      AND relation_tags.to_card_id = @{parameterName}
                )
                """);
            parameters.Add(parameterName, tagIds[index]);
        }

        if (sourceId is not null)
        {
            conditions.Add("""
                EXISTS (
                    SELECT 1
                    FROM card_relations relation_sources
                    WHERE relation_sources.from_card_id = cards.id
                      AND relation_sources.relation_type = 'sourced_from'
                      AND relation_sources.to_card_id = @SourceId
                )
                """);
            parameters.Add("SourceId", sourceId.Value);
        }

        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
        var orderDirection = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)
            ? "ASC"
            : "DESC";
        var orderColumn = string.Equals(sort, "created_at", StringComparison.OrdinalIgnoreCase)
            ? "created_at"
            : "id";

        var rows = await connection.QueryAsync<DbCard>(
            $"""
            SELECT
                id AS {nameof(DbCard.Id)},
                type AS {nameof(DbCard.Type)},
                title AS {nameof(DbCard.Title)},
                preview AS {nameof(DbCard.Preview)},
                properties AS {nameof(DbCard.Properties)},
                metadata AS {nameof(DbCard.Metadata)},
                {FavoriteSelectSql("cards.id")} AS {nameof(DbCard.IsFavorite)}
            FROM cards
            {where}
            ORDER BY {orderColumn} {orderDirection}, id {orderDirection}
            LIMIT 200;
            """,
            parameters);

        return rows.Select(ToSummary).ToList();
    }

    public async Task<CardDetails?> UpdateAsync(
        long id,
        UpdateCardData data,
        Func<Task>? promoteAssets = null,
        Action? rollbackAssets = null,
        Action? cleanupPromotedAssets = null)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var committed = false;

        try
        {
            var card = await connection.QuerySingleOrDefaultAsync<DbCard>(
                $"""
                SELECT
                    id AS Id,
                    type AS Type,
                    title AS Title,
                    preview AS Preview,
                    properties AS Properties,
                    metadata AS Metadata,
                    {FavoriteSelectSql("cards.id")} AS IsFavorite
                FROM cards
                WHERE id = @Id
                FOR UPDATE;
                """,
                new { Id = id },
                transaction);

            if (card is null)
            {
                await transaction.RollbackAsync();
                rollbackAssets?.Invoke();
                return null;
            }

            var metadata = ParseRequiredJsonObject(card.Metadata);
            var previewPath = card.Preview;

            if (data.Assets is not null)
            {
                ApplyAssetMetadata(metadata, data.Assets);
                previewPath = data.Assets.PreviewPath;
            }

            await connection.ExecuteAsync(
                """
                UPDATE cards
                SET title = @Title,
                    preview = @Preview,
                    properties = @Properties,
                    metadata = @Metadata
                WHERE id = @Id;
                """,
                new
                {
                    Id = id,
                    data.Title,
                    Preview = previewPath,
                    Properties = data.PropertiesJson,
                    Metadata = metadata.ToJsonString(),
                },
                transaction);

            await connection.ExecuteAsync(
                """
                DELETE FROM card_relations
                WHERE from_card_id = @Id
                  AND relation_type IN (
                      'tagged_with',
                      'sourced_from',
                      'related_to'
                  );
                """,
                new { Id = id },
                transaction);

            await InsertRelationsAsync(connection, transaction, id, data.Relations);

            var outgoingRelations = await GetRelationsAsync(connection, "from_card_id = @Id", id, transaction);
            var incomingRelations = await GetRelationsAsync(connection, "to_card_id = @Id", id, transaction);
            var containedCards = await GetContainedCardsAsync(connection, id, transaction);
            var updatedCard = new DbCard(
                id,
                card.Type,
                data.Title ?? card.Title,
                previewPath,
                data.PropertiesJson ?? card.Properties,
                metadata.ToJsonString(),
                card.IsFavorite);

            if (promoteAssets is not null)
            {
                await promoteAssets();
            }

            await transaction.CommitAsync();
            committed = true;

            try
            {
                cleanupPromotedAssets?.Invoke();
            }
            catch
            {
                // A committed media replacement must remain successful even if old-file cleanup fails.
            }

            return ToDetails(updatedCard, outgoingRelations, incomingRelations, containedCards);
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync();
                rollbackAssets?.Invoke();
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<long>> DeleteAsync(long id)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var card = await connection.QuerySingleOrDefaultAsync<DbCard>(
            $"""
            SELECT
                id AS Id,
                type AS Type,
                title AS Title,
                preview AS Preview,
                properties AS Properties,
                metadata AS Metadata,
                FALSE AS IsFavorite
            FROM cards
            WHERE id = @Id
            FOR UPDATE;
            """,
            new { Id = id },
            transaction);

        if (card is null)
        {
            await transaction.RollbackAsync();
            return [];
        }

        var deletedCardIds = new List<long> { id };

        if (card.Type is "comic" or "set")
        {
            var containedCardIds = await connection.QueryAsync<long>(
                """
                SELECT c.id
                FROM card_relations r
                JOIN cards c ON c.id = r.to_card_id
                WHERE r.from_card_id = @Id
                  AND r.relation_type = 'contains'
                  AND c.type = 'media'
                ORDER BY r.id ASC;
                """,
                new { Id = id },
                transaction);

            deletedCardIds.AddRange(containedCardIds);
        }

        var affectedRows = await connection.ExecuteAsync(
            """
            DELETE FROM cards
            WHERE id IN @Ids;
            """,
            new { Ids = deletedCardIds },
            transaction);

        await transaction.CommitAsync();

        return affectedRows > 0 ? deletedCardIds : [];
    }

    public async Task<CardDetails?> GetByIdAsync(long id)
    {
        await using var connection = new MySqlConnection(_connectionString);

        var card = await connection.QuerySingleOrDefaultAsync<DbCard>(
            $"""
            SELECT
                id AS Id,
                type AS Type,
                title AS Title,
                preview AS Preview,
                properties AS Properties,
                metadata AS Metadata,
                {FavoriteSelectSql("cards.id")} AS IsFavorite
            FROM cards
            WHERE id = @Id;
            """,
            new { Id = id });

        if (card is null)
        {
            return null;
        }

        var outgoing = await GetRelationsAsync(connection, "from_card_id = @Id", id);
        var incoming = await GetRelationsAsync(connection, "to_card_id = @Id", id);
        var containedCards = await GetContainedCardsAsync(connection, id);

        return ToDetails(card, outgoing, incoming, containedCards);
    }

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

    private static async Task<long> InsertCardAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string type,
        string? title,
        string? propertiesJson,
        JsonObject metadata)
    {
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO cards (type, title, preview, properties, metadata)
            VALUES (@Type, @Title, NULL, @Properties, @Metadata);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                Type = type,
                Title = title,
                Properties = propertiesJson,
                Metadata = metadata.ToJsonString(),
            },
            transaction);
    }

    private static async Task UpdateCardAssetsAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        long cardId,
        string? previewPath,
        JsonObject metadata)
    {
        await connection.ExecuteAsync(
            """
            UPDATE cards
            SET preview = @Preview,
                metadata = @Metadata
            WHERE id = @Id;
            """,
            new
            {
                Id = cardId,
                Preview = previewPath,
                Metadata = metadata.ToJsonString(),
            },
            transaction);
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

    private static string FavoriteSelectSql(string cardIdExpression) =>
        $"""
        CASE WHEN EXISTS (
            SELECT 1
            FROM deck_cards favorite_deck_cards
            JOIN decks favorite_decks ON favorite_decks.id = favorite_deck_cards.deck_id
            WHERE favorite_decks.system_key = 'favorites'
              AND favorite_deck_cards.card_id = {cardIdExpression}
        ) THEN 1 ELSE 0 END
        """;

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

    private static bool IsGenericEditableRelationType(string relationType) =>
        relationType is "tagged_with" or "sourced_from" or "related_to";

    private static bool IsTaggableCardType(string type) =>
        type is "media" or "comic" or "set" or "source";

    private static bool IsSourceableCardType(string type) =>
        type is "media" or "comic" or "set";

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

    private static CardRelationEntry ToRelationEntry(DbRelationLinkRow row)
    {
        return new CardRelationEntry(
            row.Id,
            row.RelationType,
            row.Direction,
            new RelationCardSummary(
                row.RelatedCardId,
                row.RelatedCardType,
                row.RelatedCardTitle,
                row.RelatedCardPreview is null ? null : $"/api/cards/{row.RelatedCardId}/preview"),
            ParseOptionalJson(row.Properties));
    }

    private static CardSummary ToSummary(DbCard card)
    {
        var metadata = ParseRequiredJson(card.Metadata);
        var contentPath = metadata["content_path"]?.GetValue<string>();

        return new CardSummary(
            card.Id,
            card.Type,
            card.Title,
            card.Preview is null ? null : $"/api/cards/{card.Id}/preview",
            contentPath is null ? null : $"/api/cards/{card.Id}/content",
            ParseOptionalJson(card.Properties),
            metadata,
            card.IsFavorite != 0);
    }

    private static CardDetails ToDetails(
        DbCard card,
        IReadOnlyList<CardRelationResponse> outgoingRelations,
        IReadOnlyList<CardRelationResponse> incomingRelations,
        IReadOnlyList<ContainedCardResponse> containedCards)
    {
        var summary = ToSummary(card);

        return new CardDetails(
            summary.Id,
            summary.Type,
            summary.Title,
            summary.PreviewUrl,
            summary.ContentUrl,
            summary.Properties,
            summary.Metadata,
            summary.IsFavorite,
            outgoingRelations,
            incomingRelations,
            containedCards);
    }

    private static CardRelationResponse ToRelation(DbCardRelation relation)
    {
        return new CardRelationResponse(
            relation.Id,
            relation.FromCardId,
            relation.ToCardId,
            relation.RelationType,
            ParseOptionalJson(relation.Properties),
            ParseRequiredJson(relation.Metadata));
    }

    private static ContainedCardResponse ToContainedCard(ContainedCardRow card)
    {
        var metadata = ParseRequiredJson(card.Metadata);
        var contentPath = metadata["content_path"]?.GetValue<string>();

        return new ContainedCardResponse(
            card.Id,
            card.Type,
            card.Title,
            card.Preview is null ? null : $"/api/cards/{card.Id}/preview",
            contentPath is null ? null : $"/api/cards/{card.Id}/content",
            card.IsFavorite != 0,
            card.Position);
    }

    private static JsonObject CreateMetadata() =>
        new()
        {
            ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };

    private static void ApplyAssetMetadata(JsonObject metadata, CardFileAssets assets)
    {
        metadata["media_type"] = assets.MediaType;
        metadata["mime_type"] = assets.MimeType;
        metadata["original_file_name"] = assets.OriginalFileName;
        metadata["content_path"] = assets.ContentPath;
        metadata["preview_path"] = assets.PreviewPath;
        metadata["file_size"] = assets.FileSize;
        metadata["width"] = assets.Width;
        metadata["height"] = assets.Height;

        if (assets.Duration is not null)
        {
            metadata["duration"] = assets.Duration.Value;
        }
        else
        {
            metadata.Remove("duration");
        }

        if (assets.FrameCount is not null)
        {
            metadata["frame_count"] = assets.FrameCount.Value;
        }
        else
        {
            metadata.Remove("frame_count");
        }
    }

    private static JsonNode ParseRequiredJson(string json) =>
        JsonNode.Parse(json) ?? new JsonObject();

    private static JsonObject ParseRequiredJsonObject(string json) =>
        JsonNode.Parse(json) as JsonObject ?? new JsonObject();

    private static CardDetails BuildCreatedCardDetails(
        long cardId,
        string type,
        string? title,
        string? previewPath,
        JsonObject metadata,
        string? propertiesJson,
        IReadOnlyList<CreateRelationData> relations)
    {
        var outgoingRelations = relations.Select((relation, index) => new CardRelationResponse(
            index + 1,
            cardId,
            relation.ToCardId,
            relation.RelationType,
            ParseOptionalJson(relation.PropertiesJson),
            relation.Metadata)).ToList();

        return new CardDetails(
            cardId,
            type,
            title,
            previewPath is null ? null : $"/api/cards/{cardId}/preview",
            metadata["content_path"]?.GetValue<string>() is null ? null : $"/api/cards/{cardId}/content",
            ParseOptionalJson(propertiesJson),
            metadata,
            false,
            outgoingRelations,
            [],
            []);
    }

    private static IReadOnlyList<CardRelationResponse> BuildCollectionRelations(
        long parentId,
        IReadOnlyList<long> createdCardIds,
        CreateCardCollectionData data)
    {
        var relations = new List<CardRelationResponse>();

        var childCardIds = createdCardIds.Skip(1).ToList();

        for (var index = 0; index < childCardIds.Count; index++)
        {
            var childCardId = childCardIds[index];
            relations.Add(new CardRelationResponse(
                relations.Count + 1,
                parentId,
                childCardId,
                "contains",
                new JsonObject { ["position"] = index },
                CreateMetadata()));

            if (data.Type == "comic" && index > 0)
            {
                relations.Add(new CardRelationResponse(
                    relations.Count + 1,
                    childCardIds[index - 1],
                    childCardId,
                    "next_in_sequence",
                    null,
                    CreateMetadata()));
            }
        }

        if (data.Relations.Count > 0)
        {
            var baseCount = relations.Count;
            relations.AddRange(data.Relations.Select((relation, index) => new CardRelationResponse(
                baseCount + index + 1,
                parentId,
                relation.ToCardId,
                relation.RelationType,
                ParseOptionalJson(relation.PropertiesJson),
                relation.Metadata)));
        }

        return relations;
    }

    private static JsonNode? ParseOptionalJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
}

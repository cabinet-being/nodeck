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
                    ]);

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
                        ]);
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
                metadata AS {nameof(DbCard.Metadata)}
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
                """
                SELECT
                    id AS Id,
                    type AS Type,
                    title AS Title,
                    preview AS Preview,
                    properties AS Properties,
                    metadata AS Metadata
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
                WHERE from_card_id = @Id;
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
                metadata.ToJsonString());

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
            """
            SELECT
                id AS Id,
                type AS Type,
                title AS Title,
                preview AS Preview,
                properties AS Properties,
                metadata AS Metadata
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
            """
            SELECT
                id AS Id,
                type AS Type,
                title AS Title,
                preview AS Preview,
                properties AS Properties,
                metadata AS Metadata
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
            """
            SELECT
                c.id AS Id,
                c.type AS Type,
                c.title AS Title,
                c.preview AS Preview,
                c.metadata AS Metadata,
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
        IReadOnlyList<CreateRelationData> relations)
    {
        foreach (var relation in relations)
        {
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
            metadata);
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

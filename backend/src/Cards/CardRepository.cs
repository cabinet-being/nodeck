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
            cardId = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO cards (type, title, preview, properties, metadata)
                VALUES (@Type, @Title, NULL, @Properties, @Metadata);
                SELECT LAST_INSERT_ID();
                """,
                new
                {
                    data.Type,
                    data.Title,
                    Properties = data.PropertiesJson,
                    Metadata = data.Metadata.ToJsonString(),
                },
                transaction);

            var assets = await createAssets(cardId);
            var previewPath = assets?.PreviewPath;
            var metadata = data.Metadata;

            if (assets is not null)
            {
                metadata["media_type"] = assets.MediaType;
                metadata["mime_type"] = assets.MimeType;
                metadata["original_file_name"] = assets.OriginalFileName;
                metadata["content_path"] = assets.ContentPath;
                metadata["preview_path"] = assets.PreviewPath;
                metadata["file_size"] = assets.FileSize;
                metadata["width"] = assets.Width;
                metadata["height"] = assets.Height;
            }

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

            await InsertRelationsAsync(connection, transaction, cardId, data.Relations);

            await transaction.CommitAsync();

            return await GetByIdAsync(cardId)
                ?? throw new InvalidOperationException("Created card could not be loaded.");
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

    public async Task<IReadOnlyList<CardSummary>> ListAsync(
        string? type,
        string? mediaType,
        string? search,
        string sort,
        string order)
    {
        await using var connection = new MySqlConnection(_connectionString);

        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(type))
        {
            conditions.Add("type = @Type");
            parameters.Add("Type", type);
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

    public async Task<CardDetails?> UpdateAsync(long id, UpdateCardData data)
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
            return null;
        }

        var metadata = ParseRequiredJson(card.Metadata);
        var previewPath = card.Preview;

        if (data.Assets is not null)
        {
            metadata["media_type"] = data.Assets.MediaType;
            metadata["mime_type"] = data.Assets.MimeType;
            metadata["original_file_name"] = data.Assets.OriginalFileName;
            metadata["content_path"] = data.Assets.ContentPath;
            metadata["preview_path"] = data.Assets.PreviewPath;
            metadata["file_size"] = data.Assets.FileSize;
            metadata["width"] = data.Assets.Width;
            metadata["height"] = data.Assets.Height;
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
        await transaction.CommitAsync();

        return await GetByIdAsync(id)
            ?? throw new InvalidOperationException("Updated card could not be loaded.");
    }

    public async Task<bool> DeleteAsync(long id)
    {
        await using var connection = new MySqlConnection(_connectionString);

        var affectedRows = await connection.ExecuteAsync(
            """
            DELETE FROM cards
            WHERE id = @Id;
            """,
            new { Id = id });

        return affectedRows > 0;
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

        return ToDetails(card, outgoing, incoming);
    }

    private static async Task<IReadOnlyList<CardRelationResponse>> GetRelationsAsync(
        IDbConnection connection,
        string condition,
        long id)
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
            new { Id = id });

        return rows.Select(ToRelation).ToList();
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
        IReadOnlyList<CardRelationResponse> incomingRelations)
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
            incomingRelations);
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

    private static JsonNode ParseRequiredJson(string json) =>
        JsonNode.Parse(json) ?? new JsonObject();

    private static JsonNode? ParseOptionalJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
}

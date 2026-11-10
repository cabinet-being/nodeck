using System.Data;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using MyApp.Api.Analysis;
using static MyApp.Api.Cards.CardMetadata;

namespace MyApp.Api.Cards;

public sealed partial class CardRepository
{
    private sealed class CreationRequestRow
    {
        public byte[] RequestHash { get; init; } = [];
        public long? CardId { get; init; }
    }

    private static Task<int> InsertCreationRequestAsync(
        MySqlConnection connection, MySqlTransaction transaction, CardCreationRequest request) =>
        connection.ExecuteAsync(
            """
            INSERT INTO card_creation_requests (scope, request_key, request_hash, card_id, created_at_utc)
            VALUES (@Scope, @Key, @Hash, NULL, UTC_TIMESTAMP());
            """,
            new { request.Scope, request.Key, request.Hash }, transaction);

    private static Task<int> CompleteCreationRequestAsync(
        MySqlConnection connection, MySqlTransaction transaction, CardCreationRequest request, long cardId) =>
        connection.ExecuteAsync(
            "UPDATE card_creation_requests SET card_id = @CardId WHERE scope = @Scope AND request_key = @Key;",
            new { CardId = cardId, request.Scope, request.Key }, transaction);

    public async Task<CardCreationResult> GetCreationResultAsync(
        CardCreationRequest request, byte[]? expectedHash = null)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        var row = await connection.QuerySingleOrDefaultAsync<CreationRequestRow>(
            """
            SELECT request_hash AS RequestHash, card_id AS CardId
            FROM card_creation_requests WHERE scope = @Scope AND request_key = @Key;
            """, new { request.Scope, request.Key });
        if (row is null) return new CardCreationResult(CardCreationStatus.Unknown, null);
        if (expectedHash is not null && !row.RequestHash.AsSpan().SequenceEqual(expectedHash))
            return new CardCreationResult(CardCreationStatus.Conflict, null);
        if (row.CardId is null) return new CardCreationResult(CardCreationStatus.Gone, null);
        var card = await GetByIdAsync(row.CardId.Value);
        return card is null
            ? new CardCreationResult(CardCreationStatus.Gone, null)
            : new CardCreationResult(CardCreationStatus.Replayed, card);
    }

    public async Task<CardDetails> CreateAsync(
        CreateCardData data,
        Func<long, Task<CardFileAssets?>> createAssets,
        Action<long> cleanupAssets)
        => (await CreateAsync(data, createAssets, cleanupAssets, null)).Card!;

    public async Task<CardCreationResult> CreateAsync(
        CreateCardData data,
        Func<long, Task<CardFileAssets?>> createAssets,
        Action<long> cleanupAssets,
        CardCreationRequest? creationRequest)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        long cardId = 0;

        try
        {
            if (creationRequest is not null)
                await InsertCreationRequestAsync(connection, transaction, creationRequest);
            cardId = await InsertCardAsync(connection, transaction, data.Type, data.Title, data.PropertiesJson, data.Metadata);
            var assets = await createAssets(cardId);
            var previewPath = assets?.PreviewPath;
            var metadata = data.Metadata;

            if (assets is not null)
            {
                ApplyAssetMetadata(metadata, assets);
            }

            await UpdateCardAssetsAsync(connection, transaction, cardId, previewPath, metadata, assets?.ContentSha256);
            await InsertRelationsAsync(connection, transaction, cardId, data.Relations);
            if (data.Type == "media" && assets is not null)
            {
                await RefreshImageAnalysisJobAsync(connection, transaction, cardId, assets.MediaType);
                await SpeechAnalysisQueueRepository.RefreshAsync(connection, transaction, cardId,
                    assets.MediaType == "video" && assets.HasAudio);
            }
            if (creationRequest is not null)
                await CompleteCreationRequestAsync(connection, transaction, creationRequest, cardId);
            await transaction.CommitAsync();

            return new CardCreationResult(CardCreationStatus.Created, BuildCreatedCardDetails(
                cardId,
                data.Type,
                data.Title,
                previewPath,
                metadata,
                data.PropertiesJson,
                data.Relations));
        }
        catch (MySqlException exception) when (creationRequest is not null && cardId == 0 &&
            exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync();
            return await GetCreationResultAsync(creationRequest, creationRequest.Hash);
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
        JsonObject metadata,
        string? contentSha256 = null)
    {
        await connection.ExecuteAsync(
            """
            UPDATE cards
            SET preview = @Preview,
                metadata = @Metadata,
                content_sha256 = COALESCE(@Sha, content_sha256)
            WHERE id = @Id;
            """,
            new
            {
                Id = cardId,
                Sha = contentSha256,
                Preview = previewPath,
                Metadata = metadata.ToJsonString(),
            },
            transaction);
    }

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
            [])
        {
            PlaybackUrl = metadata["playback_path"]?.GetValue<string>() is null ? null : $"/api/cards/{cardId}/playback",
            PosterUrl = metadata["poster_path"]?.GetValue<string>() is null ? null : $"/api/cards/{cardId}/poster",
        };
    }
}

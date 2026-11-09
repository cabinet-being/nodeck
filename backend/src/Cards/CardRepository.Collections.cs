using System.Data;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using MyApp.Api.Analysis;
using static MyApp.Api.Cards.CardMetadata;

namespace MyApp.Api.Cards;

public sealed partial class CardRepository
{
    private sealed class CollectionChildRow
    {
        public long Id { get; init; }
        public string Type { get; init; } = "";
        public string? Preview { get; init; }
    }

    public async Task<CardDetails> CreateCollectionAsync(
        CreateCardCollectionData data,
        Func<long, IFormFile, Task<CardFileAssets>> createAssets,
        Action<long> cleanupAssets)
        => (await CreateCollectionAsync(data, createAssets, cleanupAssets, null)).Card!;

    public async Task<CardCreationResult> CreateCollectionAsync(
        CreateCardCollectionData data,
        Func<long, IFormFile, Task<CardFileAssets>> createAssets,
        Action<long> cleanupAssets,
        CardCreationRequest? creationRequest)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var createdCardIds = new List<long>();

        try
        {
            if (creationRequest is not null)
                await InsertCreationRequestAsync(connection, transaction, creationRequest);
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
                await RefreshImageAnalysisJobAsync(connection, transaction, mediaId, assets.MediaType);
                await SpeechAnalysisQueueRepository.RefreshAsync(connection, transaction, mediaId,
                    assets.MediaType == "video" && assets.HasAudio);

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
            if (creationRequest is not null)
                await CompleteCreationRequestAsync(connection, transaction, creationRequest, parentId);
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

            return new CardCreationResult(CardCreationStatus.Created, new CardDetails(
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
                containedCards));
        }
        catch (MySqlException exception) when (creationRequest is not null && createdCardIds.Count == 0 &&
            exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync();
            return await GetCreationResultAsync(creationRequest, creationRequest.Hash);
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

    public async Task<CardDetails> CreateCollectionFromExistingAsync(CreateExistingCollectionRequest request)
        => (await CreateCollectionFromExistingAsync(request, null)).Card!;

    public async Task<CardCreationResult> CreateCollectionFromExistingAsync(
        CreateExistingCollectionRequest request, CardCreationRequest? creationRequest)
    {
        if (request.Type is not ("comic" or "set") || request.ChildCardIds is null || request.ChildCardIds.Count < 2)
            throw new InvalidOperationException("A comic or set requires at least two existing media cards.");

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        long id = 0;
        try
        {
            if (creationRequest is not null)
                await InsertCreationRequestAsync(connection, transaction, creationRequest);
            id = await InsertCardAsync(connection, transaction, request.Type, request.Title, null, CreateMetadata());
            await ReplaceCollectionChildrenAsync(connection, transaction, id, request.Type, request.ChildCardIds);
            if (creationRequest is not null)
                await CompleteCreationRequestAsync(connection, transaction, creationRequest, id);
            await transaction.CommitAsync();
        }
        catch (MySqlException exception) when (creationRequest is not null && id == 0 &&
            exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync();
            return await GetCreationResultAsync(creationRequest, creationRequest.Hash);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        return new CardCreationResult(CardCreationStatus.Created,
            await GetByIdAsync(id) ?? throw new InvalidOperationException("Created collection could not be read."));
    }

    public async Task<CardDetails?> UpdateCollectionChildrenAsync(long id, IReadOnlyList<long> childCardIds)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var type = await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT type FROM cards WHERE id = @Id FOR UPDATE;", new { Id = id }, transaction);
            if (type is null)
            {
                await transaction.RollbackAsync();
                return null;
            }
            if (type is not ("comic" or "set")) throw new InvalidOperationException("Only comic and set cards have children.");
            await ReplaceCollectionChildrenAsync(connection, transaction, id, type, childCardIds);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        return await GetByIdAsync(id);
    }

    private static async Task ReplaceCollectionChildrenAsync(
        IDbConnection connection, IDbTransaction transaction, long parentId, string parentType, IReadOnlyList<long> childIds)
    {
        if (childIds is null || childIds.Any(id => id <= 0) || childIds.Distinct().Count() != childIds.Count)
            throw new InvalidOperationException("Child media IDs must be positive and unique.");

        var previousIds = (await connection.QueryAsync<long>(
            "SELECT to_card_id FROM card_relations WHERE from_card_id = @ParentId AND relation_type = 'contains' FOR UPDATE;",
            new { ParentId = parentId }, transaction)).ToArray();

        CollectionChildRow[] children = childIds.Count == 0 ? [] : (await connection.QueryAsync<CollectionChildRow>(
            "SELECT id AS Id, type AS Type, preview AS Preview FROM cards WHERE id IN @Ids ORDER BY id FOR UPDATE;",
            new { Ids = childIds.ToArray() }, transaction)).ToArray();
        if (children.Length != childIds.Count || children.Any(c => c.Type != "media"))
            throw new InvalidOperationException("Every child must be an existing media card.");

        if (childIds.Count > 0)
        {
            var conflictingId = await connection.QueryFirstOrDefaultAsync<long?>(
                """
                SELECT to_card_id FROM card_relations
                WHERE relation_type = 'contains' AND to_card_id IN @Ids AND from_card_id <> @ParentId
                LIMIT 1 FOR UPDATE;
                """,
                new { Ids = childIds.ToArray(), ParentId = parentId }, transaction);
            if (conflictingId is not null)
                throw new InvalidOperationException($"Media card {conflictingId} already belongs to a comic or set.");
        }

        if (previousIds.Length > 0)
            await connection.ExecuteAsync(
                "DELETE FROM card_relations WHERE relation_type = 'next_in_sequence' AND from_card_id IN @Ids AND to_card_id IN @Ids;",
                new { Ids = previousIds }, transaction);

        await connection.ExecuteAsync(
            "DELETE FROM card_relations WHERE from_card_id = @ParentId AND relation_type = 'contains';",
            new { ParentId = parentId }, transaction);

        for (var index = 0; index < childIds.Count; index++)
        {
            await InsertRelationsAsync(connection, transaction, parentId,
                [new CreateRelationData(childIds[index], "contains", new JsonObject { ["position"] = index }.ToJsonString(), CreateMetadata())],
                validateRelations: false);
            if (parentType == "comic" && index > 0)
                await InsertRelationsAsync(connection, transaction, childIds[index - 1],
                    [new CreateRelationData(childIds[index], "next_in_sequence", null, CreateMetadata())],
                    validateRelations: false);
        }

        var metadataJson = await connection.QuerySingleAsync<string>(
            "SELECT metadata FROM cards WHERE id = @ParentId;", new { ParentId = parentId }, transaction);
        var metadata = ParseRequiredJsonObject(metadataJson);
        var preview = childIds.Count == 0 ? null : children.Single(c => c.Id == childIds[0]).Preview;
        metadata["preview_path"] = preview;
        metadata["contained_count"] = childIds.Count;
        await UpdateCardAssetsAsync(connection, transaction, parentId, preview, metadata);
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
}

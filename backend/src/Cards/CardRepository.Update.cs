using Dapper;
using MySqlConnector;
using MyApp.Api.Analysis;
using MyApp.Api.Storage;
using static MyApp.Api.Cards.CardMapping;
using static MyApp.Api.Cards.CardMetadata;
using static MyApp.Api.Cards.CardSql;

namespace MyApp.Api.Cards;

public sealed partial class CardRepository
{
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
                    metadata = @Metadata,
                    content_sha256 = COALESCE(@Sha, content_sha256)
                WHERE id = @Id;
                """,
                new
                {
                    Id = id,
                    Sha = data.Assets?.ContentSha256,
                    data.Title,
                    Preview = previewPath,
                    Properties = data.PropertiesJson,
                    Metadata = metadata.ToJsonString(),
                },
                transaction);

            if (data.Assets is not null && card.Type == "media")
            {
                await RefreshImageAnalysisJobAsync(connection, transaction, id, data.Assets.MediaType);
                await SpeechAnalysisQueueRepository.RefreshAsync(connection, transaction, id,
                    data.Assets.MediaType == "video" && data.Assets.HasAudio);
                await connection.ExecuteAsync(
                    """
                    UPDATE cards parent
                    JOIN card_relations r ON r.from_card_id = parent.id
                    SET parent.preview = @Preview,
                        parent.metadata = JSON_SET(parent.metadata, '$.preview_path', @Preview)
                    WHERE r.to_card_id = @Id AND r.relation_type = 'contains'
                      AND CAST(JSON_UNQUOTE(JSON_EXTRACT(r.properties, '$.position')) AS SIGNED) = 0;
                    """,
                    new { Id = id, Preview = previewPath }, transaction);
            }

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
            if (data.Assets is not null && card.Type == "media")
                await SyncMetaTagsAsync(connection, id);

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

    public async Task<CardDetails?> UpdatePreviewStartAsync(
        long id,
        double? startSeconds,
        Func<long, string, double, double?, Task<PendingPreviewReplacement>> preparePreview)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        PendingPreviewReplacement? replacement = null;
        var committed = false;

        try
        {
            var card = await connection.QuerySingleOrDefaultAsync<DbCard>(
                $"""
                SELECT id AS Id, type AS Type, title AS Title, preview AS Preview,
                       properties AS Properties, metadata AS Metadata,
                       {FavoriteSelectSql("cards.id")} AS IsFavorite
                FROM cards WHERE id = @Id FOR UPDATE;
                """,
                new { Id = id }, transaction);

            if (card is null) return null;

            var metadata = ParseRequiredJsonObject(card.Metadata);
            var mediaType = metadata["media_type"]?.GetValue<string>();
            var contentPath = metadata["content_path"]?.GetValue<string>();
            var duration = metadata["duration"]?.GetValue<double?>();
            if (card.Type != "media" || mediaType is not ("video" or "gif") ||
                string.IsNullOrWhiteSpace(contentPath) || duration is null ||
                !double.IsFinite(duration.Value) || duration.Value < 0)
                throw new InvalidOperationException("Card must have video or GIF content with a valid duration.");

            var latestStart = Math.Max(0, duration.Value - Math.Min(3, duration.Value));
            if (startSeconds is not null &&
                (!double.IsFinite(startSeconds.Value) || startSeconds.Value < 0 || startSeconds.Value > latestStart))
                throw new InvalidOperationException($"startSeconds must be between 0 and {latestStart}.");

            replacement = await preparePreview(id, contentPath, duration.Value, startSeconds);
            metadata["preview_start_seconds"] = replacement.PreviewStartSeconds;
            metadata["preview_start_manual"] = startSeconds is not null;
            metadata["preview_selection"] = replacement.PreviewSelection;
            if (replacement.PreviewScore is { } previewScore) metadata["preview_score"] = previewScore;
            else metadata.Remove("preview_score");
            var previewPath = Path.Combine("cache", "cards", id.ToString(), replacement.PreviewFileName).Replace('\\', '/');
            var posterPath = Path.Combine("cache", "cards", id.ToString(), "poster.webp").Replace('\\', '/');
            metadata["preview_path"] = previewPath;
            metadata["poster_path"] = posterPath;

            await connection.ExecuteAsync(
                "UPDATE cards SET preview = @Preview, metadata = @Metadata WHERE id = @Id;",
                new { Id = id, Preview = previewPath, Metadata = metadata.ToJsonString() }, transaction);

            var outgoing = await GetRelationsAsync(connection, "from_card_id = @Id", id, transaction);
            var incoming = await GetRelationsAsync(connection, "to_card_id = @Id", id, transaction);
            var contained = await GetContainedCardsAsync(connection, id, transaction);
            await replacement.PromoteAsync();
            await transaction.CommitAsync();
            committed = true;
            try { replacement.CleanupAfterCommit(); }
            catch { /* Preview metadata is committed; old-file cleanup can be retried separately. */ }

            return ToDetails(card with { Preview = previewPath, Metadata = metadata.ToJsonString() }, outgoing, incoming, contained);
        }
        catch
        {
            if (!committed)
            {
                try
                {
                    await transaction.RollbackAsync();
                }
                finally
                {
                    replacement?.Rollback();
                }
            }
            throw;
        }
    }

    private static async Task RefreshImageAnalysisJobAsync(
        MySqlConnection connection, MySqlTransaction transaction, long cardId, string mediaType)
    {
        await connection.ExecuteAsync("""
            DELETE FROM card_tag_suggestions
            WHERE card_id = @CardId AND state = 'pending';
            """, new { CardId = cardId }, transaction);

        if (mediaType is not ("image" or "gif" or "video"))
        {
            await connection.ExecuteAsync(
                "DELETE FROM card_image_analysis_jobs WHERE card_id = @CardId;",
                new { CardId = cardId }, transaction);
            return;
        }

        await connection.ExecuteAsync("""
            INSERT INTO card_image_analysis_jobs
                (card_id, revision, state, attempts, available_at_utc,
                 lease_token, lease_until_utc, last_error)
            VALUES (@CardId, @Revision, 'pending', 0, UTC_TIMESTAMP(6), NULL, NULL, NULL)
            ON DUPLICATE KEY UPDATE
                revision = VALUES(revision), state = 'pending', attempts = 0,
                available_at_utc = UTC_TIMESTAMP(6), lease_token = NULL,
                lease_until_utc = NULL, last_error = NULL, priority = 0;
            """, new { CardId = cardId, Revision = Guid.NewGuid().ToString("D") }, transaction);
    }
}

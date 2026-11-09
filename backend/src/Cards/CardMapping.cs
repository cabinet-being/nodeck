using static MyApp.Api.Cards.CardMetadata;

namespace MyApp.Api.Cards;

internal static class CardMapping
{
    internal static CardRelationEntry ToRelationEntry(DbRelationLinkRow row)
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

    internal static CardSummary ToSummary(DbCard card)
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
            card.IsFavorite != 0)
        {
            PlaybackUrl = metadata["playback_path"]?.GetValue<string>() is null ? null : $"/api/cards/{card.Id}/playback",
            PosterUrl = metadata["poster_path"]?.GetValue<string>() is null ? null : $"/api/cards/{card.Id}/poster",
        };
    }

    internal static CardDetails ToDetails(
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
            containedCards)
        {
            PlaybackUrl = summary.PlaybackUrl,
            PosterUrl = summary.PosterUrl,
        };
    }

    internal static CardRelationResponse ToRelation(DbCardRelation relation)
    {
        return new CardRelationResponse(
            relation.Id,
            relation.FromCardId,
            relation.ToCardId,
            relation.RelationType,
            ParseOptionalJson(relation.Properties),
            ParseRequiredJson(relation.Metadata));
    }

    internal static ContainedCardResponse ToContainedCard(ContainedCardRow card)
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
            card.Position)
        {
            PlaybackUrl = metadata["playback_path"]?.GetValue<string>() is null ? null : $"/api/cards/{card.Id}/playback",
            PosterUrl = metadata["poster_path"]?.GetValue<string>() is null ? null : $"/api/cards/{card.Id}/poster",
        };
    }
}

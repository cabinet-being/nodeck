using System.Text.Json.Nodes;

namespace MyApp.Api.Cards;

public sealed record CardSummary(
    long Id,
    string Type,
    string? Title,
    string? PreviewUrl,
    string? ContentUrl,
    JsonNode? Properties,
    JsonNode Metadata,
    bool IsFavorite)
{
    public string? PlaybackUrl { get; init; }
    public string? PosterUrl { get; init; }
    public int? DeckPosition { get; init; }
    public JsonNode? MembershipProperties { get; init; }
}

public sealed record CardDetails(
    long Id,
    string Type,
    string? Title,
    string? PreviewUrl,
    string? ContentUrl,
    JsonNode? Properties,
    JsonNode Metadata,
    bool IsFavorite,
    IReadOnlyList<CardRelationResponse> OutgoingRelations,
    IReadOnlyList<CardRelationResponse> IncomingRelations,
    IReadOnlyList<ContainedCardResponse> ContainedCards)
{
    public string? PlaybackUrl { get; init; }
    public string? PosterUrl { get; init; }
}

public sealed record ContainedCardResponse(
    long Id,
    string Type,
    string? Title,
    string? PreviewUrl,
    string? ContentUrl,
    bool IsFavorite,
    int Position)
{
    public string? PlaybackUrl { get; init; }
    public string? PosterUrl { get; init; }
    /// <summary>"image", "gif" or "video" from the media metadata; null when unknown.</summary>
    public string? MediaType { get; init; }
}

public sealed record RelationCardSummary(
    long Id,
    string Type,
    string? Title,
    string? PreviewUrl);

public sealed record CardRelationEntry(
    long Id,
    string RelationType,
    string Direction,
    RelationCardSummary RelatedCard,
    JsonNode? Properties);

public sealed record CardRelationsResponse(
    IReadOnlyList<CardRelationEntry> OutgoingRelations,
    IReadOnlyList<CardRelationEntry> IncomingRelations);

public sealed record CreateCardRelationRequest(
    long ToCardId,
    string RelationType,
    JsonObject? Properties);

public sealed record UpdateCardRelationRequest(
    JsonObject? Properties);

public sealed record CardRelationResponse(
    long Id,
    long FromCardId,
    long ToCardId,
    string RelationType,
    JsonNode? Properties,
    JsonNode Metadata);

public sealed record CreateCardData(
    string Type,
    string? Title,
    string? PropertiesJson,
    JsonObject Metadata,
    IReadOnlyList<CreateRelationData> Relations);

public sealed record CreateCardCollectionData(
    string Type,
    string? Title,
    string? PropertiesJson,
    JsonObject Metadata,
    IReadOnlyList<IFormFile> Images,
    IReadOnlyList<CreateRelationData> Relations);

public sealed record CreateExistingCollectionRequest(string Type, string? Title, IReadOnlyList<long> ChildCardIds);
public sealed record UpdateCollectionChildrenRequest(IReadOnlyList<long> ChildCardIds);
public sealed record DeletionImpact(long CardCount, long DeckMembershipCount);
public sealed record CardDeletionPreview(DeletionImpact ParentOnly, DeletionImpact WithContents);

public sealed record UpdateCardData(
    string? Title,
    string? PropertiesJson,
    IReadOnlyList<CreateRelationData> Relations,
    CardFileAssets? Assets);

public sealed record CreateRelationData(
    long ToCardId,
    string RelationType,
    string? PropertiesJson,
    JsonObject Metadata);

public sealed record CardFileAssets(
    string ContentPath,
    string PreviewPath,
    string MediaType,
    string MimeType,
    string OriginalFileName,
    long FileSize,
    int Width,
    int Height,
    double? Duration = null,
    int? FrameCount = null,
    bool HasAudio = false,
    string? Container = null,
    string? VideoCodec = null,
    string? AudioCodec = null,
    string? PlaybackPath = null,
    int? AudioChannels = null,
    int? AudioSampleRate = null,
    string? PosterPath = null,
    double? PreviewStartSeconds = null,
    string? PreviewSelection = null,
    double? PreviewScore = null,
    double? FrameRate = null,
    bool? Animated = null,
    string? ContentSha256 = null);

internal sealed record DbCard(
    long Id,
    string Type,
    string? Title,
    string? Preview,
    string? Properties,
    string Metadata,
    long IsFavorite);

internal sealed record DbCardType(
    long Id,
    string Type);

internal sealed record DbCardRelation(
    long Id,
    long FromCardId,
    long ToCardId,
    string RelationType,
    string? Properties,
    string Metadata);

internal sealed record DbRelationLinkRow(
    long Id,
    string RelationType,
    string Direction,
    long RelatedCardId,
    string RelatedCardType,
    string? RelatedCardTitle,
    string? RelatedCardPreview,
    string RelatedCardMetadata,
    string? Properties);

internal sealed class ContainedCardRow
{
    public long Id { get; init; }

    public string Type { get; init; } = "";

    public string? Title { get; init; }

    public string? Preview { get; init; }

    public string Metadata { get; init; } = "";

    public long IsFavorite { get; init; }

    public int Position { get; init; }
}

using System.Text.Json.Nodes;

namespace MyApp.Api.Cards;

public sealed record CardSummary(
    long Id,
    string Type,
    string? Title,
    string? PreviewUrl,
    string? ContentUrl,
    JsonNode? Properties,
    JsonNode Metadata);

public sealed record CardDetails(
    long Id,
    string Type,
    string? Title,
    string? PreviewUrl,
    string? ContentUrl,
    JsonNode? Properties,
    JsonNode Metadata,
    IReadOnlyList<CardRelationResponse> OutgoingRelations,
    IReadOnlyList<CardRelationResponse> IncomingRelations,
    IReadOnlyList<ContainedCardResponse> ContainedCards);

public sealed record ContainedCardResponse(
    long Id,
    string Type,
    string? Title,
    string? PreviewUrl,
    string? ContentUrl,
    int Position);

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
    int Height);

internal sealed record DbCard(
    long Id,
    string Type,
    string? Title,
    string? Preview,
    string? Properties,
    string Metadata);

internal sealed record DbCardRelation(
    long Id,
    long FromCardId,
    long ToCardId,
    string RelationType,
    string? Properties,
    string Metadata);

internal sealed class ContainedCardRow
{
    public long Id { get; init; }

    public string Type { get; init; } = "";

    public string? Title { get; init; }

    public string? Preview { get; init; }

    public string Metadata { get; init; } = "";

    public int Position { get; init; }
}

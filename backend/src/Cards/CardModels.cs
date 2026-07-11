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
    IReadOnlyList<CardRelationResponse> IncomingRelations);

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

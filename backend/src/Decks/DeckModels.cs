using System.Text.Json.Nodes;

namespace MyApp.Api.Decks;

public sealed record DeckSummary(
    long Id,
    string Title,
    long CardCount,
    JsonNode? Properties,
    JsonNode Metadata,
    string? SystemKey);

public sealed record DeckDetails(
    long Id,
    string Title,
    JsonNode? Properties,
    JsonNode Metadata,
    string? SystemKey,
    IReadOnlyList<DeckCardResponse> Cards);

public sealed record DeckCardResponse(
    long Id,
    string Type,
    string? Title,
    string? PreviewUrl,
    string? ContentUrl,
    int Position,
    JsonNode? Properties,
    JsonNode Metadata,
    JsonNode? MembershipProperties);

public sealed record DeckRequest(
    string? Title,
    JsonObject? Properties,
    IReadOnlyList<DeckCardInput>? Cards);

public sealed record DeckCardInput(
    long CardId,
    JsonObject? Properties);

internal sealed record DbDeck(
    long Id,
    string Title,
    string? Properties,
    string Metadata,
    string? SystemKey);

internal sealed record DbDeckSummary(
    long Id,
    string Title,
    long CardCount,
    string? Properties,
    string Metadata,
    string? SystemKey);

internal sealed record DbDeckCard(
    long Id,
    string Type,
    string? Title,
    string? Preview,
    string? CardProperties,
    string CardMetadata,
    int Position,
    string? MembershipProperties);

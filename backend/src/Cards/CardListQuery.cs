namespace MyApp.Api.Cards;

public sealed record CardListQuery(
    IReadOnlyList<string> Types,
    string? MediaType,
    string? Search,
    bool ExcludeContainedMedia,
    IReadOnlyList<long> TagIds,
    long? SourceId,
    IReadOnlyList<long> CatalogTagIds,
    long? CatalogSourceId,
    string Sort,
    string Order,
    int Limit = 200,
    int Offset = 0,
    long? DeckId = null);

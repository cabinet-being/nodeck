namespace MyApp.Api.Cards;

public static class CardTypes
{
    public static readonly string[] SupportedCardTypes =
    [
        "media",
        "comic",
        "set",
        "tag",
        "source",
    ];

    public static readonly string[] SupportedRelationTypes =
    [
        "tagged_with",
        "sourced_from",
        "contains",
        "related_to",
        "preview_for",
    ];

    public static bool IsSupportedCardType(string? type) =>
        !string.IsNullOrWhiteSpace(type) && SupportedCardTypes.Contains(type);

    public static bool IsSupportedRelationType(string? type) =>
        !string.IsNullOrWhiteSpace(type) && SupportedRelationTypes.Contains(type);
}

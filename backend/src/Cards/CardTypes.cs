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
        "next_in_sequence",
    ];

    public static bool IsSupportedCardType(string? type) =>
        !string.IsNullOrWhiteSpace(type) && SupportedCardTypes.Contains(type);

    public static bool IsSupportedRelationType(string? type) =>
        !string.IsNullOrWhiteSpace(type) && SupportedRelationTypes.Contains(type);

    internal static bool IsGenericEditableRelationType(string relationType) =>
        relationType is "tagged_with" or "sourced_from" or "related_to";

    internal static bool IsTaggableCardType(string type) =>
        type is "media" or "comic" or "set" or "source";

    internal static bool IsSourceableCardType(string type) =>
        type is "media" or "comic" or "set";
}

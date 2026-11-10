namespace MyApp.Api.Catalog;

public sealed record TagRequest(string? Name, string[]? Aliases, string? Category = null);
public sealed record SourceRequest(string? Name);

public sealed class TagEntry
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Origin { get; set; } = "";
    public bool NameConflict { get; set; }
    public long ReferenceCount { get; set; }
    public string[] Aliases { get; set; } = [];
}

public sealed class SourceEntry
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsLegacy { get; set; }
    public bool NameConflict { get; set; }
    public long ReferenceCount { get; set; }
}

public sealed class CatalogValidationException(string message) : Exception(message);
public sealed class CatalogConflictException(string message) : Exception(message);
public sealed class CatalogMissingException(string message) : Exception(message);

public sealed record CardTagEntry(long Id, string Name, string Category, string Origin, string AssignmentSource);
public sealed record TagSuggestionProposal(long TagId, decimal Confidence, string ModelKey);
public sealed record TagSuggestionEntry(long TagId, string Name, string Category, string Origin,
    decimal Confidence, string ModelKey);
public sealed record TagSuggestionDecisionRequest(string? Decision);
public sealed record TagSuggestionCorrectionRequest(long ReplacementTagId);
public sealed record TagSuggestionBatchDecisionRequest(string? Decision, long[]? TagIds);
public sealed record CardSourceEntry(long Id, string Name, string? OriginalUrl);
public sealed record CardSourceRequest(string? OriginalUrl);

namespace MyApp.Api.Catalog;

public sealed record TagRequest(string? Name, string[]? Aliases);
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
    public bool NameConflict { get; set; }
    public long ReferenceCount { get; set; }
}

public sealed class CatalogValidationException(string message) : Exception(message);
public sealed class CatalogConflictException(string message) : Exception(message);

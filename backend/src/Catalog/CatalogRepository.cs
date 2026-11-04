using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;

namespace MyApp.Api.Catalog;

public sealed class CatalogRepository
{
    private readonly string _connectionString;

    public CatalogRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
    }

    public async Task<IReadOnlyList<TagEntry>> ListTagsAsync(string? search)
    {
        await using var connection = await OpenAsync();
        var entries = (await connection.QueryAsync<TagEntry>("""
            SELECT t.id, t.name, t.category, t.origin, t.name_conflict AS NameConflict,
                   (SELECT COUNT(*) FROM card_tags ct WHERE ct.tag_id = t.id) AS ReferenceCount
            FROM tags t
            WHERE @Search IS NULL OR LOCATE(@Search, t.name) > 0
                OR EXISTS (SELECT 1 FROM tag_aliases a WHERE a.tag_id = t.id AND LOCATE(@Search, a.alias) > 0)
            ORDER BY t.name, t.id LIMIT 200;
            """, new { Search = NormalizeSearch(search) })).ToList();
        await AddAliasesAsync(connection, entries);
        return entries;
    }

    public async Task<TagEntry?> GetTagAsync(long id)
    {
        await using var connection = await OpenAsync();
        var entry = await connection.QuerySingleOrDefaultAsync<TagEntry>("""
            SELECT t.id, t.name, t.category, t.origin, t.name_conflict AS NameConflict,
                   (SELECT COUNT(*) FROM card_tags ct WHERE ct.tag_id = t.id) AS ReferenceCount
            FROM tags t WHERE t.id = @Id;
            """, new { Id = id });
        if (entry is not null)
            await AddAliasesAsync(connection, [entry]);
        return entry;
    }

    public async Task<TagEntry> CreateTagAsync(TagRequest request)
    {
        var (name, aliases) = ValidateTag(request);
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await EnsureTagWordsAvailableAsync(connection, transaction, name, aliases, null);
            var id = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO tags (name, canonical_key, category, origin)
                VALUES (@Name, @Key, 'custom', 'user');
                SELECT LAST_INSERT_ID();
                """, new { Name = name, Key = "user-tag:" + name.ToLowerInvariant() }, transaction);
            await InsertAliasesAsync(connection, transaction, id, aliases);
            await transaction.CommitAsync();
            return (await GetTagAsync(id))!;
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new CatalogConflictException("A tag with that name or alias already exists.");
        }
    }

    public async Task<TagEntry?> UpdateTagAsync(long id, TagRequest request)
    {
        var (name, aliases) = ValidateTag(request);
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var existing = await connection.QuerySingleOrDefaultAsync<TagProtectionRow>(
                "SELECT legacy_card_id, category, origin FROM tags WHERE id = @Id FOR UPDATE;",
                new { Id = id }, transaction);
            if (existing is null)
                return null;
            if (existing.LegacyCardId is not null || existing.Category != "custom" || existing.Origin != "user")
                throw new CatalogConflictException("This tag is managed by the legacy catalog or an import.");

            await EnsureTagWordsAvailableAsync(connection, transaction, name, aliases, id);
            await connection.ExecuteAsync("""
                UPDATE tags SET name = @Name, canonical_key = @Key WHERE id = @Id;
                DELETE FROM tag_aliases WHERE tag_id = @Id;
                """, new { Id = id, Name = name, Key = "user-tag:" + name.ToLowerInvariant() }, transaction);
            await InsertAliasesAsync(connection, transaction, id, aliases);
            await transaction.CommitAsync();
            return await GetTagAsync(id);
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new CatalogConflictException("A tag with that name or alias already exists.");
        }
    }

    public async Task<bool> DeleteTagAsync(long id)
    {
        await using var connection = await OpenAsync();
        var affected = await connection.ExecuteAsync("""
            DELETE FROM tags WHERE id = @Id AND legacy_card_id IS NULL
                AND category = 'custom' AND origin = 'user'
                AND NOT EXISTS (SELECT 1 FROM card_tags WHERE tag_id = @Id);
            """, new { Id = id });
        if (affected > 0) return true;
        if (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tags WHERE id = @Id;", new { Id = id }) == 0)
            return false;
        throw new CatalogConflictException("This tag is protected or still assigned to cards.");
    }

    public async Task<IReadOnlyList<SourceEntry>> ListSourcesAsync(string? search)
    {
        await using var connection = await OpenAsync();
        return (await connection.QueryAsync<SourceEntry>("""
            SELECT s.id, s.name, s.name_conflict AS NameConflict,
                   (SELECT COUNT(*) FROM card_sources cs WHERE cs.source_id = s.id) AS ReferenceCount
            FROM sources s WHERE @Search IS NULL OR LOCATE(@Search, s.name) > 0
            ORDER BY s.name, s.id LIMIT 200;
            """, new { Search = NormalizeSearch(search) })).ToList();
    }

    public async Task<SourceEntry?> GetSourceAsync(long id)
    {
        await using var connection = await OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<SourceEntry>("""
            SELECT s.id, s.name, s.name_conflict AS NameConflict,
                   (SELECT COUNT(*) FROM card_sources cs WHERE cs.source_id = s.id) AS ReferenceCount
            FROM sources s WHERE s.id = @Id;
            """, new { Id = id });
    }

    public async Task<SourceEntry> CreateSourceAsync(SourceRequest request)
    {
        var name = ValidateName(request.Name);
        await using var connection = await OpenAsync();
        await EnsureSourceNameAvailableAsync(connection, name, null);
        try
        {
            var id = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO sources (name, canonical_key) VALUES (@Name, @Key);
                SELECT LAST_INSERT_ID();
                """, new { Name = name, Key = "user-source:" + name.ToLowerInvariant() });
            return (await GetSourceAsync(id))!;
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new CatalogConflictException("A source with that name already exists.");
        }
    }

    public async Task<SourceEntry?> UpdateSourceAsync(long id, SourceRequest request)
    {
        var name = ValidateName(request.Name);
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var existing = await connection.QuerySingleOrDefaultAsync<SourceProtectionRow>(
                "SELECT id, legacy_card_id FROM sources WHERE id = @Id FOR UPDATE;",
                new { Id = id }, transaction);
            if (existing is null) return null;
            if (existing.LegacyCardId is not null)
                throw new CatalogConflictException("This source is managed by the legacy catalog.");
            await EnsureSourceNameAvailableAsync(connection, name, id, transaction);
            await connection.ExecuteAsync("UPDATE sources SET name = @Name, canonical_key = @Key WHERE id = @Id;",
                new { Id = id, Name = name, Key = "user-source:" + name.ToLowerInvariant() }, transaction);
            await transaction.CommitAsync();
            return await GetSourceAsync(id);
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new CatalogConflictException("A source with that name already exists.");
        }
    }

    public async Task<bool> DeleteSourceAsync(long id)
    {
        await using var connection = await OpenAsync();
        var affected = await connection.ExecuteAsync("""
            DELETE FROM sources WHERE id = @Id AND legacy_card_id IS NULL
                AND NOT EXISTS (SELECT 1 FROM card_sources WHERE source_id = @Id);
            """, new { Id = id });
        if (affected > 0) return true;
        if (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sources WHERE id = @Id;", new { Id = id }) == 0)
            return false;
        throw new CatalogConflictException("This source is protected or still assigned to cards.");
    }

    private async Task<MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string? NormalizeSearch(string? search) =>
        string.IsNullOrWhiteSpace(search) ? null : search.Trim();

    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new CatalogValidationException("Name is required.");
        var normalized = Regex.Replace(name.Trim().Normalize(NormalizationForm.FormC), @"\s+", " ");
        if (normalized.Length > 255)
            throw new CatalogValidationException("Name must be at most 255 characters.");
        return normalized;
    }

    private static (string Name, string[] Aliases) ValidateTag(TagRequest request)
    {
        var name = ValidateName(request.Name);
        var aliases = (request.Aliases ?? []).Select(ValidateName).ToArray();
        if (aliases.Length > 50)
            throw new CatalogValidationException("A tag can have at most 50 aliases.");
        if (aliases.Append(name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != aliases.Length + 1)
            throw new CatalogValidationException("Tag names and aliases must be distinct.");
        return (name, aliases);
    }

    private static async Task EnsureTagWordsAvailableAsync(
        MySqlConnection connection, MySqlTransaction transaction, string name, string[] aliases, long? id)
    {
        var words = aliases.Append(name).ToArray();
        var count = await connection.ExecuteScalarAsync<long>("""
            SELECT (SELECT COUNT(*) FROM tags WHERE name IN @Words AND (@Id IS NULL OR id <> @Id))
                 + (SELECT COUNT(*) FROM tag_aliases WHERE alias IN @Words AND (@Id IS NULL OR tag_id <> @Id));
            """, new { Words = words, Id = id }, transaction);
        if (count > 0)
            throw new CatalogConflictException("A tag with that name or alias already exists.");
    }

    private static async Task EnsureSourceNameAvailableAsync(
        MySqlConnection connection, string name, long? id, MySqlTransaction? transaction = null)
    {
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sources WHERE name = @Name AND (@Id IS NULL OR id <> @Id);",
            new { Name = name, Id = id }, transaction);
        if (count > 0)
            throw new CatalogConflictException("A source with that name already exists.");
    }

    private static async Task AddAliasesAsync(MySqlConnection connection, IReadOnlyList<TagEntry> entries)
    {
        if (entries.Count == 0) return;
        var aliases = await connection.QueryAsync<(long TagId, string Alias)>(
            "SELECT tag_id, alias FROM tag_aliases WHERE tag_id IN @Ids ORDER BY alias;",
            new { Ids = entries.Select(entry => entry.Id).ToArray() });
        var byTag = aliases.GroupBy(alias => alias.TagId).ToDictionary(
            group => group.Key, group => group.Select(alias => alias.Alias).ToArray());
        foreach (var entry in entries)
            entry.Aliases = byTag.GetValueOrDefault(entry.Id) ?? [];
    }

    private static async Task InsertAliasesAsync(
        MySqlConnection connection, MySqlTransaction transaction, long tagId, string[] aliases)
    {
        foreach (var alias in aliases)
            await connection.ExecuteAsync("INSERT INTO tag_aliases (tag_id, alias) VALUES (@TagId, @Alias);",
                new { TagId = tagId, Alias = alias }, transaction);
    }

    private sealed class TagProtectionRow
    {
        public long? LegacyCardId { get; set; }
        public string Category { get; set; } = "";
        public string Origin { get; set; } = "";
    }

    private sealed class SourceProtectionRow
    {
        public long Id { get; set; }
        public long? LegacyCardId { get; set; }
    }
}

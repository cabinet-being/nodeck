using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using MySqlConnector;

namespace MyApp.Api.Catalog;

public sealed class DanbooruImportOptions
{
    public string UserAgent { get; init; } = "";
    public int PageSize { get; init; } = 1000;
    public int? MaxPages { get; init; }
    public bool Refresh { get; init; }
    public Uri BaseUri { get; init; } = new("https://danbooru.donmai.us/");
    public Action<DanbooruImportProgress>? OnPageCompleted { get; init; }
}

public sealed record DanbooruImportProgress(
    string Kind, int Page, long? CursorBeforeId, long Processed, long Conflicts, bool Completed);

public sealed record DanbooruImportResult(
    int Pages, long Processed, long Conflicts, bool TagsCompleted, bool AliasesCompleted);

/// <summary>Imports Danbooru's descending-ID tag and alias pages without changing local catalog entries.</summary>
public sealed class DanbooruTagImporter(string connectionString, HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _connectionString = !string.IsNullOrWhiteSpace(connectionString)
        ? connectionString : throw new ArgumentException("A connection string is required.", nameof(connectionString));
    private readonly HttpClient _httpClient = httpClient;
    private DateTimeOffset _nextRequestAt;

    public async Task<DanbooruImportResult> RunAsync(DanbooruImportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.UserAgent) || options.UserAgent.Contains('\r') || options.UserAgent.Contains('\n'))
            throw new ArgumentException("A stable User-Agent is required.", nameof(options));
        if (options.PageSize is < 1 or > 1000 || options.MaxPages is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "PageSize must be 1..1000 and MaxPages must be positive.");
        if (options.BaseUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("BaseUri must use HTTP or HTTPS.", nameof(options));

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var lockName = "nodeck-danbooru-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(connection.Database)))[..24];
        var acquired = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT GET_LOCK(@LockName, 30);", new { LockName = lockName }, cancellationToken: cancellationToken));
        if (acquired != 1)
            throw new InvalidOperationException("Could not acquire the Danbooru import lock.");
        try
        {
            return await RunLockedAsync(connection, options, cancellationToken);
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT RELEASE_LOCK(@LockName);", new { LockName = lockName }));
        }
    }

    private async Task<DanbooruImportResult> RunLockedAsync(
        MySqlConnection connection, DanbooruImportOptions options, CancellationToken cancellationToken)
    {
        await EnsureStatesAsync(connection, cancellationToken);
        var priorTags = await ReadStateAsync(connection, "tags", cancellationToken);
        var priorAliases = await ReadStateAsync(connection, "tag_aliases", cancellationToken);
        if (options.Refresh && priorTags.Completed && priorAliases.Completed)
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE danbooru_import_state
                SET cursor_before_id = NULL, completed = FALSE, processed = 0, conflicts = 0,
                    updated_at_utc = UTC_TIMESTAMP(6)
                WHERE kind IN ('tags', 'tag_aliases');
                """, cancellationToken: cancellationToken));

        var pages = 0;
        long processed = 0, conflicts = 0;
        foreach (var kind in new[] { "tags", "tag_aliases" })
        {
            var kindPages = 0;
            var state = await ReadStateAsync(connection, kind, cancellationToken);
            while (!state.Completed && (options.MaxPages is null || pages < options.MaxPages.Value))
            {
                var path = kind == "tags" ? "tags.json" : "tag_aliases.json";
                var query = $"?limit={options.PageSize}" + (state.CursorBeforeId is long cursor ? $"&page=b{cursor}" : "");
                using var response = await GetWithRetryAsync(new Uri(options.BaseUri, path + query), options.UserAgent, cancellationToken);
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Danbooru response must be an array.");
                var rows = document.RootElement;
                long? smallestId = null;
                long? previousId = null;
                long pageProcessed = 0, pageConflicts = 0;
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                foreach (var json in rows.EnumerateArray())
                {
                    var id = json.GetProperty("id").GetInt64();
                    if (id <= 0 || (state.CursorBeforeId is long before && id >= before)
                        || (previousId is long previous && id >= previous))
                        throw new JsonException("Danbooru returned an invalid or non-descending page ID.");
                    previousId = id;
                    smallestId = Math.Min(smallestId ?? id, id);
                    var outcome = kind == "tags"
                        ? await ApplyTagAsync(connection, transaction, json.Deserialize<DanbooruTag>(JsonOptions)!, cancellationToken)
                        : await ApplyAliasAsync(connection, transaction, json.Deserialize<DanbooruAlias>(JsonOptions)!, cancellationToken);
                    pageProcessed++;
                    if (!outcome) pageConflicts++;
                }
                var completed = rows.GetArrayLength() == 0;
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE danbooru_import_state
                    SET cursor_before_id = @Cursor, completed = @Completed,
                        processed = processed + @Processed, conflicts = conflicts + @Conflicts,
                        updated_at_utc = UTC_TIMESTAMP(6)
                    WHERE kind = @Kind;
                    """, new { Kind = kind, Cursor = smallestId ?? state.CursorBeforeId,
                        Completed = completed, Processed = pageProcessed, Conflicts = pageConflicts },
                    transaction, cancellationToken: cancellationToken));
                await transaction.CommitAsync(cancellationToken);
                pages++;
                kindPages++;
                processed += pageProcessed;
                conflicts += pageConflicts;
                state = state with { CursorBeforeId = smallestId ?? state.CursorBeforeId, Completed = completed };
                options.OnPageCompleted?.Invoke(new DanbooruImportProgress(
                    kind, kindPages, state.CursorBeforeId, pageProcessed, pageConflicts, completed));
            }
        }
        var tags = await ReadStateAsync(connection, "tags", cancellationToken);
        var aliases = await ReadStateAsync(connection, "tag_aliases", cancellationToken);
        return new DanbooruImportResult(pages, processed, conflicts, tags.Completed, aliases.Completed);
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(Uri uri, string userAgent, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var delay = _nextRequestAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            _nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(1);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 4)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(16, 1 << attempt));
                response.Dispose();
                await Task.Delay(retryAfter > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : retryAfter, cancellationToken);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); }
            catch { response.Dispose(); throw; }
            return response;
        }
    }

    private static async Task<bool> ApplyTagAsync(MySqlConnection db, MySqlTransaction tx, DanbooruTag tag, CancellationToken ct)
    {
        var name = tag.Name?.Trim();
        if (tag.Id <= 0 || string.IsNullOrWhiteSpace(name) || name.Length > 255)
            return false;
        var existing = await db.QuerySingleOrDefaultAsync<ImportedTag>(new CommandDefinition(
            "SELECT id, origin FROM tags WHERE danbooru_id = @Id FOR UPDATE;", new { tag.Id }, tx, cancellationToken: ct));
        if (existing is not null && existing.Origin != "danbooru") return false;
        if (tag.IsDeleted || tag.IsDeprecated)
        {
            if (existing is not null)
            {
                var removed = await db.ExecuteAsync(new CommandDefinition("""
                    DELETE FROM tags WHERE id = @Id AND origin = 'danbooru'
                    AND NOT EXISTS (SELECT 1 FROM card_tags WHERE tag_id = @Id)
                    AND NOT EXISTS (SELECT 1 FROM tag_aliases WHERE tag_id = @Id AND origin <> 'danbooru');
                    """, new { existing.Id }, tx, cancellationToken: ct));
                return removed == 1;
            }
            return true;
        }
        var category = tag.Category switch
        {
            0 => "general", 1 => "artist", 3 => "copyright", 4 => "character", 5 => "meta", _ => null
        };
        if (category is null) return false;
        var occupied = await db.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM tags WHERE name = @Name AND id <> @ExistingId)
                 + (SELECT COUNT(*) FROM tag_aliases WHERE alias = @Name);
            """, new { Name = name, ExistingId = existing?.Id ?? 0 }, tx, cancellationToken: ct));
        if (occupied != 0) return false;
        if (existing is null)
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO tags (name, canonical_key, category, origin, danbooru_id)
                VALUES (@Name, @Key, @Category, 'danbooru', @Id);
                """, new { Name = name, Key = $"danbooru-tag:{tag.Id}", Category = category, tag.Id }, tx, cancellationToken: ct));
        else
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE tags SET name = @Name, category = @Category WHERE id = @ExistingId;
                """, new { Name = name, Category = category, ExistingId = existing.Id }, tx, cancellationToken: ct));
        return true;
    }

    private static async Task<bool> ApplyAliasAsync(MySqlConnection db, MySqlTransaction tx, DanbooruAlias alias, CancellationToken ct)
    {
        if (alias.Id <= 0) return false;
        var existing = await db.QuerySingleOrDefaultAsync<ImportedAlias>(new CommandDefinition(
            "SELECT id, origin FROM tag_aliases WHERE danbooru_id = @Id FOR UPDATE;",
            new { alias.Id }, tx, cancellationToken: ct));
        if (existing is not null && existing.Origin != "danbooru") return false;
        if (!string.Equals(alias.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            if (existing is not null)
                await db.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM tag_aliases WHERE id = @Id AND origin = 'danbooru';",
                    new { existing.Id }, tx, cancellationToken: ct));
            return true;
        }
        var name = alias.AntecedentName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || string.IsNullOrWhiteSpace(alias.ConsequentName))
            return false;
        var target = await db.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT id FROM tags WHERE name = @Name AND origin = 'danbooru' LIMIT 1;
            """, new { Name = alias.ConsequentName }, tx, cancellationToken: ct));
        if (target is null) return false;
        var occupied = await db.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM tags WHERE name = @Name)
                 + (SELECT COUNT(*) FROM tag_aliases WHERE alias = @Name AND id <> @ExistingId);
            """, new { Name = name, ExistingId = existing?.Id ?? 0 }, tx, cancellationToken: ct));
        if (occupied != 0) return false;
        if (existing is null)
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO tag_aliases (tag_id, alias, origin, danbooru_id)
                VALUES (@TagId, @Name, 'danbooru', @Id);
                """, new { TagId = target, Name = name, alias.Id }, tx, cancellationToken: ct));
        else
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE tag_aliases SET tag_id = @TagId, alias = @Name WHERE id = @ExistingId;
                """, new { TagId = target, Name = name, ExistingId = existing.Id }, tx, cancellationToken: ct));
        return true;
    }

    private static async Task EnsureStatesAsync(MySqlConnection db, CancellationToken ct)
    {
        foreach (var kind in new[] { "tags", "tag_aliases" })
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT IGNORE INTO danbooru_import_state
                    (kind, cursor_before_id, completed, processed, conflicts, updated_at_utc)
                VALUES (@Kind, NULL, FALSE, 0, 0, UTC_TIMESTAMP(6));
                """, new { Kind = kind }, cancellationToken: ct));
    }

    private static Task<ImportState> ReadStateAsync(MySqlConnection db, string kind, CancellationToken ct) =>
        db.QuerySingleAsync<ImportState>(new CommandDefinition("""
            SELECT cursor_before_id AS CursorBeforeId, completed AS Completed
            FROM danbooru_import_state WHERE kind = @Kind;
            """, new { Kind = kind }, cancellationToken: ct));

    private sealed record ImportState(long? CursorBeforeId, bool Completed);
    private sealed record ImportedTag(long Id, string Origin);
    private sealed record ImportedAlias(long Id, string Origin);
    private sealed record DanbooruTag(long Id, string? Name, int Category,
        [property: JsonPropertyName("is_deleted")] bool IsDeleted,
        [property: JsonPropertyName("is_deprecated")] bool IsDeprecated);
    private sealed record DanbooruAlias(long Id,
        [property: JsonPropertyName("antecedent_name")] string? AntecedentName,
        [property: JsonPropertyName("consequent_name")] string? ConsequentName,
        string? Status);
}

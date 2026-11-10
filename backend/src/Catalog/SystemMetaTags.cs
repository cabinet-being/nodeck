using System.Data;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;

namespace MyApp.Api.Catalog;

/// <summary>Materialises Danbooru-style meta tags (origin 'system') from card file properties.
/// Card metadata stays the source of truth; the sync is idempotent.</summary>
public static class SystemMetaTags
{
    public const string Origin = "system";
    public const string AssignmentSource = "system";
    public const string KeyPrefix = "system-meta:";
    private const int BatchSize = 200;

    public static readonly string[] AllNames =
    [
        "image", "gif", "video", "animated", "sound", "silent",
        "horizontal", "vertical", "square_image", "highres", "absurdres", "lowres", "comic_book", "set",
    ];

    /// <summary>Computes the meta tag names a card should carry.</summary>
    public static IReadOnlyList<string> Compute(string cardType, JsonObject? metadata)
    {
        // "square" and "comic" are general Danbooru tags with other meanings, so the meta
        // names are distinct: square_image and comic_book.
        if (cardType == "comic") return ["comic_book"];
        if (cardType == "set") return ["set"];
        if (cardType != "media" || metadata is null) return [];
        var mediaType = metadata["media_type"]?.GetValue<string>();
        if (mediaType is not ("image" or "gif" or "video")) return [];
        var names = new List<string> { mediaType };
        if (mediaType is "gif" or "video" || ReadBool(metadata["animated"]) == true) names.Add("animated");
        if (mediaType == "video") names.Add(ReadBool(metadata["has_audio"]) == true ? "sound" : "silent");
        var width = ReadDouble(metadata["width"]);
        var height = ReadDouble(metadata["height"]);
        if (width is > 0 && height is > 0)
        {
            var ratio = width.Value / height.Value;
            names.Add(Math.Abs(ratio - 1) <= 0.05 ? "square_image" : ratio > 1 ? "horizontal" : "vertical");
            var longSide = Math.Max(width.Value, height.Value);
            if (longSide >= 3200) names.Add("absurdres");
            if (longSide >= 1600) names.Add("highres");
            if (longSide < 500) names.Add("lowres");
        }
        return names;
    }

    /// <summary>Syncs the system rows of the given cards. Returns the meta names skipped because a
    /// non-system tag already owns the name.</summary>
    public static async Task<IReadOnlyList<string>> SyncAsync(
        MySqlConnection connection, IReadOnlyCollection<long> cardIds, CancellationToken cancellationToken = default)
    {
        var skipped = new List<string>();
        if (cardIds.Count == 0) return skipped;
        var tagIds = await EnsureTagsAsync(connection, skipped, cancellationToken);
        foreach (var cardId in cardIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var card = await connection.QuerySingleOrDefaultAsync<CardRow>(new CommandDefinition(
                "SELECT type AS Type, CAST(metadata AS CHAR) AS Metadata FROM cards WHERE id = @CardId FOR UPDATE;",
                new { CardId = cardId }, transaction, cancellationToken: cancellationToken));
            if (card is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }
            var metadata = string.IsNullOrWhiteSpace(card.Metadata)
                ? null : JsonNode.Parse(card.Metadata) as JsonObject;
            var desired = Compute(card.Type, metadata)
                .Where(tagIds.ContainsKey).Select(name => tagIds[name]).ToHashSet();
            var existing = (await connection.QueryAsync<(long Id, long TagId)>(new CommandDefinition("""
                SELECT id, tag_id FROM card_tags
                WHERE card_id = @CardId AND assignment_source = 'system';
                """, new { CardId = cardId }, transaction, cancellationToken: cancellationToken))).ToList();
            var keep = new HashSet<long>();
            var stale = new List<long>();
            foreach (var row in existing)
                if (desired.Contains(row.TagId) && keep.Add(row.TagId)) continue;
                else stale.Add(row.Id);
            if (stale.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM card_tags WHERE id IN @Ids;", new { Ids = stale.ToArray() },
                    transaction, cancellationToken: cancellationToken));
            foreach (var tagId in desired.Where(id => !keep.Contains(id)))
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO card_tags (card_id, tag_id, assignment_source, confidence)
                    VALUES (@CardId, @TagId, 'system', NULL);
                    """, new { CardId = cardId, TagId = tagId }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        return skipped;
    }

    /// <summary>Syncs every card of the library in id order; returns (cards processed, skipped names).</summary>
    public static async Task<(int Cards, IReadOnlyList<string> Skipped)> SyncLibraryAsync(
        MySqlConnection connection, CancellationToken cancellationToken = default)
    {
        var processed = 0;
        long lastId = 0;
        var skipped = new HashSet<string>();
        while (true)
        {
            var ids = (await connection.QueryAsync<long>(new CommandDefinition("""
                SELECT id FROM cards WHERE type IN ('media', 'comic', 'set') AND id > @LastId
                ORDER BY id LIMIT @Limit;
                """, new { LastId = lastId, Limit = BatchSize }, cancellationToken: cancellationToken))).ToList();
            if (ids.Count == 0) break;
            foreach (var name in await SyncAsync(connection, ids, cancellationToken)) skipped.Add(name);
            processed += ids.Count;
            lastId = ids[^1];
        }
        return (processed, skipped.ToList());
    }

    private static async Task<Dictionary<string, long>> EnsureTagsAsync(
        MySqlConnection connection, List<string> skipped, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in AllNames)
        {
            var key = KeyPrefix + name;
            var ownId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT id FROM tags WHERE canonical_key = @Key;", new { Key = key }, cancellationToken: cancellationToken));
            if (ownId is not null)
            {
                result[name] = ownId.Value;
                continue;
            }
            var clash = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                SELECT (SELECT COUNT(*) FROM tags WHERE name = @Name)
                     + (SELECT COUNT(*) FROM tag_aliases WHERE alias = @Name);
                """, new { Name = name }, cancellationToken: cancellationToken));
            if (clash > 0)
            {
                skipped.Add(name);
                continue;
            }
            try
            {
                result[name] = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT INTO tags (name, canonical_key, category, origin)
                    VALUES (@Name, @Key, 'meta', 'system');
                    SELECT LAST_INSERT_ID();
                    """, new { Name = name, Key = key }, cancellationToken: cancellationToken));
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                result[name] = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM tags WHERE canonical_key = @Key;", new { Key = key },
                    cancellationToken: cancellationToken));
            }
        }
        return result;
    }

    private sealed class CardRow
    {
        public string Type { get; set; } = "";
        public string? Metadata { get; set; }
    }

    private static bool? ReadBool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static double? ReadDouble(JsonNode? node) =>
        node is JsonValue value && double.TryParse(value.ToString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number : null;
}

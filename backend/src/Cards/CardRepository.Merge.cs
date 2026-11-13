using System.Data;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using static MyApp.Api.Cards.CardMetadata;

namespace MyApp.Api.Cards;

public sealed record DuplicateMergeRequest(long SurvivorId, IReadOnlyList<long>? CardIds);

public sealed record DuplicateDismissRequest(IReadOnlyList<long>? CardIds);

/// <param name="MergedCardIds">Cards merged into the survivor and deleted by this call.</param>
/// <param name="SkippedCardIds">Requested cards that no longer exist, for example after a repeated request.</param>
public sealed record DuplicateMergeResult(long SurvivorId, IReadOnlyList<long> MergedCardIds, IReadOnlyList<long> SkippedCardIds);

public sealed class DuplicateMergeException(string message, bool notFound = false) : Exception(message)
{
    public bool NotFound { get; } = notFound;
}

public sealed partial class CardRepository
{
    public const int MaxMergeCards = 100;

    /// <summary>
    /// Merges media cards into <paramref name="survivorId"/> in one transaction: tags, sources, deck and
    /// collection memberships, relations, suggestions, decisions, views, votes and creation requests move to
    /// the survivor; empty title and properties are filled from the others, and every merged card's own values
    /// are kept in the survivor's <c>metadata.merged_cards</c>. The merged cards are then deleted; the caller
    /// deletes their files after the commit. Cards that no longer exist are skipped, so a retry is harmless.
    /// </summary>
    public async Task<DuplicateMergeResult> MergeDuplicatesAsync(long survivorId, IReadOnlyList<long> cardIds)
    {
        var requested = cardIds.Where(id => id != survivorId).Distinct().ToArray();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var all = requested.Append(survivorId).ToArray();
            // Lock the cards and their current collections in one id order, then confirm nothing moved meanwhile.
            var parentIds = (await connection.QueryAsync<long>(
                "SELECT DISTINCT from_card_id FROM card_relations WHERE relation_type = 'contains' AND to_card_id IN @Ids;",
                new { Ids = all }, transaction)).ToArray();
            var locked = (await connection.QueryAsync<MergeCardRow>(
                """
                SELECT id AS Id, type AS Type, title AS Title, properties AS Properties, metadata AS Metadata,
                       content_sha256 AS ContentSha256
                FROM cards WHERE id IN @Ids ORDER BY id FOR UPDATE;
                """, new { Ids = all.Concat(parentIds).Distinct().ToArray() }, transaction)).ToDictionary(c => c.Id);
            if (!locked.TryGetValue(survivorId, out var survivor))
                throw new DuplicateMergeException("The card to keep was not found.", notFound: true);
            if (survivor.Type != "media")
                throw new DuplicateMergeException("Only media cards can be merged.");
            var duplicates = requested.Where(locked.ContainsKey).Order().Select(id => locked[id]).ToList();
            var skipped = requested.Where(id => !locked.ContainsKey(id)).Order().ToList();
            if (duplicates.Any(c => c.Type != "media"))
                throw new DuplicateMergeException("Only media cards can be merged.");
            if (duplicates.Count == 0)
            {
                await transaction.CommitAsync();
                return new DuplicateMergeResult(survivorId, [], skipped);
            }

            var dupIds = duplicates.Select(c => c.Id).ToArray();
            var groupIds = dupIds.Append(survivorId).ToArray();
            var parentOf = (await connection.QueryAsync<(long Child, long Parent)>(
                "SELECT to_card_id, from_card_id FROM card_relations WHERE relation_type = 'contains' AND to_card_id IN @Ids FOR UPDATE;",
                new { Ids = groupIds }, transaction)).ToDictionary(r => r.Child, r => r.Parent);
            if (parentOf.Values.Any(p => !locked.ContainsKey(p)))
                throw new DuplicateMergeException("The library changed during the merge. Try again.");

            await MergeCollectionPlaceAsync(connection, transaction, survivorId, dupIds, parentOf, locked);
            await MergeRelationsAsync(connection, transaction, survivorId, dupIds, groupIds);
            await MergeCatalogAsync(connection, transaction, survivorId, dupIds, groupIds);
            await MergeInterestAsync(connection, transaction, survivorId, dupIds, groupIds);
            await MergeDeckMembershipsAsync(connection, transaction, survivorId, groupIds);
            await connection.ExecuteAsync(
                "UPDATE card_creation_requests SET card_id = @S WHERE card_id IN @Dups;",
                new { S = survivorId, Dups = dupIds }, transaction);
            await connection.ExecuteAsync(
                """
                INSERT IGNORE INTO card_duplicate_dismissals (low_card_id, high_card_id, dismissed_at_utc)
                SELECT LEAST(@S, other_id), GREATEST(@S, other_id), MIN(dismissed_at_utc) FROM (
                    SELECT high_card_id AS other_id, dismissed_at_utc FROM card_duplicate_dismissals
                    WHERE low_card_id IN @Dups AND high_card_id NOT IN @Group
                    UNION ALL
                    SELECT low_card_id, dismissed_at_utc FROM card_duplicate_dismissals
                    WHERE high_card_id IN @Dups AND low_card_id NOT IN @Group
                ) others GROUP BY other_id;
                """, new { S = survivorId, Dups = dupIds, Group = groupIds }, transaction);
            await MergeCardFieldsAsync(connection, transaction, survivor, duplicates);

            await connection.ExecuteAsync("DELETE FROM cards WHERE id IN @Dups;", new { Dups = dupIds }, transaction);
            await transaction.CommitAsync();
            await SyncMetaTagsAsync(connection, survivorId);
            return new DuplicateMergeResult(survivorId, dupIds, skipped);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>The survivor takes the earliest page place of the group; other pages of that collection are removed.</summary>
    private static async Task MergeCollectionPlaceAsync(MySqlConnection connection, MySqlTransaction transaction,
        long survivorId, long[] dupIds, IReadOnlyDictionary<long, long> parentOf, IReadOnlyDictionary<long, MergeCardRow> locked)
    {
        var parents = parentOf.Values.Distinct().ToList();
        if (parents.Count == 0) return;
        if (parents.Count > 1)
            throw new DuplicateMergeException("These cards are pages of different comics or sets. Move one page out first.");
        var parentId = parents[0];
        var children = (await connection.QueryAsync<long>(
            $"SELECT to_card_id FROM card_relations WHERE from_card_id = @Id AND relation_type = 'contains' {ChildOrderSql};",
            new { Id = parentId }, transaction)).ToList();
        var first = children.FindIndex(id => id == survivorId || dupIds.Contains(id));
        var updated = new List<long>();
        for (var index = 0; index < children.Count; index++)
        {
            if (index == first) updated.Add(survivorId);
            else if (children[index] != survivorId && !dupIds.Contains(children[index])) updated.Add(children[index]);
        }
        await ReplaceCollectionChildrenAsync(connection, transaction, parentId, locked[parentId].Type, updated);
    }

    /// <summary>Repoints relations to the survivor, drops ones inside the group and duplicates of existing ones.</summary>
    private static async Task MergeRelationsAsync(MySqlConnection connection, MySqlTransaction transaction,
        long survivorId, long[] dupIds, long[] groupIds)
    {
        var inner = (await connection.QueryAsync<long>(
            """
            SELECT id FROM card_relations
            WHERE relation_type <> 'contains' AND from_card_id IN @Group AND to_card_id IN @Group;
            """, new { Group = groupIds }, transaction)).ToArray();
        await DropLegacyAssignmentsAsync(connection, transaction, inner);
        if (inner.Length > 0)
            await connection.ExecuteAsync("DELETE FROM card_relations WHERE id IN @Ids;", new { Ids = inner }, transaction);

        await connection.ExecuteAsync(
            "UPDATE card_relations SET from_card_id = @S WHERE from_card_id IN @Dups AND relation_type <> 'contains';",
            new { S = survivorId, Dups = dupIds }, transaction);
        await connection.ExecuteAsync(
            "UPDATE card_relations SET to_card_id = @S WHERE to_card_id IN @Dups AND relation_type <> 'contains';",
            new { S = survivorId, Dups = dupIds }, transaction);

        var relations = await connection.QueryAsync<(long Id, long From, long To, string Type)>(
            """
            SELECT id, from_card_id, to_card_id, relation_type FROM card_relations
            WHERE (from_card_id = @S OR to_card_id = @S) AND relation_type <> 'contains' ORDER BY id;
            """, new { S = survivorId }, transaction);
        var replaced = new List<(long Removed, long Kept)>();
        foreach (var same in relations.GroupBy(r => r.Type == "related_to"
            ? (r.Type, Math.Min(r.From, r.To), Math.Max(r.From, r.To))
            : (r.Type, r.From, r.To)))
        {
            var kept = same.First().Id;
            replaced.AddRange(same.Skip(1).Select(r => (r.Id, kept)));
        }
        if (replaced.Count == 0) return;

        // Catalog rows mirroring a legacy relation follow the relation that is kept.
        foreach (var table in new[] { "card_tags", "card_sources" })
        {
            var referenced = (await connection.QueryAsync<long>(
                $"SELECT legacy_relation_id FROM {table} WHERE legacy_relation_id IN @Ids;",
                new { Ids = replaced.SelectMany(p => new[] { p.Removed, p.Kept }).ToArray() }, transaction)).ToHashSet();
            foreach (var (removed, kept) in replaced.Where(p => referenced.Contains(p.Removed)))
            {
                if (referenced.Add(kept))
                    await connection.ExecuteAsync(
                        $"UPDATE {table} SET legacy_relation_id = @Kept, card_id = @S WHERE legacy_relation_id = @Removed;",
                        new { Kept = kept, Removed = removed, S = survivorId }, transaction);
                else
                    await connection.ExecuteAsync($"DELETE FROM {table} WHERE legacy_relation_id = @Removed;",
                        new { Removed = removed }, transaction);
            }
        }
        await connection.ExecuteAsync("DELETE FROM card_relations WHERE id IN @Ids;",
            new { Ids = replaced.Select(p => p.Removed).ToArray() }, transaction);
    }

    private static async Task DropLegacyAssignmentsAsync(MySqlConnection connection, MySqlTransaction transaction, long[] relationIds)
    {
        if (relationIds.Length == 0) return;
        await connection.ExecuteAsync("DELETE FROM card_tags WHERE legacy_relation_id IN @Ids;", new { Ids = relationIds }, transaction);
        await connection.ExecuteAsync("DELETE FROM card_sources WHERE legacy_relation_id IN @Ids;", new { Ids = relationIds }, transaction);
    }

    /// <summary>Union of tags, sources and tag suggestions; the tag decision log moves as it is.</summary>
    private static async Task MergeCatalogAsync(MySqlConnection connection, MySqlTransaction transaction,
        long survivorId, long[] dupIds, long[] groupIds)
    {
        var args = new { S = survivorId, Dups = dupIds, Group = groupIds };
        // Meta tags describe a file; the survivor's are synced from its own file after the commit.
        await connection.ExecuteAsync(
            """
            DELETE ct FROM card_tags ct JOIN tags t ON t.id = ct.tag_id
            WHERE ct.card_id IN @Dups AND t.origin = 'system';
            """, args, transaction);
        await connection.ExecuteAsync("UPDATE card_tags SET card_id = @S WHERE card_id IN @Dups;", args, transaction);
        var tags = await connection.QueryAsync<(long Id, long TagId, string Source, decimal? Confidence)>(
            """
            SELECT id, tag_id, assignment_source, confidence FROM card_tags
            WHERE card_id = @S AND legacy_relation_id IS NULL ORDER BY id;
            """, args, transaction);
        var redundantTags = tags.GroupBy(t => t.TagId)
            .SelectMany(g => g.OrderByDescending(t => t.Source == "manual").ThenByDescending(t => t.Confidence ?? 1m)
                .ThenBy(t => t.Id).Skip(1).Select(t => t.Id))
            .ToArray();
        if (redundantTags.Length > 0)
            await connection.ExecuteAsync("DELETE FROM card_tags WHERE id IN @Ids;", new { Ids = redundantTags }, transaction);

        await connection.ExecuteAsync("UPDATE card_sources SET card_id = @S WHERE card_id IN @Dups;", args, transaction);
        var sources = await connection.QueryAsync<(long Id, long SourceId, string? Url)>(
            """
            SELECT id, source_id, original_url FROM card_sources
            WHERE card_id = @S AND legacy_relation_id IS NULL ORDER BY id;
            """, args, transaction);
        var redundantSources = sources.GroupBy(s => (s.SourceId, s.Url ?? ""))
            .SelectMany(g => g.Skip(1).Select(s => s.Id)).ToArray();
        if (redundantSources.Length > 0)
            await connection.ExecuteAsync("DELETE FROM card_sources WHERE id IN @Ids;", new { Ids = redundantSources }, transaction);

        var suggestions = (await connection.QueryAsync<SuggestionRow>(
            """
            SELECT card_id AS CardId, tag_id AS TagId, state AS State, confidence AS Confidence, model_key AS ModelKey,
                   suggestion_source AS SuggestionSource, created_at_utc AS CreatedAtUtc
            FROM card_tag_suggestions WHERE card_id IN @Group FOR UPDATE;
            """, args, transaction)).ToList();
        if (suggestions.Any(s => s.CardId != survivorId))
        {
            static int Rank(string state) => state switch { "accepted" => 2, "rejected" => 1, _ => 0 };
            var merged = suggestions.GroupBy(s => s.TagId)
                .Select(g => g.OrderByDescending(s => Rank(s.State)).ThenByDescending(s => s.Confidence ?? 0m).First())
                .Select(s => new
                {
                    S = survivorId, s.TagId, s.State, s.Confidence, s.ModelKey, s.SuggestionSource, s.CreatedAtUtc,
                })
                .ToList();
            await connection.ExecuteAsync("DELETE FROM card_tag_suggestions WHERE card_id IN @Group;", args, transaction);
            await connection.ExecuteAsync(
                """
                INSERT INTO card_tag_suggestions
                    (card_id, tag_id, state, confidence, model_key, suggestion_source, created_at_utc, updated_at_utc)
                VALUES (@S, @TagId, @State, @Confidence, @ModelKey, @SuggestionSource, @CreatedAtUtc, UTC_TIMESTAMP(6));
                """, merged, transaction);
            // A pending suggestion for a tag the survivor now has is settled, as when the tag is set by hand.
            await connection.ExecuteAsync(
                """
                DELETE s FROM card_tag_suggestions s
                JOIN card_tags ct ON ct.card_id = s.card_id AND ct.tag_id = s.tag_id
                WHERE s.card_id = @S AND s.state = 'pending';
                """, args, transaction);
        }
        await connection.ExecuteAsync("UPDATE card_tag_decisions SET card_id = @S WHERE card_id IN @Dups;", args, transaction);
    }

    /// <summary>Views move as they are; for a day with several votes the latest one is kept.</summary>
    private static async Task MergeInterestAsync(MySqlConnection connection, MySqlTransaction transaction,
        long survivorId, long[] dupIds, long[] groupIds)
    {
        await connection.ExecuteAsync("UPDATE card_view_events SET card_id = @S WHERE card_id IN @Dups;",
            new { S = survivorId, Dups = dupIds }, transaction);
        var votes = (await connection.QueryAsync<VoteRow>(
            """
            SELECT card_id AS CardId, vote_date AS VoteDate, vote AS Vote, zone_id AS ZoneId, updated_at_utc AS UpdatedAtUtc
            FROM card_daily_votes WHERE card_id IN @Group FOR UPDATE;
            """, new { Group = groupIds }, transaction)).ToList();
        if (votes.All(v => v.CardId == survivorId)) return;
        var kept = votes.GroupBy(v => v.VoteDate)
            .Select(g => g.OrderByDescending(v => v.UpdatedAtUtc).ThenByDescending(v => v.CardId == survivorId).First())
            .Select(v => new { S = survivorId, v.VoteDate, v.Vote, v.ZoneId, v.UpdatedAtUtc })
            .ToList();
        await connection.ExecuteAsync("DELETE FROM card_daily_votes WHERE card_id IN @Group;", new { Group = groupIds }, transaction);
        await connection.ExecuteAsync(
            """
            INSERT INTO card_daily_votes (card_id, vote_date, vote, zone_id, updated_at_utc)
            VALUES (@S, @VoteDate, @Vote, @ZoneId, @UpdatedAtUtc);
            """, kept, transaction);
    }

    /// <summary>In each deck the survivor takes the earliest position of the group.</summary>
    private static async Task MergeDeckMembershipsAsync(MySqlConnection connection, MySqlTransaction transaction,
        long survivorId, long[] groupIds)
    {
        var rows = (await connection.QueryAsync<(long DeckId, long CardId, int Position, string? Properties)>(
            """
            SELECT deck_id, card_id, position, properties FROM deck_cards
            WHERE card_id IN @Group ORDER BY deck_id, position FOR UPDATE;
            """, new { Group = groupIds }, transaction)).ToList();
        foreach (var deck in rows.GroupBy(r => r.DeckId))
        {
            var keeper = deck.First();
            var others = deck.Skip(1).ToList();
            if (others.Count == 0 && keeper.CardId == survivorId) continue;
            var properties = ParseOptionalJson(keeper.Properties) as JsonObject;
            foreach (var other in others)
                if (ParseOptionalJson(other.Properties) is JsonObject extra)
                    foreach (var (key, value) in extra)
                    {
                        properties ??= new JsonObject();
                        if (!properties.ContainsKey(key)) properties[key] = value?.DeepClone();
                    }
            if (others.Count > 0)
                await connection.ExecuteAsync("DELETE FROM deck_cards WHERE deck_id = @DeckId AND card_id IN @Ids;",
                    new { DeckId = deck.Key, Ids = others.Select(o => o.CardId).ToArray() }, transaction);
            await connection.ExecuteAsync(
                "UPDATE deck_cards SET card_id = @S, properties = @Properties WHERE deck_id = @DeckId AND card_id = @CardId;",
                new { S = survivorId, Properties = properties?.ToJsonString(), DeckId = deck.Key, keeper.CardId }, transaction);
        }
    }

    /// <summary>
    /// Fills an empty title and missing properties from the merged cards, keeps the oldest creation time and
    /// records each merged card's own title, properties and file facts in <c>metadata.merged_cards</c>.
    /// </summary>
    private static async Task MergeCardFieldsAsync(MySqlConnection connection, MySqlTransaction transaction,
        MergeCardRow survivor, IReadOnlyList<MergeCardRow> duplicates)
    {
        var title = survivor.Title;
        var properties = ParseOptionalJson(survivor.Properties) as JsonObject;
        var metadata = ParseRequiredJsonObject(survivor.Metadata);
        var history = metadata["merged_cards"] as JsonArray ?? new JsonArray();
        var mergedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        foreach (var duplicate in duplicates)
        {
            if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(duplicate.Title)) title = duplicate.Title;
            var dupProperties = ParseOptionalJson(duplicate.Properties) as JsonObject;
            if (dupProperties is not null)
                foreach (var (key, value) in dupProperties)
                {
                    properties ??= new JsonObject();
                    if (!properties.ContainsKey(key)) properties[key] = value?.DeepClone();
                }
            var dupMetadata = ParseRequiredJsonObject(duplicate.Metadata);
            var record = new JsonObject
            {
                ["id"] = duplicate.Id,
                ["title"] = duplicate.Title,
                ["merged_at"] = mergedAt,
                ["content_sha256"] = duplicate.ContentSha256,
            };
            foreach (var key in new[] { "created_at", "original_file_name", "mime_type", "file_size", "width", "height", "duration" })
                if (dupMetadata[key] is { } value) record[key] = value.DeepClone();
            if (dupProperties is { Count: > 0 }) record["properties"] = dupProperties.DeepClone();
            history.Add(record);
            if (dupMetadata["merged_cards"] is JsonArray earlier)
                foreach (var item in earlier) history.Add(item?.DeepClone());
            if (Text(dupMetadata, "created_at") is { } created &&
                string.CompareOrdinal(created, Text(metadata, "created_at") ?? created) < 0)
                metadata["created_at"] = created;
        }
        metadata["merged_cards"] = history;
        metadata["updated_at"] = mergedAt;
        await connection.ExecuteAsync(
            "UPDATE cards SET title = @Title, properties = @Properties, metadata = @Metadata WHERE id = @Id;",
            new { survivor.Id, Title = title, Properties = properties?.ToJsonString(), Metadata = metadata.ToJsonString() },
            transaction);
    }

    private sealed class MergeCardRow
    {
        public long Id { get; set; }
        public string Type { get; set; } = "";
        public string? Title { get; set; }
        public string? Properties { get; set; }
        public string Metadata { get; set; } = "{}";
        public string? ContentSha256 { get; set; }
    }

    private sealed class SuggestionRow
    {
        public long CardId { get; set; }
        public long TagId { get; set; }
        public string State { get; set; } = "";
        public decimal? Confidence { get; set; }
        public string? ModelKey { get; set; }
        public string SuggestionSource { get; set; } = "visual";
        public DateTime CreatedAtUtc { get; set; }
    }

    private sealed class VoteRow
    {
        public long CardId { get; set; }
        public DateTime VoteDate { get; set; }
        public int Vote { get; set; }
        public string ZoneId { get; set; } = "";
        public DateTime UpdatedAtUtc { get; set; }
    }
}

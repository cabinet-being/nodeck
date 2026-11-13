using System.Text.Json.Nodes;
using Dapper;
using MyApp.Api.Duplicates;
using MySqlConnector;
using static MyApp.Api.Cards.CardMetadata;
using static MyApp.Api.Cards.CardSql;

namespace MyApp.Api.Cards;

public sealed record DuplicateCard(
    long Id, string? Title, string? MediaType, string? MimeType, int? Width, int? Height, long? FileSize,
    double? Duration, string? CreatedAt, string? OriginalFileName, string? ContentSha256,
    int TagCount, int SourceCount, IReadOnlyList<string> Decks, bool IsFavorite, int ViewCount,
    long? ParentId, string? ParentTitle, string? ParentType);

/// <param name="Kind">"exact" when every card has the same content hash, otherwise "similar".</param>
/// <param name="MaxDistance">Largest perceptual hash distance between two cards with different content.</param>
/// <param name="Conflict">Why the group cannot be merged as a whole, or null.</param>
public sealed record DuplicateGroup(
    string Key, string Kind, int? MaxDistance, long SuggestedSurvivorId, string? Conflict,
    IReadOnlyList<DuplicateCard> Cards);

public sealed record DuplicateGroupPage(
    IReadOnlyList<DuplicateGroup> Groups, int Total, int ExactGroups, int SimilarGroups,
    int ExactRedundantCards, int PendingHashes);

public sealed partial class CardRepository
{
    private sealed record DuplicateCandidate(long Id, string Sha, long? Hash);

    /// <summary>
    /// Groups media cards with the same content hash (exact) or perceptual hashes at most
    /// <see cref="PerceptualHash.SimilarDistance"/> bits apart (similar). Pairs dismissed as
    /// "not duplicates" are never joined; groups are connected components of the remaining pairs.
    /// </summary>
    public async Task<DuplicateGroupPage> FindDuplicateGroupsAsync(string kind, int limit, int offset)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        var candidates = (await connection.QueryAsync<DuplicateCandidate>(
            """
            SELECT c.id AS Id, c.content_sha256 AS Sha, p.hash AS Hash
            FROM cards c
            LEFT JOIN card_perceptual_hashes p ON p.card_id = c.id AND p.content_sha256 = c.content_sha256
            WHERE c.type = 'media' AND c.content_sha256 IS NOT NULL
            ORDER BY c.id;
            """)).ToList();
        var dismissed = (await connection.QueryAsync<(long Low, long High)>(
            "SELECT low_card_id, high_card_id FROM card_duplicate_dismissals;")).ToHashSet();
        var pendingHashes = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM cards c
            LEFT JOIN card_perceptual_hashes p ON p.card_id = c.id AND p.content_sha256 = c.content_sha256
            WHERE c.type = 'media' AND (c.content_sha256 IS NULL OR (p.card_id IS NULL
                AND JSON_UNQUOTE(JSON_EXTRACT(c.metadata, '$.media_type')) IN ('image', 'gif')));
            """);

        bool IsDismissed(long a, long b) => dismissed.Contains(a < b ? (a, b) : (b, a));

        var index = candidates.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var parent = Enumerable.Range(0, candidates.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }

        foreach (var sameContent in candidates.GroupBy(c => c.Sha).Where(g => g.Count() > 1))
        {
            var members = sameContent.ToList();
            for (var i = 0; i < members.Count; i++)
                for (var j = i + 1; j < members.Count; j++)
                    if (!IsDismissed(members[i].Id, members[j].Id))
                        Union(index[members[i].Id], index[members[j].Id]);
        }
        var hashed = candidates.Where(c => c.Hash is not null).ToArray();
        for (var i = 0; i < hashed.Length; i++)
        {
            var a = hashed[i];
            for (var j = i + 1; j < hashed.Length; j++)
            {
                var b = hashed[j];
                if (a.Sha != b.Sha && PerceptualHash.Distance(a.Hash!.Value, b.Hash!.Value) <= PerceptualHash.SimilarDistance &&
                    !IsDismissed(a.Id, b.Id))
                    Union(index[a.Id], index[b.Id]);
            }
        }

        var components = candidates.GroupBy(c => Find(index[c.Id]))
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var members = g.OrderBy(c => c.Id).ToList();
                var exact = members.All(c => c.Sha == members[0].Sha);
                int? maxDistance = null;
                if (!exact)
                    for (var i = 0; i < members.Count; i++)
                        for (var j = i + 1; j < members.Count; j++)
                            if (members[i].Hash is long x && members[j].Hash is long y && members[i].Sha != members[j].Sha)
                                maxDistance = Math.Max(maxDistance ?? 0, PerceptualHash.Distance(x, y));
                return (Members: members, Exact: exact, MaxDistance: maxDistance);
            })
            .ToList();
        var exactGroups = components.Count(c => c.Exact);
        var exactRedundant = components.Where(c => c.Exact).Sum(c => c.Members.Count - 1);
        var selected = components
            .Where(c => kind switch { "exact" => c.Exact, "similar" => !c.Exact, _ => true })
            .OrderByDescending(c => c.Exact).ThenByDescending(c => c.Members.Count).ThenBy(c => c.Members[0].Id)
            .ToList();
        var page = selected.Skip(offset).Take(limit).ToList();

        var cards = await LoadDuplicateCardsAsync(connection, page.SelectMany(c => c.Members.Select(m => m.Id)).ToArray());
        var groups = page.Select(component =>
        {
            var members = component.Members.Select(m => cards[m.Id]).ToList();
            var parents = members.Where(c => c.ParentId is not null).Select(c => c.ParentId).Distinct().Count();
            return new DuplicateGroup(
                string.Join('-', component.Members.Select(m => m.Id)),
                component.Exact ? "exact" : "similar",
                component.MaxDistance,
                SuggestSurvivor(members).Id,
                parents > 1 ? "These cards are pages of different comics or sets. Move one page out first." : null,
                members);
        }).ToList();
        return new DuplicateGroupPage(groups, selected.Count, exactGroups, components.Count - exactGroups,
            exactRedundant, pendingHashes);
    }

    /// <summary>
    /// The card to keep by default: a page of a comic or set (only it can keep that place), then the most
    /// pixels, the largest file, and the oldest card.
    /// </summary>
    internal static DuplicateCard SuggestSurvivor(IReadOnlyList<DuplicateCard> cards) => cards
        .OrderByDescending(c => c.ParentId is not null)
        .ThenByDescending(c => (long)(c.Width ?? 0) * (c.Height ?? 0))
        .ThenByDescending(c => c.FileSize ?? 0)
        .ThenBy(c => c.Id)
        .First();

    private static async Task<Dictionary<long, DuplicateCard>> LoadDuplicateCardsAsync(MySqlConnection connection, long[] ids)
    {
        if (ids.Length == 0) return [];
        var rows = await connection.QueryAsync<DuplicateCardRow>(
            $"""
            SELECT c.id AS Id, c.title AS Title, c.metadata AS Metadata, c.content_sha256 AS ContentSha256,
                (SELECT COUNT(DISTINCT ct.tag_id) FROM card_tags ct JOIN tags t ON t.id = ct.tag_id
                 WHERE ct.card_id = c.id AND t.origin <> 'system') AS TagCount,
                (SELECT COUNT(*) FROM card_sources cs WHERE cs.card_id = c.id) AS SourceCount,
                (SELECT COUNT(*) FROM card_view_events v WHERE v.card_id = c.id) AS ViewCount,
                {FavoriteSelectSql("c.id")} AS IsFavorite,
                p.id AS ParentId, p.title AS ParentTitle, p.type AS ParentType
            FROM cards c
            LEFT JOIN card_relations r ON r.to_card_id = c.id AND r.relation_type = 'contains'
            LEFT JOIN cards p ON p.id = r.from_card_id
            WHERE c.id IN @Ids;
            """, new { Ids = ids });
        var decks = (await connection.QueryAsync<(long CardId, string Title)>(
            """
            SELECT dc.card_id, d.title FROM deck_cards dc JOIN decks d ON d.id = dc.deck_id
            WHERE dc.card_id IN @Ids AND (d.system_key IS NULL OR d.system_key <> 'favorites')
            ORDER BY d.title, d.id;
            """, new { Ids = ids })).ToLookup(r => r.CardId, r => r.Title);
        return rows.ToDictionary(r => r.Id, r =>
        {
            var metadata = ParseRequiredJsonObject(r.Metadata);
            return new DuplicateCard(r.Id, r.Title,
                Text(metadata, "media_type"), Text(metadata, "mime_type"),
                (int?)Number(metadata, "width"), (int?)Number(metadata, "height"), (long?)Number(metadata, "file_size"),
                Number(metadata, "duration"), Text(metadata, "created_at"), Text(metadata, "original_file_name"),
                r.ContentSha256, r.TagCount, r.SourceCount, decks[r.Id].ToList(), r.IsFavorite, r.ViewCount,
                r.ParentId, r.ParentTitle, r.ParentType);
        });
    }

    private static string? Text(JsonObject metadata, string key) =>
        metadata[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonObject metadata, string key) =>
        metadata[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    /// <summary>Remembers that these cards are not duplicates of each other.</summary>
    public async Task<int> DismissDuplicatesAsync(IReadOnlyList<long> cardIds)
    {
        var ids = cardIds.Distinct().Order().ToArray();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        var existing = (await connection.QueryAsync<long>("SELECT id FROM cards WHERE id IN @Ids;", new { Ids = ids }))
            .Order().ToArray();
        if (existing.Length != ids.Length) return -1;
        var pairs = new List<object>();
        for (var i = 0; i < ids.Length; i++)
            for (var j = i + 1; j < ids.Length; j++)
                pairs.Add(new { Low = ids[i], High = ids[j] });
        await connection.ExecuteAsync(
            """
            INSERT IGNORE INTO card_duplicate_dismissals (low_card_id, high_card_id, dismissed_at_utc)
            VALUES (@Low, @High, UTC_TIMESTAMP(6));
            """, pairs);
        return pairs.Count;
    }

    private sealed class DuplicateCardRow
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public string Metadata { get; set; } = "{}";
        public string? ContentSha256 { get; set; }
        public int TagCount { get; set; }
        public int SourceCount { get; set; }
        public int ViewCount { get; set; }
        public bool IsFavorite { get; set; }
        public long? ParentId { get; set; }
        public string? ParentTitle { get; set; }
        public string? ParentType { get; set; }
    }
}

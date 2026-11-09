using System.Text.Json.Nodes;
using System.Globalization;
using Dapper;
using MyApp.Api.Cards;
using MyApp.Api.Storage;
using MySqlConnector;

namespace MyApp.Api.Recommendations;

public sealed record RecommendationItem(CardSummary Card, double Score);
public sealed record DailyRecommendationResult(IReadOnlyList<RecommendationItem> Items, string LocalDate, string TimeZone);

public sealed class RecommendationRepository
{
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly PreviewService _previewService;

    public RecommendationRepository(IConfiguration configuration, PreviewService previewService,
        TimeProvider? timeProvider = null)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
        _timeProvider = timeProvider ?? TimeProvider.System;
        _previewService = previewService;
    }

    public async Task<DailyRecommendationResult> GetDailyAsync(TimeZoneInfo timeZone, int limit,
        CancellationToken cancellationToken = default)
    {
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), timeZone).DateTime);
        var dateText = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var utcStart = TimeZoneInfo.ConvertTimeToUtc(localDate.ToDateTime(TimeOnly.MinValue), timeZone);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // SHA2 keeps the local day's order stable. Page through that order so
        // unavailable files cannot consume result slots before the limit.
        const string dailySql = """
            SELECT c.id AS Id, c.type AS Type, c.title AS Title, c.preview AS Preview,
                   c.properties AS Properties, c.metadata AS Metadata,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM deck_cards dc JOIN decks d ON d.id = dc.deck_id
                       WHERE dc.card_id = c.id AND d.system_key = 'favorites'
                   ) THEN 1 ELSE 0 END AS IsFavorite
            FROM cards c
            WHERE c.type IN ('media', 'comic', 'set')
              AND (@IncludeToday = 1 OR c.created_at < @UtcStart OR c.created_at IS NULL)
              AND NOT EXISTS (SELECT 1 FROM card_relations r
                              WHERE r.to_card_id = c.id AND r.relation_type = 'contains')
              AND NOT EXISTS (
                  SELECT 1 FROM card_daily_votes v
                  WHERE v.card_id = c.id AND v.vote = -1
                    AND NOT EXISTS (
                        SELECT 1 FROM card_daily_votes newer
                        WHERE newer.card_id = v.card_id
                          AND (newer.updated_at_utc > v.updated_at_utc
                               OR (newer.updated_at_utc = v.updated_at_utc
                                   AND newer.vote_date > v.vote_date))
                    )
              )
              AND NOT EXISTS (
                  SELECT 1 FROM card_relations child
                  JOIN card_daily_votes v ON v.card_id = child.to_card_id
                  WHERE child.from_card_id = c.id AND child.relation_type = 'contains'
                    AND v.vote = -1
                    AND NOT EXISTS (
                        SELECT 1 FROM card_daily_votes newer
                        WHERE newer.card_id = v.card_id
                          AND (newer.updated_at_utc > v.updated_at_utc
                               OR (newer.updated_at_utc = v.updated_at_utc
                                   AND newer.vote_date > v.vote_date))
                    )
              )
            ORDER BY SHA2(CONCAT(@LocalDate, '|', @TimeZone, '|', c.id), 256), c.id
            LIMIT @PageSize OFFSET @Offset;
            """;

        async Task<List<CardRow>> ScanAsync(bool includeToday)
        {
            const int pageSize = 100;
            const int maxPages = 50;
            var available = new List<CardRow>(limit);
            // Cap filesystem and SQL work at 5,000 candidates; a larger library
            // can return fewer than limit if its available cards lie beyond the cap.
            for (var pageNumber = 0; pageNumber < maxPages && available.Count < limit; pageNumber++)
            {
                var page = (await connection.QueryAsync<CardRow>(new CommandDefinition(dailySql,
                    new { UtcStart = utcStart, IncludeToday = includeToday ? 1 : 0,
                        LocalDate = dateText, TimeZone = timeZone.Id, PageSize = pageSize,
                        Offset = pageNumber * pageSize },
                    cancellationToken: cancellationToken))).ToArray();
                if (page.Length == 0) break;
                var availableIds = await GetAvailableIdsAsync(connection, page.Select(card => card.Id).ToArray(),
                    cancellationToken);
                foreach (var card in page)
                {
                    if (availableIds.Contains(card.Id)) available.Add(card);
                    if (available.Count == limit) break;
                }
                if (page.Length < pageSize) break;
            }
            return available;
        }

        // Today's uploads are a cold-start fallback only when no older card can be read.
        var cards = await ScanAsync(false);
        if (cards.Count == 0) cards = await ScanAsync(true);
        return new DailyRecommendationResult(cards.Select(card => new RecommendationItem(ToSummary(card), 0)).ToArray(),
            dateText, timeZone.Id);
    }

    public async Task<IReadOnlyList<RecommendationItem>?> GetAsync(string mode, int limit,
        long? contextCardId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var viewCutoff = now.AddDays(-30);
        var voteCutoff = now.Date.AddDays(-90);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        if (contextCardId is long contextId &&
            !await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT COUNT(*) FROM cards WHERE id = @Id;", new { Id = contextId },
                cancellationToken: cancellationToken)))
            return null;

        // Each source is bounded before IDs are combined. The last source supplies the
        // newest top-level cards when the library has no interest signals.
        var candidateIds = (await connection.QueryAsync<long>(new CommandDefinition("""
            SELECT card_id FROM (
                SELECT card_id FROM card_view_events
                ORDER BY viewed_at_utc DESC, id DESC LIMIT 500
            ) recent_views
            UNION
            SELECT card_id FROM (
                SELECT card_id FROM card_daily_votes
                WHERE vote_date >= @VoteCutoff
                ORDER BY updated_at_utc DESC, card_id DESC LIMIT 500
            ) recent_votes
            UNION
            SELECT id FROM (
                SELECT c.id FROM cards c
                WHERE c.type IN ('media', 'comic', 'set')
                  AND NOT EXISTS (SELECT 1 FROM card_relations r
                                  WHERE r.to_card_id = c.id AND r.relation_type = 'contains')
                ORDER BY c.id DESC LIMIT 200
            ) newest_cards;
            """, new { VoteCutoff = voteCutoff }, cancellationToken: cancellationToken))).ToHashSet();

        if (mode == "pages")
        {
            var newestChildren = await connection.QueryAsync<long>(new CommandDefinition("""
                SELECT r.to_card_id FROM card_relations r
                JOIN cards c ON c.id = r.to_card_id
                WHERE r.relation_type = 'contains' AND c.type = 'media'
                ORDER BY r.to_card_id DESC LIMIT 200;
                """, cancellationToken: cancellationToken));
            candidateIds.UnionWith(newestChildren);
        }

        HashSet<long> contextTags = [];
        long? contextFamilyId = null;
        if (contextCardId is long contextCard)
        {
            var contextParent = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("""
                SELECT from_card_id FROM card_relations
                WHERE to_card_id = @CardId AND relation_type = 'contains' LIMIT 1;
                """, new { CardId = contextCard }, cancellationToken: cancellationToken));
            contextFamilyId = contextParent ?? contextCard;
            var contextMembers = new HashSet<long> { contextCard };
            if (contextParent is long parentId) contextMembers.Add(parentId);
            var contextChildren = await connection.QueryAsync<long>(new CommandDefinition("""
                SELECT to_card_id FROM card_relations
                WHERE from_card_id = @CardId AND relation_type = 'contains'
                ORDER BY to_card_id DESC LIMIT 200;
                """, new { CardId = contextCard }, cancellationToken: cancellationToken));
            contextMembers.UnionWith(contextChildren);
            contextTags = (await connection.QueryAsync<long>(new CommandDefinition(
                "SELECT DISTINCT tag_id FROM card_tags WHERE card_id IN @Ids ORDER BY tag_id LIMIT 100;",
                new { Ids = contextMembers.ToArray() }, cancellationToken: cancellationToken))).ToHashSet();
        }

        // Build a bounded preference profile from actual assigned tags. Repeated daily
        // votes contribute repeatedly, while each source card and tag has a fixed cap.
        var profileViews = (await connection.QueryAsync<ViewRow>(new CommandDefinition("""
            SELECT card_id AS CardId, viewed_at_utc AS ViewedAtUtc
            FROM card_view_events
            WHERE viewed_at_utc >= @ViewCutoff
            ORDER BY viewed_at_utc DESC, id DESC LIMIT 500;
            """, new { ViewCutoff = viewCutoff }, cancellationToken: cancellationToken))).ToArray();
        var profileVotes = (await connection.QueryAsync<VoteRow>(new CommandDefinition("""
            SELECT card_id AS CardId, vote AS Vote FROM card_daily_votes
            WHERE vote_date >= @VoteCutoff
            ORDER BY updated_at_utc DESC, card_id DESC LIMIT 500;
            """, new { VoteCutoff = voteCutoff }, cancellationToken: cancellationToken))).ToArray();
        var cardWeights = new Dictionary<long, double>();
        foreach (var view in profileViews)
        {
            var ageDays = Math.Max(0, (now - view.ViewedAtUtc).TotalDays);
            cardWeights[view.CardId] = Math.Min(3,
                cardWeights.GetValueOrDefault(view.CardId) + 1.0 / (1.0 + ageDays / 7.0));
        }
        foreach (var vote in profileVotes)
            cardWeights[vote.CardId] = Math.Clamp(cardWeights.GetValueOrDefault(vote.CardId) +
                (vote.Vote switch { 1 => 2, -1 => -3, _ => 0 }), -8, 8);

        var tagWeights = contextTags.ToDictionary(tagId => tagId, _ => 4.0);
        if (cardWeights.Count > 0)
        {
            var signalTags = await connection.QueryAsync<TagRow>(new CommandDefinition("""
                SELECT DISTINCT card_id AS CardId, tag_id AS TagId
                FROM card_tags WHERE card_id IN @Ids
                ORDER BY card_id DESC, tag_id LIMIT 10000;
                """, new { Ids = cardWeights.Keys.ToArray() }, cancellationToken: cancellationToken));
            foreach (var tag in signalTags)
                tagWeights[tag.TagId] = Math.Clamp(tagWeights.GetValueOrDefault(tag.TagId) +
                    cardWeights[tag.CardId], -12, 12);
        }
        foreach (var tagId in contextTags)
            tagWeights[tagId] = Math.Max(4, tagWeights[tagId]);

        var strongestContextTags = contextTags.OrderByDescending(tagId => tagWeights[tagId])
            .ThenBy(tagId => tagId).Take(4);
        var strongestProfileTags = tagWeights.Where(pair => pair.Value > 0 &&
                !contextTags.Contains(pair.Key))
            .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key)
            .Take(contextTags.Count > 0 ? 4 : 8).Select(pair => pair.Key);
        var strongestTags = strongestContextTags.Concat(strongestProfileTags).ToArray();
        foreach (var tagId in strongestTags)
        {
            var related = await connection.QueryAsync<long>(new CommandDefinition("""
                SELECT card_id FROM card_tags WHERE tag_id = @TagId
                ORDER BY card_id DESC LIMIT 50;
                """, new { TagId = tagId }, cancellationToken: cancellationToken));
            candidateIds.UnionWith(related);
        }

        if (candidateIds.Count == 0) return [];
        var ids = candidateIds.ToArray();
        var relationshipIds = contextCardId is long contextForRelation
            ? ids.Append(contextForRelation).Distinct().ToArray() : ids;
        var parents = (await connection.QueryAsync<ParentRow>(new CommandDefinition("""
            SELECT to_card_id AS ChildId, from_card_id AS ParentId
            FROM card_relations WHERE relation_type = 'contains' AND to_card_id IN @Ids;
            """, new { Ids = relationshipIds }, cancellationToken: cancellationToken)))
            .ToDictionary(row => row.ChildId, row => row.ParentId);
        long Family(long id) => parents.GetValueOrDefault(id, id);
        long Resolve(long id) => mode == "collections" && parents.TryGetValue(id, out var parentId)
            ? parentId : id;

        var familyIds = ids.Select(Family).Distinct().ToArray();
        var latestNegativeIds = (await connection.QueryAsync<long>(new CommandDefinition("""
            SELECT v.card_id FROM card_daily_votes v
            WHERE v.card_id IN @Ids AND v.vote = -1
              AND NOT EXISTS (
                  SELECT 1 FROM card_daily_votes newer
                  WHERE newer.card_id = v.card_id
                    AND (newer.updated_at_utc > v.updated_at_utc
                         OR (newer.updated_at_utc = v.updated_at_utc
                             AND newer.vote_date > v.vote_date))
              );
            """, new { Ids = familyIds.Concat(ids).Distinct().ToArray() },
                cancellationToken: cancellationToken)))
            .ToHashSet();
        var negativeFamilies = latestNegativeIds.Select(Family).ToHashSet();

        var scores = new Dictionary<long, double>();
        foreach (var id in ids) scores.TryAdd(Resolve(id), 0);

        var candidateSignals = cardWeights.Where(pair => candidateIds.Contains(pair.Key));
        if (mode == "collections")
        {
            foreach (var group in candidateSignals.GroupBy(pair => Resolve(pair.Key)))
            {
                var ownWeight = group.Where(pair => pair.Key == group.Key).Sum(pair => pair.Value);
                var childWeight = group.Where(pair => pair.Key != group.Key)
                    .OrderByDescending(pair => pair.Value).Take(3).Sum(pair => pair.Value);
                scores[group.Key] += Math.Clamp(ownWeight + childWeight, -8, 8);
            }
        }
        else
        {
            foreach (var (cardId, weight) in candidateSignals)
                scores[cardId] += weight;
        }

        var scoringTagWeights = tagWeights.Where(pair => contextTags.Contains(pair.Key))
            .Concat(tagWeights.Where(pair => pair.Value > 0 && !contextTags.Contains(pair.Key))
                .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(32))
            .Concat(tagWeights.Where(pair => pair.Value < 0 && !contextTags.Contains(pair.Key))
                .OrderBy(pair => pair.Value).ThenBy(pair => pair.Key).Take(16))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        // Load assigned tags once for both preference scoring and diversity. The
        // deterministic cap bounds work even when cards have unusually many tags.
        var candidateTags = await connection.QueryAsync<TagRow>(new CommandDefinition("""
            SELECT card_id AS CardId, tag_id AS TagId
            FROM card_tags WHERE card_id IN @Ids
            ORDER BY card_id DESC, tag_id LIMIT 20000;
            """, new { Ids = ids.Concat(scores.Keys).Distinct().ToArray() },
            cancellationToken: cancellationToken));
        var tagsByCard = candidateTags.GroupBy(row => Resolve(row.CardId))
            .ToDictionary(group => group.Key, group => group.Select(row => row.TagId).Distinct().ToArray());
        foreach (var (cardId, tags) in tagsByCard)
            scores[cardId] += Math.Clamp(tags.Sum(tagId => scoringTagWeights.GetValueOrDefault(tagId)), -12, 12);

        var createdCards = await connection.QueryAsync<CreatedRow>(new CommandDefinition("""
            SELECT id AS Id, created_at AS CreatedAt FROM cards WHERE id IN @Ids;
            """, new { Ids = scores.Keys.ToArray() }, cancellationToken: cancellationToken));
        foreach (var card in createdCards)
        {
            if (card.CreatedAt is not DateTime createdAt) continue;
            var ageDays = Math.Max(0, (now - createdAt).TotalDays);
            scores[card.Id] += 2.0 / (1.0 + ageDays / 7.0);
        }

        // A negative vote on the collection suppresses its children. A negative
        // child vote suppresses the whole collapsed group in collections mode.
        var availableIds = await GetAvailableIdsAsync(connection, scores.Keys.ToArray(), cancellationToken);
        var eligible = scores.Where(pair => availableIds.Contains(pair.Key) &&
                Family(pair.Key) != contextFamilyId &&
                !latestNegativeIds.Contains(pair.Key) &&
                (mode == "collections"
                    ? !negativeFamilies.Contains(pair.Key)
                    : !latestNegativeIds.Contains(Family(pair.Key))))
            .ToArray();
        HashSet<long> childFamilies = mode == "pages"
            ? eligible.Where(pair => parents.ContainsKey(pair.Key)).Select(pair => Family(pair.Key)).ToHashSet()
            : new HashSet<long>();
        var familyCounts = new Dictionary<long, int>();
        var selectedTagCounts = new Dictionary<long, int>();
        var selectedScores = new Dictionary<long, double>();
        var rankedIds = new List<long>(limit);
        var remaining = eligible.Select(pair => pair.Key).ToList();
        while (rankedIds.Count < limit)
        {
            long? bestId = null;
            var bestScore = double.NegativeInfinity;
            foreach (var id in remaining)
            {
                if (mode == "pages")
                {
                    var family = Family(id);
                    if (family == id && childFamilies.Contains(family)) continue;
                    if (familyCounts.GetValueOrDefault(family) >= 3) continue;
                }

                var repetition = tagsByCard.TryGetValue(id, out var tags)
                    ? tags.Select(tagId => selectedTagCounts.GetValueOrDefault(tagId)).DefaultIfEmpty().Max()
                    : 0;
                // Keep one related follow-up before diversity lowers repeated tags.
                var marginalScore = scores[id] - 4 * Math.Max(0, repetition - 1);
                if (marginalScore > bestScore ||
                    (marginalScore == bestScore && (bestId is null || id > bestId.Value)))
                {
                    bestId = id;
                    bestScore = marginalScore;
                }
            }
            if (bestId is not long selectedId) break;
            remaining.Remove(selectedId);
            rankedIds.Add(selectedId);
            selectedScores[selectedId] = bestScore;
            if (mode == "pages")
            {
                var family = Family(selectedId);
                familyCounts[family] = familyCounts.GetValueOrDefault(family) + 1;
            }
            if (tagsByCard.TryGetValue(selectedId, out var selectedTags))
                foreach (var tagId in selectedTags)
                    selectedTagCounts[tagId] = selectedTagCounts.GetValueOrDefault(tagId) + 1;
        }
        if (rankedIds.Count == 0) return [];

        var cards = await connection.QueryAsync<CardRow>(new CommandDefinition("""
            SELECT c.id AS Id, c.type AS Type, c.title AS Title, c.preview AS Preview,
                   c.properties AS Properties, c.metadata AS Metadata,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM deck_cards dc JOIN decks d ON d.id = dc.deck_id
                       WHERE dc.card_id = c.id AND d.system_key = 'favorites'
                   ) THEN 1 ELSE 0 END AS IsFavorite
            FROM cards c WHERE c.id IN @Ids AND c.type IN ('media', 'comic', 'set');
            """, new { Ids = rankedIds }, cancellationToken: cancellationToken));
        var byId = cards.ToDictionary(card => card.Id);
        return rankedIds.Where(byId.ContainsKey)
            .Select(id => new RecommendationItem(ToSummary(byId[id]), Math.Round(selectedScores[id], 3)))
            .ToArray();
    }

    private async Task<HashSet<long>> GetAvailableIdsAsync(MySqlConnection connection, long[] ids,
        CancellationToken cancellationToken)
    {
        var available = new HashSet<long>();
        foreach (var batch in ids.Chunk(100))
        {
            var cards = (await connection.QueryAsync<AvailabilityRow>(new CommandDefinition("""
                SELECT id AS Id, type AS Type,
                       JSON_UNQUOTE(JSON_EXTRACT(metadata, '$.content_path')) AS ContentPath,
                       JSON_UNQUOTE(JSON_EXTRACT(metadata, '$.playback_path')) AS PlaybackPath,
                       JSON_TYPE(JSON_EXTRACT(metadata, '$.playback_path')) AS PlaybackPathType,
                       JSON_UNQUOTE(JSON_EXTRACT(metadata, '$.preview_path')) AS PreviewPath
                FROM cards WHERE id IN @Ids AND type IN ('media', 'comic', 'set');
                """, new { Ids = batch }, cancellationToken: cancellationToken))).ToArray();

            var collections = new HashSet<long>();
            foreach (var card in cards)
            {
                if (card.Type == "media")
                {
                    if (_previewService.CanReadStoredFile(card.ContentPath) &&
                        (card.PlaybackPathType is null or "NULL" ||
                         (card.PlaybackPathType == "STRING" && _previewService.CanReadStoredFile(card.PlaybackPath))))
                        available.Add(card.Id);
                }
                else if (_previewService.CanReadStoredFile(card.PreviewPath))
                {
                    collections.Add(card.Id);
                }
            }

            if (collections.Count == 0) continue;
            var collectionIds = collections.ToArray();
            const int childPageSize = 500;
            const int maxChildPages = 20;
            // Limit child file probes to 10,000 per batch of collections.
            for (var pageNumber = 0; pageNumber < maxChildPages && collections.Count > 0; pageNumber++)
            {
                var children = (await connection.QueryAsync<ChildAvailabilityRow>(new CommandDefinition("""
                    SELECT r.from_card_id AS ParentId,
                           JSON_UNQUOTE(JSON_EXTRACT(c.metadata, '$.content_path')) AS ContentPath
                    FROM card_relations r JOIN cards c ON c.id = r.to_card_id
                    WHERE r.relation_type = 'contains' AND r.from_card_id IN @Ids AND c.type = 'media'
                    ORDER BY r.from_card_id, r.to_card_id
                    LIMIT @PageSize OFFSET @Offset;
                    """, new { Ids = collectionIds, PageSize = childPageSize,
                        Offset = pageNumber * childPageSize },
                    cancellationToken: cancellationToken))).ToArray();
                foreach (var child in children)
                    if (collections.Contains(child.ParentId) && _previewService.CanReadStoredFile(child.ContentPath))
                    {
                        available.Add(child.ParentId);
                        collections.Remove(child.ParentId);
                    }
                if (children.Length < childPageSize) break;
            }
        }
        return available;
    }

    private static CardSummary ToSummary(CardRow row)
    {
        var metadata = JsonNode.Parse(row.Metadata) ?? new JsonObject();
        var contentPath = metadata["content_path"]?.GetValue<string>();
        return new CardSummary(row.Id, row.Type, row.Title,
            row.Preview is null ? null : $"/api/cards/{row.Id}/preview",
            contentPath is null ? null : $"/api/cards/{row.Id}/content",
            row.Properties is null ? null : JsonNode.Parse(row.Properties), metadata, row.IsFavorite != 0)
        {
            PlaybackUrl = metadata["playback_path"]?.GetValue<string>() is null
                ? null : $"/api/cards/{row.Id}/playback",
            PosterUrl = metadata["poster_path"]?.GetValue<string>() is null
                ? null : $"/api/cards/{row.Id}/poster"
        };
    }

    private sealed class ParentRow { public long ChildId { get; init; } public long ParentId { get; init; } }
    private sealed class AvailabilityRow
    {
        public long Id { get; init; }
        public string Type { get; init; } = "";
        public string? ContentPath { get; init; }
        public string? PlaybackPath { get; init; }
        public string? PlaybackPathType { get; init; }
        public string? PreviewPath { get; init; }
    }
    private sealed class ChildAvailabilityRow { public long ParentId { get; init; } public string? ContentPath { get; init; } }
    private sealed class ViewRow { public long CardId { get; init; } public DateTime ViewedAtUtc { get; init; } }
    private sealed class VoteRow { public long CardId { get; init; } public sbyte Vote { get; init; } }
    private sealed class TagRow { public long CardId { get; init; } public long TagId { get; init; } }
    private sealed class CreatedRow { public long Id { get; init; } public DateTime? CreatedAt { get; init; } }
    private sealed class CardRow
    {
        public long Id { get; init; }
        public string Type { get; init; } = "";
        public string? Title { get; init; }
        public string? Preview { get; init; }
        public string? Properties { get; init; }
        public string Metadata { get; init; } = "{}";
        public int IsFavorite { get; init; }
    }
}

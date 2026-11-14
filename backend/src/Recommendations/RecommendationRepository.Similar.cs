using System.Numerics;
using System.Text.Json;
using Dapper;
using MyApp.Api.Analysis;
using MySqlConnector;

namespace MyApp.Api.Recommendations;

/// <summary>
/// "Similar": cards ranked by how much their content resembles the open card. Shared tags and
/// sources count by rarity (IDF) and category, system meta tags (image, horizontal, lowres...)
/// are ignored, and the score is a cosine, so cards with very many tags do not match everything.
/// The visual tagger's label probabilities and perceptual hashes add to it when they exist.
/// Votes, freshness and the viewing profile do not lift a card here; they belong to "For you".
/// </summary>
public sealed partial class RecommendationRepository
{
    private async Task<RecommendationPage?> GetSimilarPageAsync(string mode, int limit, int offset,
        bool includeTotal, long contextId, CancellationToken cancellationToken)
    {
        var o = _options;
        var rankLimit = includeTotal
            ? Math.Max(o.MaxRankedCards, 1)
            : Math.Min(offset + limit, Math.Max(o.MaxRankedCards, limit));
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        Task<IEnumerable<T>> Query<T>(string sql, object? parameters = null) =>
            connection.QueryAsync<T>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));

        var contextType = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT type FROM cards WHERE id = @Id;", new { Id = contextId }, cancellationToken: cancellationToken));
        if (contextType is null) return null;

        // The open card's family (its comic or set, or its own pages) is excluded from the results.
        var contextParent = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT from_card_id FROM card_relations
            WHERE to_card_id = @Id AND relation_type = 'contains' LIMIT 1;
            """, new { Id = contextId }, cancellationToken: cancellationToken));
        var familyRoot = contextParent ?? contextId;
        var contextChildren = contextParent is null
            ? (await Query<long>("""
                SELECT to_card_id FROM card_relations
                WHERE from_card_id = @Id AND relation_type = 'contains'
                ORDER BY to_card_id LIMIT 500;
                """, new { Id = contextId })).ToArray()
            : [];

        // What the open card is about: its own tags and sources; a page adds its collection's
        // own tags at half weight, and a collection adds how often each tag occurs on its pages.
        var contentMembers = new List<long> { contextId };
        if (contextParent is long parentId) contentMembers.Add(parentId);
        contentMembers.AddRange(contextChildren);
        var memberFeatures = await LoadSimilarFeaturesAsync(connection, contentMembers, cancellationToken);
        var termFrequency = new Dictionary<long, double>();
        var childSet = contextChildren.ToHashSet();
        var childCounts = new Dictionary<long, int>();
        foreach (var (cardId, feature) in memberFeatures)
        {
            if (cardId == contextId) termFrequency[feature] = 1;
            else if (cardId == contextParent)
                termFrequency[feature] = Math.Max(termFrequency.GetValueOrDefault(feature), 0.5);
            else if (childSet.Contains(cardId)) childCounts[feature] = childCounts.GetValueOrDefault(feature) + 1;
        }
        foreach (var (feature, count) in childCounts)
            termFrequency[feature] = Math.Max(termFrequency.GetValueOrDefault(feature),
                Math.Sqrt((double)count / contextChildren.Length));

        var specificity = new Dictionary<long, double>();
        await AddSimilarSpecificityAsync(connection, termFrequency.Keys, specificity, cancellationToken);
        var contextVector = termFrequency
            .Select(pair => (Feature: pair.Key, Weight: pair.Value * specificity.GetValueOrDefault(pair.Key)))
            .Where(pair => pair.Weight > 0)
            .OrderByDescending(pair => pair.Weight).ThenBy(pair => pair.Feature)
            .Take(o.SimilarContextTagLimit)
            .ToDictionary(pair => pair.Feature, pair => pair.Weight);
        var contextNorm = Math.Sqrt(contextVector.Values.Sum(weight => weight * weight));
        var contextTagIds = contextVector.Keys.Where(feature => feature > 0).ToArray();
        var contextSourceIds = contextVector.Keys.Where(feature => feature < 0).Select(feature => -feature).ToArray();

        // Candidates: cards sharing the most specific tags, cards from the same sources, cards with
        // a pending suggestion of a context tag, and cards that look alike by perceptual hash.
        var candidateIds = new HashSet<long>();
        if (contextTagIds.Length > 0)
        {
            var weights = contextTagIds.Select(tagId => new { t = tagId, w = Math.Round(contextVector[tagId], 6) });
            candidateIds.UnionWith(await Query<long>("""
                SELECT ct.card_id
                FROM card_tags ct
                JOIN JSON_TABLE(@Weights, '$[*]' COLUMNS (
                    tag_id BIGINT PATH '$.t', weight DOUBLE PATH '$.w')) jt ON jt.tag_id = ct.tag_id
                JOIN cards c ON c.id = ct.card_id AND c.type IN ('media', 'comic', 'set')
                GROUP BY ct.card_id
                ORDER BY SUM(jt.weight) DESC, ct.card_id DESC
                LIMIT @Limit;
                """, new { Weights = JsonSerializer.Serialize(weights), Limit = o.SimilarCandidateLimit }));
            candidateIds.UnionWith(await Query<long>("""
                SELECT card_id FROM (
                    SELECT card_id, ROW_NUMBER() OVER (PARTITION BY tag_id
                               ORDER BY confidence DESC, card_id DESC) AS rn
                    FROM card_tag_suggestions
                    WHERE state = 'pending' AND confidence >= @MinConfidence AND tag_id IN @TagIds
                ) ranked WHERE rn <= 25;
                """, new { MinConfidence = o.SuggestMinConfidence,
                    TagIds = contextTagIds.OrderByDescending(tagId => contextVector[tagId]).Take(8).ToArray() }));
        }
        if (contextSourceIds.Length > 0)
            candidateIds.UnionWith(await Query<long>("""
                SELECT DISTINCT card_id FROM card_sources WHERE source_id IN @Ids
                ORDER BY card_id DESC LIMIT 200;
                """, new { Ids = contextSourceIds }));

        var hashDistances = new Dictionary<long, int>();
        var contextHashes = (await Query<long>(
            "SELECT hash FROM card_perceptual_hashes WHERE card_id IN @Ids AND hash IS NOT NULL;",
            new { Ids = contentMembers.Take(50).ToArray() })).ToArray();
        if (contextHashes.Length > 0)
        {
            foreach (var row in await Query<HashRow>(
                         "SELECT card_id AS CardId, hash AS Hash FROM card_perceptual_hashes WHERE hash IS NOT NULL;"))
            {
                var distance = contextHashes.Min(hash => BitOperations.PopCount((ulong)(hash ^ row.Hash)));
                if (distance <= o.SimilarHashMaxDistance) hashDistances[row.CardId] = distance;
            }
            candidateIds.UnionWith(hashDistances.OrderBy(pair => pair.Value).ThenByDescending(pair => pair.Key)
                .Take(50).Select(pair => pair.Key));
        }

        candidateIds.ExceptWith(contentMembers);
        // A small or sparsely tagged library still gets a list: the newest cards fill it with score 0.
        if (candidateIds.Count < rankLimit * 2)
        {
            candidateIds.UnionWith(await Query<long>("""
                SELECT c.id FROM cards c
                WHERE c.type IN ('media', 'comic', 'set')
                  AND NOT EXISTS (SELECT 1 FROM card_relations r
                                  WHERE r.to_card_id = c.id AND r.relation_type = 'contains')
                ORDER BY c.id DESC LIMIT @Limit;
                """, new { Limit = o.NewestCardLimit }));
            if (mode == "pages")
                candidateIds.UnionWith(await Query<long>("""
                    SELECT r.to_card_id FROM card_relations r
                    JOIN cards c ON c.id = r.to_card_id
                    WHERE r.relation_type = 'contains' AND c.type = 'media'
                    ORDER BY r.to_card_id DESC LIMIT 200;
                    """));
        }
        if (candidateIds.Count == 0) return new RecommendationPage([], includeTotal ? 0 : null);

        var ids = candidateIds.ToArray();
        var parents = (await Query<ParentRow>("""
            SELECT to_card_id AS ChildId, from_card_id AS ParentId
            FROM card_relations WHERE relation_type = 'contains' AND to_card_id IN @Ids;
            """, new { Ids = ids })).ToDictionary(row => row.ChildId, row => row.ParentId);
        long Family(long id) => parents.GetValueOrDefault(id, id);
        long Resolve(long id) => mode == "collections" && parents.TryGetValue(id, out var parent) ? parent : id;
        ids = ids.Where(id => Family(id) != familyRoot).ToArray();

        // Each candidate's own vector: its tags and sources by specificity, plus confident pending
        // suggestions of the open card's tags (scaled by confidence).
        var vectors = ids.ToDictionary(id => id, _ => new Dictionary<long, double>());
        var candidateFeatures = await LoadSimilarFeaturesAsync(connection, ids, cancellationToken);
        await AddSimilarSpecificityAsync(connection, candidateFeatures.Select(row => row.Feature),
            specificity, cancellationToken);
        foreach (var (cardId, feature) in candidateFeatures)
            if (specificity.GetValueOrDefault(feature) is var spec && spec > 0) vectors[cardId][feature] = spec;
        if (contextTagIds.Length > 0)
            foreach (var row in await Query<SuggestionRow>("""
                         SELECT card_id AS CardId, tag_id AS TagId, confidence AS Confidence
                         FROM card_tag_suggestions
                         WHERE state = 'pending' AND confidence >= @MinConfidence
                           AND card_id IN @Ids AND tag_id IN @TagIds;
                         """, new { MinConfidence = o.SuggestMinConfidence, Ids = ids, TagIds = contextTagIds }))
                if (vectors.TryGetValue(row.CardId, out var vector) && !vector.ContainsKey(row.TagId))
                    vector[row.TagId] = (double)row.Confidence * specificity.GetValueOrDefault(row.TagId);

        var visual = await GetVisualSimilarityAsync(connection, contextId, contextChildren, ids, cancellationToken);

        var content = new Dictionary<long, double>();
        foreach (var id in ids)
        {
            var vector = vectors[id];
            var norm = Math.Sqrt(vector.Values.Sum(weight => weight * weight));
            var tagSimilarity = norm > 0 && contextNorm > 0
                ? vector.Sum(pair => pair.Value * contextVector.GetValueOrDefault(pair.Key)) / (norm * contextNorm)
                : 0;
            var hashSimilarity = hashDistances.TryGetValue(id, out var distance)
                ? Math.Max(0, 1 - distance / (o.SimilarHashMaxDistance + 1.0)) : 0;
            content[id] = tagSimilarity + o.SimilarVisualWeight * visual.GetValueOrDefault(id) +
                o.SimilarHashWeight * hashSimilarity;
        }

        // Collections mode folds pages into their collection, which scores as its best page.
        var scores = new Dictionary<long, double>();
        var bestMember = new Dictionary<long, long>();
        foreach (var id in ids)
        {
            var key = Resolve(id);
            if (key == familyRoot) continue;
            if (!scores.TryGetValue(key, out var current) || content[id] > current ||
                (content[id] == current && id == key))
            {
                scores[key] = content[id];
                bestMember[key] = id;
            }
        }
        foreach (var key in scores.Keys.ToArray()) scores[key] *= o.SimilarScale;

        // Recently viewed cards step back for a few days so the list does not repeat itself.
        var seenCutoff = now.AddDays(-o.SeenWindowDays);
        var seen = new Dictionary<long, double>();
        foreach (var view in await Query<ViewRow>("""
                     SELECT card_id AS CardId, viewed_at_utc AS ViewedAtUtc FROM card_view_events
                     WHERE viewed_at_utc >= @Cutoff AND card_id IN @Ids;
                     """, new { Cutoff = seenCutoff, Ids = ids.Concat(scores.Keys).Distinct().ToArray() }))
        {
            var key = Resolve(view.CardId);
            if (!scores.ContainsKey(key)) continue;
            var ageDays = Math.Max(0, (now - view.ViewedAtUtc).TotalDays);
            seen[key] = seen.GetValueOrDefault(key) + 1.0 / (1.0 + ageDays / o.ViewHalfLifeDays);
        }
        foreach (var (key, weight) in seen) scores[key] -= o.SimilarSeenPenalty * Math.Min(1, weight);

        var familyIds = ids.Select(Family).Concat(scores.Keys).Distinct().ToArray();
        var latestNegativeIds = (await Query<long>("""
            SELECT v.card_id FROM card_daily_votes v
            WHERE v.card_id IN @Ids AND v.vote = -1
              AND NOT EXISTS (
                  SELECT 1 FROM card_daily_votes newer
                  WHERE newer.card_id = v.card_id
                    AND (newer.updated_at_utc > v.updated_at_utc
                         OR (newer.updated_at_utc = v.updated_at_utc
                             AND newer.vote_date > v.vote_date))
              );
            """, new { Ids = familyIds.Concat(ids).Distinct().ToArray() })).ToHashSet();
        var negativeFamilies = latestNegativeIds.Select(Family).ToHashSet();

        var availableIds = await GetAvailableIdsAsync(connection, scores.Keys.ToArray(), cancellationToken);
        var eligible = scores.Keys.Where(id => availableIds.Contains(id) &&
                !latestNegativeIds.Contains(id) &&
                (mode == "collections" ? !negativeFamilies.Contains(id) : !latestNegativeIds.Contains(Family(id))))
            .ToList();
        var childFamilies = mode == "pages"
            ? eligible.Where(parents.ContainsKey).Select(Family).ToHashSet()
            : new HashSet<long>();

        // Diversity: a card that resembles an already chosen one in ways the open card does not
        // explain (beyond the shared context tags) loses part of its score, so near-identical
        // variants and one-artist runs spread out.
        var residuals = new Dictionary<long, Dictionary<long, double>>();
        foreach (var id in eligible)
        {
            var residual = vectors.GetValueOrDefault(bestMember[id]) is { } vector
                ? vector.Where(pair => !contextVector.ContainsKey(pair.Key)).ToDictionary()
                : new Dictionary<long, double>();
            var norm = Math.Sqrt(residual.Values.Sum(weight => weight * weight));
            if (norm > 0) foreach (var key in residual.Keys.ToArray()) residual[key] /= norm;
            residuals[id] = residual;
        }
        var redundancy = eligible.ToDictionary(id => id, _ => 0.0);
        var familyCounts = new Dictionary<long, int>();
        var rankedIds = new List<long>(Math.Min(rankLimit, eligible.Count));
        var selectedScores = new Dictionary<long, double>();
        var remaining = eligible;
        var diversity = o.SimilarScale * o.SimilarDiversity;
        while (rankedIds.Count < rankLimit)
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
                var marginal = scores[id] - diversity * redundancy[id];
                if (marginal > bestScore || (marginal == bestScore && (bestId is null || id > bestId.Value)))
                {
                    bestId = id;
                    bestScore = marginal;
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
            var chosen = residuals[selectedId];
            if (chosen.Count == 0) continue;
            foreach (var id in remaining)
            {
                var other = residuals[id];
                if (other.Count == 0) continue;
                var (small, large) = other.Count < chosen.Count ? (other, chosen) : (chosen, other);
                var overlap = small.Sum(pair => pair.Value * large.GetValueOrDefault(pair.Key));
                if (overlap > redundancy[id]) redundancy[id] = overlap;
            }
        }

        int? total = includeTotal ? rankedIds.Count : null;
        var pageIds = rankedIds.Skip(offset).Take(limit).ToList();
        if (pageIds.Count == 0) return new RecommendationPage([], total);
        var cards = await Query<CardRow>("""
            SELECT c.id AS Id, c.type AS Type, c.title AS Title, c.preview AS Preview,
                   c.properties AS Properties, c.metadata AS Metadata,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM deck_cards dc JOIN decks d ON d.id = dc.deck_id
                       WHERE dc.card_id = c.id AND d.system_key = 'favorites'
                   ) THEN 1 ELSE 0 END AS IsFavorite
            FROM cards c WHERE c.id IN @Ids AND c.type IN ('media', 'comic', 'set');
            """, new { Ids = pageIds });
        var byId = cards.ToDictionary(card => card.Id);
        return new RecommendationPage(pageIds.Where(byId.ContainsKey)
            .Select(id => new RecommendationItem(ToSummary(byId[id]), Math.Round(selectedScores[id], 3)))
            .ToArray(), total);
    }

    /// <summary>Tags (positive keys) and sources (negative keys) of the given cards; system meta tags are left out.</summary>
    private static async Task<List<(long CardId, long Feature)>> LoadSimilarFeaturesAsync(
        MySqlConnection connection, IReadOnlyCollection<long> cardIds, CancellationToken cancellationToken)
    {
        var features = new List<(long, long)>();
        if (cardIds.Count == 0) return features;
        var ids = cardIds.Distinct().ToArray();
        features.AddRange((await connection.QueryAsync<TagRow>(new CommandDefinition("""
            SELECT DISTINCT ct.card_id AS CardId, ct.tag_id AS TagId
            FROM card_tags ct JOIN tags t ON t.id = ct.tag_id
            WHERE ct.card_id IN @Ids AND t.origin <> 'system';
            """, new { Ids = ids }, cancellationToken: cancellationToken)))
            .Select(row => (row.CardId, row.TagId)));
        features.AddRange((await connection.QueryAsync<TagRow>(new CommandDefinition("""
            SELECT DISTINCT card_id AS CardId, source_id AS TagId FROM card_sources WHERE card_id IN @Ids;
            """, new { Ids = ids }, cancellationToken: cancellationToken)))
            .Select(row => (row.CardId, -row.TagId)));
        return features;
    }

    /// <summary>
    /// Adds missing specificities: <c>multiplier · ln(1 + N/(1 + df)) / ln(1 + N)</c> over top-level
    /// cards, as in "For you", but meta tags count <see cref="RecommendationOptions.SimilarMetaMultiplier"/>
    /// and sources <see cref="RecommendationOptions.SimilarSourceMultiplier"/>.
    /// </summary>
    private async Task AddSimilarSpecificityAsync(MySqlConnection connection, IEnumerable<long> features,
        Dictionary<long, double> specificity, CancellationToken cancellationToken)
    {
        var missing = features.Where(feature => !specificity.ContainsKey(feature)).Distinct().ToArray();
        if (missing.Length == 0) return;
        var o = _options;
        var topLevelCount = Math.Max(1, await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT COUNT(*) FROM cards c
            WHERE c.type IN ('media', 'comic', 'set')
              AND NOT EXISTS (SELECT 1 FROM card_relations r
                              WHERE r.to_card_id = c.id AND r.relation_type = 'contains');
            """, cancellationToken: cancellationToken)));
        double Idf(long df) => Math.Log(1 + (double)topLevelCount / (1 + df)) / Math.Log(1 + topLevelCount);

        var tagIds = missing.Where(feature => feature > 0).ToArray();
        if (tagIds.Length > 0)
        {
            // COUNT(*) is answered from idx_card_tags_tag alone.
            var frequencies = (await connection.QueryAsync<TagFrequencyRow>(new CommandDefinition("""
                SELECT tag_id AS TagId, COUNT(*) AS Df FROM card_tags WHERE tag_id IN @Ids GROUP BY tag_id;
                """, new { Ids = tagIds }, cancellationToken: cancellationToken)))
                .ToDictionary(row => row.TagId, row => row.Df);
            var categories = (await connection.QueryAsync<TagCategoryRow>(new CommandDefinition(
                    "SELECT id AS TagId, category AS Category FROM tags WHERE id IN @Ids;",
                    new { Ids = tagIds }, cancellationToken: cancellationToken)))
                .ToDictionary(row => row.TagId, row => row.Category);
            foreach (var tagId in tagIds)
            {
                var category = categories.GetValueOrDefault(tagId);
                var multiplier = string.Equals(category, "meta", StringComparison.OrdinalIgnoreCase)
                    ? o.SimilarMetaMultiplier
                    : category is not null && o.CategoryMultipliers.TryGetValue(category, out var mapped)
                        ? mapped : o.UnknownCategoryMultiplier;
                specificity[tagId] = multiplier * Idf(frequencies.GetValueOrDefault(tagId));
            }
        }
        var sourceIds = missing.Where(feature => feature < 0).Select(feature => -feature).ToArray();
        if (sourceIds.Length > 0)
        {
            var frequencies = (await connection.QueryAsync<TagFrequencyRow>(new CommandDefinition("""
                SELECT source_id AS TagId, COUNT(DISTINCT card_id) AS Df FROM card_sources
                WHERE source_id IN @Ids GROUP BY source_id;
                """, new { Ids = sourceIds }, cancellationToken: cancellationToken)))
                .ToDictionary(row => row.TagId, row => row.Df);
            foreach (var sourceId in sourceIds)
                specificity[-sourceId] = o.SimilarSourceMultiplier * Idf(frequencies.GetValueOrDefault(sourceId));
        }
    }

    /// <summary>
    /// Cosine of the visual tagger's label probabilities against the open card (for a collection,
    /// the peak over its first pages), rescaled so the candidates' median counts as 0: most
    /// pictures share labels such as "1girl", so only above-typical resemblance is a signal.
    /// </summary>
    private async Task<Dictionary<long, double>> GetVisualSimilarityAsync(MySqlConnection connection,
        long contextId, long[] contextChildren, long[] candidateIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, double>();
        if (candidateIds.Length == 0 || _options.SimilarVisualWeight <= 0) return result;
        var contextRows = (await connection.QueryAsync<FeatureRow>(new CommandDefinition("""
            SELECT card_id AS CardId, model_key AS ModelKey, dimension AS Dimension, features AS Features
            FROM card_visual_features WHERE card_id IN @Ids;
            """, new { Ids = contextChildren.Take(20).Prepend(contextId).ToArray() },
            cancellationToken: cancellationToken))).ToArray();
        var own = contextRows.FirstOrDefault(row => row.CardId == contextId) ?? contextRows.FirstOrDefault();
        if (own is null) return result;
        var context = TryDecode(own.Dimension, own.Features);
        if (contextRows.Length > 1 && contextRows.All(row => row.CardId != contextId))
            context = VisualFeatureVector.MaxOf(contextRows
                .Where(row => row.ModelKey == own.ModelKey && row.Dimension == own.Dimension)
                .Select(row => TryDecode(row.Dimension, row.Features)));
        if (context is null) return result;

        var cosines = new Dictionary<long, double>();
        foreach (var batch in candidateIds.Chunk(200))
            foreach (var row in await connection.QueryAsync<FeatureRow>(new CommandDefinition("""
                         SELECT card_id AS CardId, model_key AS ModelKey, dimension AS Dimension, features AS Features
                         FROM card_visual_features
                         WHERE card_id IN @Ids AND model_key = @ModelKey AND dimension = @Dimension;
                         """, new { Ids = batch, own.ModelKey, own.Dimension }, cancellationToken: cancellationToken)))
                if (TryDecode(row.Dimension, row.Features) is { } vector)
                    cosines[row.CardId] = Cosine(context, vector);
        if (cosines.Count == 0) return result;
        var ordered = cosines.Values.Order().ToArray();
        var median = ordered[ordered.Length / 2];
        foreach (var (cardId, cosine) in cosines)
            result[cardId] = median >= 1 ? 0 : Math.Max(0, (cosine - median) / (1 - median));
        return result;

        static VisualFeatureVector? TryDecode(int dimension, byte[] bytes)
        {
            try { return VisualFeatureVector.FromBytes(dimension, bytes); }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException) { return null; }
        }
    }

    private static double Cosine(VisualFeatureVector left, VisualFeatureVector right)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0;
        foreach (var value in left.Values) leftNorm += value * value;
        foreach (var value in right.Values) rightNorm += value * value;
        for (int i = 0, j = 0; i < left.Count && j < right.Count;)
        {
            if (left.Indices[i] == right.Indices[j]) dot += left.Values[i++] * right.Values[j++];
            else if (left.Indices[i] < right.Indices[j]) i++;
            else j++;
        }
        return leftNorm > 0 && rightNorm > 0 ? dot / Math.Sqrt(leftNorm * rightNorm) : 0;
    }

    private sealed class HashRow { public long CardId { get; init; } public long Hash { get; init; } }
    private sealed class FeatureRow
    {
        public long CardId { get; init; }
        public string ModelKey { get; init; } = "";
        public int Dimension { get; init; }
        public byte[] Features { get; init; } = [];
    }
}

using Dapper;
using MySqlConnector;
using static MyApp.Api.Cards.CardMapping;
using static MyApp.Api.Cards.CardSql;

namespace MyApp.Api.Cards;

public sealed partial class CardRepository
{
    public Task<IReadOnlyList<CardSummary>> ListAsync(
        IReadOnlyList<string> types,
        string? mediaType,
        string? search,
        bool excludeContainedMedia,
        IReadOnlyList<long> tagIds,
        long? sourceId,
        IReadOnlyList<long> catalogTagIds,
        long? catalogSourceId,
        string sort,
        string order,
        int limit = 200,
        int offset = 0) =>
        ListAsync(new CardListQuery(
            types, mediaType, search, excludeContainedMedia, tagIds, sourceId,
            catalogTagIds, catalogSourceId, sort, order, limit, offset));

    public async Task<IReadOnlyList<CardSummary>> ListAsync(CardListQuery query)
    {
        var (types, mediaType, search, excludeContainedMedia, tagIds, sourceId,
            catalogTagIds, catalogSourceId, sort, order, limit, offset, deckId) = query;
        var positionSort = string.Equals(sort, "position", StringComparison.OrdinalIgnoreCase);
        if (positionSort && deckId is null)
            throw new ArgumentException("sort=position requires deck_id.", nameof(query));
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        await using var connection = new MySqlConnection(_connectionString);

        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (deckId is not null)
        {
            var deckExists = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM decks WHERE id = @DeckId;", new { DeckId = deckId.Value });
            if (deckExists == 0) throw new KeyNotFoundException("Deck not found.");
            parameters.Add("DeckId", deckId.Value);
        }

        if (types.Count > 0)
        {
            conditions.Add("cards.type IN @Types");
            parameters.Add("Types", types);
        }

        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            conditions.Add("JSON_UNQUOTE(JSON_EXTRACT(cards.metadata, '$.media_type')) = @MediaType");
            parameters.Add("MediaType", mediaType);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("""
                (CAST(cards.id AS CHAR) = @Search OR cards.title LIKE @SearchLike ESCAPE '!'
                 OR EXISTS (
                    SELECT 1 FROM card_tags ct JOIN tags t ON t.id = ct.tag_id
                    WHERE (ct.card_id = cards.id OR ct.card_id IN (
                        SELECT child.to_card_id FROM card_relations child
                        WHERE child.from_card_id = cards.id AND child.relation_type = 'contains'))
                      AND (t.name LIKE @SearchLike ESCAPE '!' OR EXISTS (
                          SELECT 1 FROM tag_aliases a WHERE a.tag_id = t.id
                            AND a.alias LIKE @SearchLike ESCAPE '!'))
                 ) OR EXISTS (
                    SELECT 1 FROM card_relations r JOIN tags t ON t.legacy_card_id = r.to_card_id
                    WHERE r.from_card_id = cards.id AND r.relation_type = 'tagged_with'
                      AND (t.name LIKE @SearchLike ESCAPE '!' OR EXISTS (
                          SELECT 1 FROM tag_aliases a WHERE a.tag_id = t.id
                            AND a.alias LIKE @SearchLike ESCAPE '!'))
                 ))
                """);
            parameters.Add("Search", search);
            var escapedSearch = search.Replace("!", "!!").Replace("%", "!%")
                .Replace("_", "!_");
            parameters.Add("SearchLike", $"%{escapedSearch}%");
        }

        if (excludeContainedMedia)
        {
            conditions.Add("""
                NOT EXISTS (
                    SELECT 1
                    FROM card_relations r
                    WHERE r.to_card_id = cards.id
                      AND r.relation_type = 'contains'
                )
                """);
        }

        for (var index = 0; index < tagIds.Count; index++)
        {
            var parameterName = $"TagId{index}";
            conditions.Add($"""
                EXISTS (
                    SELECT 1
                    FROM card_relations relation_tags
                    WHERE relation_tags.from_card_id = cards.id
                      AND relation_tags.relation_type = 'tagged_with'
                      AND relation_tags.to_card_id = @{parameterName}
                )
                """);
            parameters.Add(parameterName, tagIds[index]);
        }

        if (sourceId is not null)
        {
            conditions.Add("""
                EXISTS (
                    SELECT 1
                    FROM card_relations relation_sources
                    WHERE relation_sources.from_card_id = cards.id
                      AND relation_sources.relation_type = 'sourced_from'
                      AND relation_sources.to_card_id = @SourceId
                )
                """);
            parameters.Add("SourceId", sourceId.Value);
        }

        for (var index = 0; index < catalogTagIds.Count; index++)
        {
            var parameterName = $"CatalogTagId{index}";
            conditions.Add($"""
                (EXISTS (
                    SELECT 1 FROM card_tags ct WHERE ct.tag_id = @{parameterName}
                      AND ct.legacy_relation_id IS NULL
                      AND (ct.card_id = cards.id OR ct.card_id IN (
                          SELECT child.to_card_id FROM card_relations child
                          WHERE child.from_card_id = cards.id AND child.relation_type = 'contains'))
                ) OR EXISTS (
                    SELECT 1 FROM card_relations r JOIN tags t ON t.legacy_card_id = r.to_card_id
                    WHERE (r.from_card_id = cards.id OR r.from_card_id IN (
                        SELECT child.to_card_id FROM card_relations child
                        WHERE child.from_card_id = cards.id AND child.relation_type = 'contains'))
                      AND r.relation_type = 'tagged_with'
                      AND t.id = @{parameterName}
                ))
                """);
            parameters.Add(parameterName, catalogTagIds[index]);
        }

        if (catalogSourceId is not null)
        {
            conditions.Add("""
                (EXISTS (
                    SELECT 1 FROM card_sources cs WHERE cs.card_id = cards.id
                      AND cs.source_id = @CatalogSourceId AND cs.legacy_relation_id IS NULL
                ) OR EXISTS (
                    SELECT 1 FROM card_relations r JOIN sources s ON s.legacy_card_id = r.to_card_id
                    WHERE r.from_card_id = cards.id AND r.relation_type = 'sourced_from'
                      AND s.id = @CatalogSourceId
                ))
                """);
            parameters.Add("CatalogSourceId", catalogSourceId.Value);
        }

        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
        var orderDirection = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)
            ? "ASC"
            : "DESC";
        var orderColumn = string.Equals(sort, "created_at", StringComparison.OrdinalIgnoreCase)
            ? "cards.created_at"
            : "cards.id";
        var orderBy = positionSort
            ? "dc.position ASC, cards.id ASC"
            : $"{orderColumn} {orderDirection}, cards.id {orderDirection}";
        var deckJoin = deckId is null
            ? ""
            : "JOIN deck_cards dc ON dc.card_id = cards.id AND dc.deck_id = @DeckId";
        var deckColumns = deckId is null
            ? "NULL AS DeckPosition, NULL AS MembershipProperties"
            : "dc.position AS DeckPosition, dc.properties AS MembershipProperties";
        parameters.Add("Limit", limit);
        parameters.Add("Offset", offset);

        var rows = await connection.QueryAsync<DbListCard>(
            $"""
            SELECT
                cards.id AS {nameof(DbListCard.Id)},
                cards.type AS {nameof(DbListCard.Type)},
                cards.title AS {nameof(DbListCard.Title)},
                cards.preview AS {nameof(DbListCard.Preview)},
                cards.properties AS {nameof(DbListCard.Properties)},
                cards.metadata AS {nameof(DbListCard.Metadata)},
                {FavoriteSelectSql("cards.id")} AS {nameof(DbListCard.IsFavorite)},
                {deckColumns}
            FROM cards
            {deckJoin}
            {where}
            ORDER BY {orderBy}
            LIMIT @Limit OFFSET @Offset;
            """,
            parameters);

        return rows.Select(row => ToSummary(new DbCard(
                row.Id, row.Type, row.Title, row.Preview, row.Properties, row.Metadata, row.IsFavorite))
            with
            {
                DeckPosition = row.DeckPosition,
                MembershipProperties = deckId is null ? null : CardMetadata.ParseOptionalJson(row.MembershipProperties),
            }).ToList();
    }
}

internal sealed class DbListCard
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string? Title { get; set; }
    public string? Preview { get; set; }
    public string? Properties { get; set; }
    public string Metadata { get; set; } = "{}";
    public long IsFavorite { get; set; }
    public int? DeckPosition { get; set; }
    public string? MembershipProperties { get; set; }
}

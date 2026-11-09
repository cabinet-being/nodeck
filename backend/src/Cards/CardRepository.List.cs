using Dapper;
using MySqlConnector;
using static MyApp.Api.Cards.CardMapping;
using static MyApp.Api.Cards.CardSql;

namespace MyApp.Api.Cards;

public sealed partial class CardRepository
{
    public async Task<IReadOnlyList<CardSummary>> ListAsync(
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
        int offset = 0)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        await using var connection = new MySqlConnection(_connectionString);

        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (types.Count > 0)
        {
            conditions.Add("type IN @Types");
            parameters.Add("Types", types);
        }

        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            conditions.Add("JSON_UNQUOTE(JSON_EXTRACT(metadata, '$.media_type')) = @MediaType");
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
            ? "created_at"
            : "id";
        parameters.Add("Limit", limit);
        parameters.Add("Offset", offset);

        var rows = await connection.QueryAsync<DbCard>(
            $"""
            SELECT
                id AS {nameof(DbCard.Id)},
                type AS {nameof(DbCard.Type)},
                title AS {nameof(DbCard.Title)},
                preview AS {nameof(DbCard.Preview)},
                properties AS {nameof(DbCard.Properties)},
                metadata AS {nameof(DbCard.Metadata)},
                {FavoriteSelectSql("cards.id")} AS {nameof(DbCard.IsFavorite)}
            FROM cards
            {where}
            ORDER BY {orderColumn} {orderDirection}, id {orderDirection}
            LIMIT @Limit OFFSET @Offset;
            """,
            parameters);

        return rows.Select(ToSummary).ToList();
    }
}

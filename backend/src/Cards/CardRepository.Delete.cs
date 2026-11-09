using Dapper;
using MySqlConnector;

namespace MyApp.Api.Cards;

public sealed partial class CardRepository
{
    public async Task<CardDeletionPreview?> GetDeletionImpactAsync(long id)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        var type = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT type FROM cards WHERE id = @Id;", new { Id = id });
        if (type is null) return null;

        long[] childIds = type is "comic" or "set"
            ? (await connection.QueryAsync<long>(
                "SELECT to_card_id FROM card_relations WHERE from_card_id = @Id AND relation_type = 'contains';",
                new { Id = id })).ToArray()
            : [];
        var parentMemberships = await connection.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM deck_cards WHERE card_id = @Id;", new { Id = id });
        var totalMemberships = childIds.Length == 0 ? parentMemberships : parentMemberships +
            await connection.QuerySingleAsync<long>(
                "SELECT COUNT(*) FROM deck_cards WHERE card_id IN @Ids;", new { Ids = childIds });
        return new CardDeletionPreview(
            new DeletionImpact(1, parentMemberships),
            new DeletionImpact(1 + childIds.Length, totalMemberships));
    }

    public async Task<IReadOnlyList<long>> DeleteAsync(long id, bool deleteContents = false)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var card = await connection.QuerySingleOrDefaultAsync<DbCard>(
            $"""
            SELECT
                id AS Id,
                type AS Type,
                title AS Title,
                preview AS Preview,
                properties AS Properties,
                metadata AS Metadata,
                FALSE AS IsFavorite
            FROM cards
            WHERE id = @Id
            FOR UPDATE;
            """,
            new { Id = id },
            transaction);

        if (card is null)
        {
            await transaction.RollbackAsync();
            return [];
        }

        var deletedCardIds = new List<long> { id };
        long? parentId = null;
        if (card.Type == "media")
            parentId = await connection.QueryFirstOrDefaultAsync<long?>(
                "SELECT from_card_id FROM card_relations WHERE to_card_id = @Id AND relation_type = 'contains' FOR UPDATE;",
                new { Id = id }, transaction);

        if (card.Type is "comic" or "set")
        {
            var containedCardIds = await connection.QueryAsync<long>(
                """
                SELECT c.id
                FROM card_relations r
                JOIN cards c ON c.id = r.to_card_id
                WHERE r.from_card_id = @Id
                  AND r.relation_type = 'contains'
                  AND c.type = 'media'
                ORDER BY r.id ASC;
                """,
                new { Id = id },
                transaction);

            if (deleteContents)
                deletedCardIds.AddRange(containedCardIds);
            else if (containedCardIds.Any())
                await connection.ExecuteAsync(
                    "DELETE FROM card_relations WHERE relation_type = 'next_in_sequence' AND from_card_id IN @Ids AND to_card_id IN @Ids;",
                    new { Ids = containedCardIds.ToArray() }, transaction);
        }

        var affectedRows = await connection.ExecuteAsync(
            """
            DELETE FROM cards
            WHERE id IN @Ids;
            """,
            new { Ids = deletedCardIds },
            transaction);

        if (parentId is not null)
        {
            var parentType = await connection.QuerySingleAsync<string>(
                "SELECT type FROM cards WHERE id = @Id FOR UPDATE;", new { Id = parentId }, transaction);
            var remainingIds = (await connection.QueryAsync<long>(
                """
                SELECT to_card_id FROM card_relations
                WHERE from_card_id = @Id AND relation_type = 'contains'
                ORDER BY CAST(JSON_UNQUOTE(JSON_EXTRACT(properties, '$.position')) AS SIGNED), id;
                """,
                new { Id = parentId }, transaction)).ToArray();
            await ReplaceCollectionChildrenAsync(connection, transaction, parentId.Value, parentType, remainingIds);
        }

        await transaction.CommitAsync();

        return affectedRows > 0 ? deletedCardIds : [];
    }
}

using Dapper;
using MySqlConnector;
using static MyApp.Api.Cards.CardMapping;
using static MyApp.Api.Cards.CardSql;

namespace MyApp.Api.Cards;

public sealed partial class CardRepository
{
    private readonly string _connectionString;

    public CardRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
    }

    public async Task<CardDetails?> GetByIdAsync(long id)
    {
        await using var connection = new MySqlConnection(_connectionString);

        var card = await connection.QuerySingleOrDefaultAsync<DbCard>(
            $"""
            SELECT
                id AS Id,
                type AS Type,
                title AS Title,
                preview AS Preview,
                properties AS Properties,
                metadata AS Metadata,
                {FavoriteSelectSql("cards.id")} AS IsFavorite
            FROM cards
            WHERE id = @Id;
            """,
            new { Id = id });

        if (card is null)
        {
            return null;
        }

        var outgoing = await GetRelationsAsync(connection, "from_card_id = @Id", id);
        var incoming = await GetRelationsAsync(connection, "to_card_id = @Id", id);
        var containedCards = await GetContainedCardsAsync(connection, id);

        return ToDetails(card, outgoing, incoming, containedCards);
    }
}

namespace MyApp.Api.Cards;

internal static class CardSql
{
    internal static string FavoriteSelectSql(string cardIdExpression) =>
        $"""
        CASE WHEN EXISTS (
            SELECT 1
            FROM deck_cards favorite_deck_cards
            JOIN decks favorite_decks ON favorite_decks.id = favorite_deck_cards.deck_id
            WHERE favorite_decks.system_key = 'favorites'
              AND favorite_deck_cards.card_id = {cardIdExpression}
        ) THEN 1 ELSE 0 END
        """;
}

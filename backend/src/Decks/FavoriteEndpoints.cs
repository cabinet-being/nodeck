namespace MyApp.Api.Decks;

public static class FavoriteEndpoints
{
    public static IEndpointRouteBuilder MapFavoriteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/favorites");

        group.MapGet("", GetFavoritesAsync);
        group.MapPut("/{cardId:long}", AddFavoriteAsync);
        group.MapDelete("/{cardId:long}", RemoveFavoriteAsync);

        return app;
    }

    private static async Task<IResult> GetFavoritesAsync(DeckRepository repository, bool? include_cards)
    {
        // include_cards=false returns just the deck (with its id), so the page can list
        // its cards page by page through GET /api/cards?deck_id=.
        var favorites = await repository.GetFavoritesAsync(include_cards ?? true);

        return Results.Ok(favorites);
    }

    private static async Task<IResult> AddFavoriteAsync(DeckRepository repository, long cardId)
    {
        try
        {
            var favorites = await repository.AddFavoriteAsync(cardId);

            return Results.Ok(favorites);
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> RemoveFavoriteAsync(DeckRepository repository, long cardId)
    {
        var favorites = await repository.RemoveFavoriteAsync(cardId);

        return Results.Ok(favorites);
    }
}

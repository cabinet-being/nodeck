using System.Text.Json;

namespace MyApp.Api.Decks;

public static class DeckEndpoints
{
    public static IEndpointRouteBuilder MapDeckEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/decks");

        group.MapPost("", CreateDeckAsync);
        group.MapGet("", ListDecksAsync);
        group.MapGet("/{id:long}", GetDeckAsync);
        group.MapPut("/{id:long}", UpdateDeckAsync);
        group.MapDelete("/{id:long}", DeleteDeckAsync);
        group.MapPut("/{id:long}/cards/{cardId:long}", AddCardAsync);
        group.MapDelete("/{id:long}/cards/{cardId:long}", RemoveCardAsync);
        app.MapGet("/api/cards/{cardId:long}/decks", GetCardDecksAsync);

        return app;
    }

    private static async Task<IResult> CreateDeckAsync(
        DeckRequest request,
        DeckRepository repository)
    {
        try
        {
            var created = await repository.CreateAsync(request);

            return Results.Created($"/api/decks/{created.Id}", created);
        }
        catch (JsonException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> ListDecksAsync(
        DeckRepository repository,
        HttpResponse response,
        string? search,
        string? sort,
        string? order,
        int? limit,
        int? offset,
        string? include_total)
    {
        if (Paging.Validate(limit, offset) is { } pagingError) return Results.BadRequest(new { error = pagingError });
        var (decks, total) = await repository.ListPageAsync(
            search,
            sort ?? "created_at",
            order ?? "desc",
            limit ?? 200,
            offset ?? 0,
            Paging.WantsTotal(include_total));

        if (total is long count) Paging.SetTotal(response, count);
        return Results.Ok(decks);
    }

    private static async Task<IResult> GetDeckAsync(
        DeckRepository repository,
        long id,
        bool? include_cards)
    {
        var deck = await repository.GetByIdAsync(id, include_cards ?? true);

        return deck is null ? Results.NotFound(new { error = "Deck not found." }) : Results.Ok(deck);
    }

    private static async Task<IResult> UpdateDeckAsync(
        DeckRepository repository,
        long id,
        DeckRequest request)
    {
        try
        {
            var updated = await repository.UpdateAsync(id, request);

            return updated is null ? Results.NotFound(new { error = "Deck not found." }) : Results.Ok(updated);
        }
        catch (JsonException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> DeleteDeckAsync(DeckRepository repository, long id)
    {
        try
        {
            var deleted = await repository.DeleteAsync(id);

            return deleted ? Results.NoContent() : Results.NotFound(new { error = "Deck not found." });
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> GetCardDecksAsync(DeckRepository repository, long cardId)
    {
        var decks = await repository.GetCardDecksAsync(cardId);
        return Results.Ok(decks);
    }

    private static async Task<IResult> AddCardAsync(DeckRepository repository, long id, long cardId)
    {
        try
        {
            var deck = await repository.AddCardAsync(id, cardId);
            return deck is null ? Results.NotFound(new { error = "Deck not found." }) : Results.Ok(deck);
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> RemoveCardAsync(DeckRepository repository, long id, long cardId)
    {
        try
        {
            var deck = await repository.RemoveCardAsync(id, cardId);
            return deck is null ? Results.NotFound(new { error = "Deck not found." }) : Results.Ok(deck);
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }
}

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
        string? search,
        string? sort,
        string? order)
    {
        var decks = await repository.ListAsync(
            search,
            sort ?? "created_at",
            order ?? "desc");

        return Results.Ok(decks);
    }

    private static async Task<IResult> GetDeckAsync(DeckRepository repository, long id)
    {
        var deck = await repository.GetByIdAsync(id);

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
}

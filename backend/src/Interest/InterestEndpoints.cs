namespace MyApp.Api.Interest;

public sealed record RecordViewRequest(Guid EventId, int DurationMs);
public sealed record PutDailyVoteRequest(sbyte Value, string TimeZone);

public static class InterestEndpoints
{
    public static IEndpointRouteBuilder MapInterestEndpoints(this IEndpointRouteBuilder app)
    {
        var cards = app.MapGroup("/api/cards");
        cards.MapPost("/{id:long}/views", RecordViewAsync);
        cards.MapGet("/{id:long}/vote", GetVoteAsync);
        cards.MapPut("/{id:long}/vote", PutVoteAsync);
        return app;
    }

    private static async Task<IResult> RecordViewAsync(long id, RecordViewRequest request,
        InterestRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            var result = await repository.RecordViewAsync(id, request.EventId,
                request.DurationMs, cancellationToken);
            return result == InterestViewResult.NotFound
                ? Results.NotFound(new { error = "Card not found." })
                : Results.NoContent();
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> GetVoteAsync(long id, string? timeZone,
        InterestRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            var status = await repository.GetVoteAsync(id, timeZone ?? "", cancellationToken);
            return status is null ? Results.NotFound(new { error = "Card not found." }) :
                Results.Ok(status);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> PutVoteAsync(long id, PutDailyVoteRequest request,
        InterestRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            var status = await repository.PutVoteAsync(id, request.Value,
                request.TimeZone, cancellationToken);
            return status is null ? Results.NotFound(new { error = "Card not found." }) :
                Results.Ok(status);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }
}

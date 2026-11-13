using MyApp.Api.Cards;
using MyApp.Api.Storage;
using MySqlConnector;

namespace MyApp.Api.Duplicates;

public static class DuplicateEndpoints
{
    public static IEndpointRouteBuilder MapDuplicateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/duplicates");
        group.MapGet("", ListAsync);
        group.MapPost("/merge", MergeAsync);
        group.MapPost("/dismiss", DismissAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(CardRepository repository, string? kind, int? limit, int? offset)
    {
        kind ??= "all";
        if (kind is not ("all" or "exact" or "similar"))
            return Results.BadRequest(new { error = "kind must be all, exact or similar." });
        if (Paging.Validate(limit, offset, maxLimit: 100) is { } error) return Results.BadRequest(new { error });
        return Results.Ok(await repository.FindDuplicateGroupsAsync(kind, limit ?? 20, offset ?? 0));
    }

    private static async Task<IResult> MergeAsync(
        DuplicateMergeRequest request, CardRepository repository, PreviewService previewService,
        ILogger<CardRepository> logger)
    {
        var ids = request.CardIds;
        if (request.SurvivorId <= 0 || ids is null || ids.Count < 1 || ids.Count > CardRepository.MaxMergeCards ||
            ids.Any(id => id <= 0))
            return Results.BadRequest(new
            {
                error = $"survivorId and 1 to {CardRepository.MaxMergeCards} positive cardIds are required.",
            });
        DuplicateMergeResult result;
        try
        {
            result = await repository.MergeDuplicatesAsync(request.SurvivorId, ids);
        }
        catch (DuplicateMergeException exception)
        {
            return exception.NotFound
                ? Results.NotFound(new { error = exception.Message })
                : Results.Conflict(new { error = exception.Message });
        }
        catch (MySqlException exception) when (exception.ErrorCode is MySqlErrorCode.LockDeadlock or
            MySqlErrorCode.LockWaitTimeout)
        {
            return Results.Conflict(new { error = "The library is busy. Try the merge again." });
        }
        // Files go only after the commit, as with deletion; a leftover directory is found by the orphan scan.
        foreach (var id in result.MergedCardIds)
        {
            try { previewService.DeleteCardFiles(id); }
            catch (Exception exception) { logger.LogWarning(exception, "Could not delete files of merged card {CardId}.", id); }
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> DismissAsync(DuplicateDismissRequest request, CardRepository repository)
    {
        var ids = request.CardIds;
        if (ids is null || ids.Distinct().Count() is < 2 or > CardRepository.MaxMergeCards || ids.Any(id => id <= 0))
            return Results.BadRequest(new { error = $"cardIds must hold 2 to {CardRepository.MaxMergeCards} positive ids." });
        var pairs = await repository.DismissDuplicatesAsync(ids);
        return pairs < 0 ? Results.NotFound(new { error = "Card not found." }) : Results.NoContent();
    }
}

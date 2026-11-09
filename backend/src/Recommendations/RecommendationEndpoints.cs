using System.Globalization;

namespace MyApp.Api.Recommendations;

public static class RecommendationEndpoints
{
    public static IEndpointRouteBuilder MapRecommendationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/recommendations", GetAsync);
        app.MapGet("/api/recommendations/daily", GetDailyAsync);
        return app;
    }

    private static async Task<IResult> GetDailyAsync(HttpRequest request, RecommendationRepository repository,
        CancellationToken cancellationToken)
    {
        var query = request.Query;
        var timeZoneId = query.TryGetValue("timeZone", out var zoneValue) ? zoneValue.ToString() : "UTC";
        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out _) ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var timeZone))
            return Results.BadRequest(new { error = "Time zone must be a valid IANA identifier." });

        var limit = 30;
        if (query.TryGetValue("limit", out var limitValue) &&
            (!int.TryParse(limitValue.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit is < 1 or > 50))
            return Results.BadRequest(new { error = "Limit must be from 1 to 50." });

        return Results.Ok(await repository.GetDailyAsync(timeZone!, limit, cancellationToken));
    }

    private static async Task<IResult> GetAsync(HttpRequest request, RecommendationRepository repository,
        CancellationToken cancellationToken)
    {
        var query = request.Query;
        var mode = query.TryGetValue("mode", out var modeValue) ? modeValue.ToString() : "collections";
        if (mode is not ("collections" or "pages"))
            return Results.BadRequest(new { error = "Mode must be collections or pages." });

        var limit = 20;
        if (query.TryGetValue("limit", out var limitValue) &&
            (!int.TryParse(limitValue.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit is < 1 or > 50))
            return Results.BadRequest(new { error = "Limit must be from 1 to 50." });

        long? contextCardId = null;
        if (query.TryGetValue("contextCardId", out var contextValue))
        {
            if (!long.TryParse(contextValue.ToString(), NumberStyles.None, CultureInfo.InvariantCulture,
                    out var parsedId) || parsedId <= 0)
                return Results.BadRequest(new { error = "Context card ID must be positive." });
            contextCardId = parsedId;
        }

        var items = await repository.GetAsync(mode, limit, contextCardId, cancellationToken);
        return items is null
            ? Results.NotFound(new { error = "Context card not found." })
            : Results.Ok(new { items, mode });
    }
}

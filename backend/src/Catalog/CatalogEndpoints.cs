namespace MyApp.Api.Catalog;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var tags = app.MapGroup("/api/tags");
        tags.MapGet("", async (CatalogRepository repository, HttpResponse response, string? search, string? sort,
            string? category, int? limit, int? offset, string? include_total) =>
        {
            if (sort is not (null or "name" or "usage"))
                return Results.BadRequest(new { error = "sort must be name or usage." });
            if (category is not (null or "general" or "artist" or "character" or "copyright" or "meta" or "custom"))
                return Results.BadRequest(new { error = "category must be general, artist, character, copyright, meta or custom." });
            if (Paging.Validate(limit, offset) is { } pagingError) return Results.BadRequest(new { error = pagingError });
            var (items, total) = await repository.ListTagsPageAsync(search, sort == "usage", category,
                limit ?? 200, offset ?? 0, Paging.WantsTotal(include_total));
            if (total is long count) Paging.SetTotal(response, count);
            return Results.Ok(items);
        });
        tags.MapGet("/{id:long}", async (long id, CatalogRepository repository) =>
            await repository.GetTagAsync(id) is { } tag ? Results.Ok(tag) : Results.NotFound());
        tags.MapPost("", async (TagRequest request, CatalogRepository repository) =>
            await RunAsync(async () =>
            {
                var tag = await repository.CreateTagAsync(request);
                return Results.Created($"/api/tags/{tag.Id}", tag);
            }));
        tags.MapPut("/{id:long}", async (long id, TagRequest request, CatalogRepository repository) =>
            await RunAsync(async () =>
                await repository.UpdateTagAsync(id, request) is { } tag ? Results.Ok(tag) : Results.NotFound()));
        tags.MapDelete("/{id:long}", async (long id, CatalogRepository repository) =>
            await RunAsync(async () =>
                await repository.DeleteTagAsync(id) ? Results.NoContent() : Results.NotFound()));

        var sources = app.MapGroup("/api/sources");
        sources.MapGet("", async (CatalogRepository repository, HttpResponse response, string? search,
            int? limit, int? offset, string? include_total) =>
        {
            if (Paging.Validate(limit, offset) is { } pagingError) return Results.BadRequest(new { error = pagingError });
            var (items, total) = await repository.ListSourcesPageAsync(search, limit ?? 200, offset ?? 0,
                Paging.WantsTotal(include_total));
            if (total is long count) Paging.SetTotal(response, count);
            return Results.Ok(items);
        });
        sources.MapGet("/{id:long}", async (long id, CatalogRepository repository) =>
            await repository.GetSourceAsync(id) is { } source ? Results.Ok(source) : Results.NotFound());
        sources.MapPost("", async (SourceRequest request, CatalogRepository repository) =>
            await RunAsync(async () =>
            {
                var source = await repository.CreateSourceAsync(request);
                return Results.Created($"/api/sources/{source.Id}", source);
            }));
        sources.MapPut("/{id:long}", async (long id, SourceRequest request, CatalogRepository repository) =>
            await RunAsync(async () =>
                await repository.UpdateSourceAsync(id, request) is { } source ? Results.Ok(source) : Results.NotFound()));
        sources.MapDelete("/{id:long}", async (long id, CatalogRepository repository) =>
            await RunAsync(async () =>
                await repository.DeleteSourceAsync(id) ? Results.NoContent() : Results.NotFound()));

        var cardTags = app.MapGroup("/api/cards/{cardId:long}/tags");
        cardTags.MapGet("", async (long cardId, CardCatalogRepository repository) =>
            await RunAsync(async () => Results.Ok(await repository.ListTagsAsync(cardId))));
        cardTags.MapPut("/{tagId:long}", async (long cardId, long tagId, CardCatalogRepository repository) =>
            await RunAsync(async () =>
            {
                await repository.SetTagAsync(cardId, tagId);
                return Results.NoContent();
            }));
        cardTags.MapDelete("/{tagId:long}", async (long cardId, long tagId, CardCatalogRepository repository) =>
            await RunAsync(async () =>
                await repository.DeleteTagAsync(cardId, tagId) ? Results.NoContent() : Results.NotFound()));

        var suggestions = app.MapGroup("/api/cards/{cardId:long}/tag-suggestions");
        suggestions.MapGet("", async (long cardId, CardCatalogRepository repository) =>
            await RunAsync(async () => Results.Ok(await repository.ListPendingSuggestionsAsync(cardId))));
        suggestions.MapPut("/{tagId:long}/decision", async (
            long cardId, long tagId, TagSuggestionDecisionRequest request, CardCatalogRepository repository) =>
            await RunAsync(async () =>
                await repository.DecideSuggestionAsync(cardId, tagId, request.Decision ?? "")
                    ? Results.NoContent() : Results.NotFound()));
        suggestions.MapPut("/{tagId:long}/correction", async (
            long cardId, long tagId, TagSuggestionCorrectionRequest request, CardCatalogRepository repository) =>
            await RunAsync(async () =>
            {
                await repository.CorrectSuggestionAsync(cardId, tagId, request.ReplacementTagId);
                return Results.NoContent();
            }));
        suggestions.MapPut("/decision", async (
            long cardId, TagSuggestionBatchDecisionRequest request, CardCatalogRepository repository) =>
            await RunAsync(async () => Results.Ok(new
            {
                count = await repository.DecideSuggestionsAsync(cardId, request.TagIds, request.Decision ?? "")
            })));

        var cardSources = app.MapGroup("/api/cards/{cardId:long}/sources");
        cardSources.MapGet("", async (long cardId, CardCatalogRepository repository) =>
            await RunAsync(async () => Results.Ok(await repository.ListSourcesAsync(cardId))));
        cardSources.MapPut("/{sourceId:long}", async (
            long cardId, long sourceId, CardSourceRequest request, CardCatalogRepository repository) =>
            await RunAsync(async () =>
            {
                await repository.SetSourceAsync(cardId, sourceId, request);
                return Results.NoContent();
            }));
        cardSources.MapDelete("/{sourceId:long}", async (
            long cardId, long sourceId, CardCatalogRepository repository) =>
            await RunAsync(async () =>
                await repository.DeleteSourceAsync(cardId, sourceId) ? Results.NoContent() : Results.NotFound()));
        return app;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (CatalogValidationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (CatalogConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        catch (CatalogMissingException exception)
        {
            return Results.NotFound(new { error = exception.Message });
        }
    }
}

namespace MyApp.Api.Catalog;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var tags = app.MapGroup("/api/tags");
        tags.MapGet("", (CatalogRepository repository, string? search) => repository.ListTagsAsync(search));
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
        sources.MapGet("", (CatalogRepository repository, string? search) => repository.ListSourcesAsync(search));
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
    }
}

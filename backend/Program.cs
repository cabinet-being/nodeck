using MySqlConnector;
using MyApp.Api.Cards;
using MyApp.Api.Catalog;
using MyApp.Api.Decks;
using MyApp.Api.Migrations;
using MyApp.Api.Storage;

var importingDanbooru = args.Length > 0 && args[0] == "import-danbooru-tags";
if (importingDanbooru && args.Contains("--help"))
{
    Console.WriteLine("Usage: import-danbooru-tags [--page-size 1..1000] [--max-pages N] [--refresh]");
    Console.WriteLine("Set NODECK_DANBOORU_USER_AGENT to a unique contactable User-Agent before running.");
    return;
}

DanbooruImportOptions? importOptions = null;
if (importingDanbooru)
{
    var pageSize = 1000;
    int? maxPages = null;
    var refresh = false;
    for (var index = 1; index < args.Length; index++)
    {
        switch (args[index])
        {
            case "--page-size" when index + 1 < args.Length &&
                int.TryParse(args[index + 1], out var parsedPageSize) && parsedPageSize is >= 1 and <= 1000:
                pageSize = parsedPageSize;
                index++;
                break;
            case "--max-pages" when index + 1 < args.Length &&
                int.TryParse(args[index + 1], out var parsedMaxPages) && parsedMaxPages > 0:
                maxPages = parsedMaxPages;
                index++;
                break;
            case "--refresh":
                refresh = true;
                break;
            default:
                throw new ArgumentException($"Invalid import option: {args[index]}. Use --help for usage.");
        }
    }
    importOptions = new DanbooruImportOptions
    {
        UserAgent = Environment.GetEnvironmentVariable("NODECK_DANBOORU_USER_AGENT") ?? "",
        PageSize = pageSize,
        MaxPages = maxPages,
        Refresh = refresh,
        OnPageCompleted = progress => Console.WriteLine(
            $"{progress.Kind}: page {progress.Page}, cursor {progress.CursorBeforeId}, " +
            $"processed {progress.Processed}, conflicts {progress.Conflicts}, complete {progress.Completed}")
    };
    if (string.IsNullOrWhiteSpace(importOptions.UserAgent))
        throw new ArgumentException("Set NODECK_DANBOORU_USER_AGENT before importing Danbooru tags.");
}

var builder = WebApplication.CreateBuilder(importingDanbooru ? [] : args);

builder.Services.AddScoped<CardRepository>();
builder.Services.AddScoped<DeckRepository>();
builder.Services.AddScoped<CatalogRepository>();
builder.Services.AddScoped<CardCatalogRepository>();
builder.Services.AddSingleton<CardDatabaseInitializer>();
builder.Services.AddSingleton<CatalogDatabaseInitializer>();
builder.Services.AddSingleton<DeckDatabaseInitializer>();
builder.Services.AddSingleton<SchemaMigrationRunner>();
builder.Services.AddSingleton<PreviewService>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<SchemaMigrationRunner>().InitializeAsync();
}

if (importOptions is not null)
{
    var connectionString = app.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Missing database connection string.");
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    var result = await new DanbooruTagImporter(connectionString, httpClient).RunAsync(importOptions);
    Console.WriteLine($"Danbooru import: {result.Pages} pages, {result.Processed} entries, " +
        $"{result.Conflicts} conflicts; tags complete: {result.TagsCompleted}; " +
        $"aliases complete: {result.AliasesCompleted}.");
    return;
}

app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/health/mysql", async (IConfiguration configuration) =>
{
    var connectionString = configuration.GetConnectionString("Default");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("Missing database connection string.");
    }

    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();

    return Results.Ok(new { status = "ok" });
});

app.MapCardEndpoints();
app.MapCardRelationEndpoints();
app.MapDeckEndpoints();
app.MapFavoriteEndpoints();
app.MapCatalogEndpoints();

app.Run();

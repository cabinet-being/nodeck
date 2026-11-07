using MySqlConnector;
using MyApp.Api.Analysis;
using MyApp.Api.Cards;
using MyApp.Api.Catalog;
using MyApp.Api.Decks;
using MyApp.Api.Interest;
using MyApp.Api.Migrations;
using MyApp.Api.Storage;

var importingDanbooru = args.Length > 0 && args[0] == "import-danbooru-tags";
var analyzingCard = args.Length > 0 && args[0] == "analyze-card";
if (importingDanbooru && args.Contains("--help"))
{
    Console.WriteLine("Usage: import-danbooru-tags [--page-size 1..1000] [--max-pages N] [--refresh]");
    Console.WriteLine("Set NODECK_DANBOORU_USER_AGENT to a unique contactable User-Agent before running.");
    return;
}
if (analyzingCard && args.Contains("--help"))
{
    Console.WriteLine("Usage: analyze-card <card-id>");
    Console.WriteLine("Set NODECK_TAGGER_MODEL_DIR to the directory containing model.onnx and selected_tags.csv.");
    return;
}

long? analysisCardId = null;
if (analyzingCard)
{
    if (args.Length != 2 || !long.TryParse(args[1], out var parsedCardId) || parsedCardId <= 0)
        throw new ArgumentException("Usage: analyze-card <positive-card-id>");
    analysisCardId = parsedCardId;
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

var builder = WebApplication.CreateBuilder(importingDanbooru || analyzingCard ? [] : args);

builder.Services.AddScoped<CardRepository>();
builder.Services.AddScoped<DeckRepository>();
builder.Services.AddScoped<CatalogRepository>();
builder.Services.AddScoped<CardCatalogRepository>();
builder.Services.AddScoped<InterestRepository>();
builder.Services.AddSingleton<AnalysisQueueRepository>();
builder.Services.AddSingleton<SpeechAnalysisQueueRepository>();
if (!importingDanbooru && !analyzingCard)
{
    builder.Services.AddHostedService<ImageAnalysisWorker>();
    builder.Services.AddHostedService<SpeechAnalysisWorker>();
}
builder.Services.AddSingleton<CardDatabaseInitializer>();
builder.Services.AddSingleton<CatalogDatabaseInitializer>();
builder.Services.AddSingleton<DeckDatabaseInitializer>();
builder.Services.AddSingleton<InterestDatabaseInitializer>();
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
if (analysisCardId is long cardId)
{
    var appDataRoot = app.Configuration["NODECK_APP_DATA"]
        ?? Path.Combine(AppContext.BaseDirectory, "data");
    var modelDirectory = app.Configuration["NODECK_TAGGER_MODEL_DIR"]
        ?? Path.Combine(appDataRoot, "models", "wd-vit-tagger-v3");
    using var tagger = new WdViTTagger(
        Path.Combine(modelDirectory, "model.onnx"),
        Path.Combine(modelDirectory, "selected_tags.csv"));
    using var scope = app.Services.CreateScope();
    var analysis = new CardImageAnalysisService(app.Configuration,
        scope.ServiceProvider.GetRequiredService<CardCatalogRepository>(), tagger);
    var result = await analysis.AnalyzeAsync(cardId);
    Console.WriteLine($"Card {result.CardId}: {result.Predictions} predictions, " +
        $"{result.MatchedTags} catalog matches, {result.SubmittedSuggestions} suggestions submitted.");
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
app.MapInterestEndpoints();
app.MapCatalogEndpoints();

app.Run();

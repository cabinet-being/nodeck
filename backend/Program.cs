using MyApp.Api;
using MySqlConnector;
using MyApp.Api.Analysis;
using MyApp.Api.Analysis.LearnedTags;
using MyApp.Api.Cards;
using MyApp.Api.Catalog;
using MyApp.Api.Decks;
using MyApp.Api.Duplicates;
using MyApp.Api.Interest;
using MyApp.Api.Migrations;
using MyApp.Api.Recommendations;
using MyApp.Api.Storage;

var importingDanbooru = args.Length > 0 && args[0] == "import-danbooru-tags";
var analyzingCard = args.Length > 0 && args[0] == "analyze-card";
var seedingWdTags = args.Length > 0 && args[0] == "seed-wd-tags";
if (seedingWdTags && args.Contains("--help"))
{
    Console.WriteLine("Usage: seed-wd-tags");
    Console.WriteLine("Set NODECK_TAGGER_MODEL_DIR to the directory containing selected_tags.csv.");
    return;
}
if (seedingWdTags && args.Length != 1)
    throw new ArgumentException("Usage: seed-wd-tags");
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

var builder = WebApplication.CreateBuilder(importingDanbooru || analyzingCard || seedingWdTags ? [] : args);

// A single compose file (such as the ZimaOS import) gives the app the same MYSQL_* variables as the
// MySQL service instead of a connection string, so the password is written only once per service.
if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Default")) &&
    builder.Configuration["MYSQL_HOST"] is { Length: > 0 } mysqlHost)
{
    builder.Configuration["ConnectionStrings:Default"] = new MySqlConnectionStringBuilder
    {
        Server = mysqlHost,
        Port = uint.TryParse(builder.Configuration["MYSQL_PORT"], out var mysqlPort) ? mysqlPort : 3306,
        Database = builder.Configuration["MYSQL_DATABASE"] is { Length: > 0 } database ? database : "nodeck",
        UserID = builder.Configuration["MYSQL_USER"] is { Length: > 0 } user ? user : "nodeck",
        Password = builder.Configuration["MYSQL_PASSWORD"] ?? "",
    }.ConnectionString;
}

// Refuse to run with the placeholder password shipped in .env.example.
if (builder.Configuration.GetConnectionString("Default") is { } defaultConnection &&
    new MySqlConnectionStringBuilder(defaultConnection).Password == "change-me")
    throw new InvalidOperationException(
        "MYSQL_PASSWORD is still the placeholder \"change-me\". Set real passwords in .env " +
        "(for example: tr -dc A-Za-z0-9 </dev/urandom | head -c 24) and recreate the stack.");

builder.AddUploadLimits();
builder.Services.AddScoped<CardRepository>();
builder.Services.AddScoped<DeckRepository>();
builder.Services.AddScoped<CatalogRepository>();
builder.Services.AddScoped<CardCatalogRepository>();
builder.Services.AddScoped<InterestRepository>();
builder.Services.AddScoped<RecommendationRepository>();
builder.Services.Configure<RecommendationOptions>(builder.Configuration.GetSection(RecommendationOptions.SectionName));
builder.Services.AddSingleton<AnalysisQueueRepository>();
builder.Services.AddSingleton<SpeechAnalysisQueueRepository>();
builder.Services.AddSingleton<LearnedTagRepository>();
if (!importingDanbooru && !analyzingCard && !seedingWdTags)
{
    builder.Services.AddHostedService<ModelDownloadService>();
    builder.Services.AddHostedService<ImageAnalysisWorker>();
    builder.Services.AddHostedService<SpeechAnalysisWorker>();
    builder.Services.AddHostedService<LearnedTagWorker>();
    builder.Services.AddHostedService<ContentHashBackfillService>();
    builder.Services.AddHostedService<PerceptualHashBackfillService>();
    builder.Services.AddHostedService<MyApp.Api.Catalog.SystemMetaTagBackfillService>();
    builder.Services.AddHostedService<OrphanScanService>();
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
            .AllowAnyMethod()
            .WithExposedHeaders(Paging.TotalHeader);
    });
});

var app = builder.Build();

// MySQL may still be starting (for example on the first start of a fresh install): wait for it
// instead of crashing and relying on the container restart policy.
if (app.Configuration.GetConnectionString("Default") is { Length: > 0 } startupConnection)
{
    var deadline = DateTime.UtcNow.AddMinutes(2);
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var probe = new MySqlConnection(startupConnection);
            await probe.OpenAsync();
            break;
        }
        catch (MySqlException exception) when (DateTime.UtcNow < deadline &&
            exception.ErrorCode is MySqlErrorCode.UnableToConnectToHost or MySqlErrorCode.UnknownDatabase)
        {
            if (attempt == 1) app.Logger.LogInformation("Waiting for MySQL to accept connections...");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<SchemaMigrationRunner>().InitializeAsync();
    var activated = await scope.ServiceProvider.GetRequiredService<CardCatalogRepository>().ActivatePendingSuggestionsAsync();
    if (activated > 0) app.Logger.LogInformation("Activated {Count} pending automatic tags.", activated);
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
if (seedingWdTags)
{
    var appDataRoot = app.Configuration["NODECK_APP_DATA"]
        ?? Path.Combine(AppContext.BaseDirectory, "data");
    var modelDirectory = app.Configuration["NODECK_TAGGER_MODEL_DIR"]
        ?? Path.Combine(appDataRoot, "models", "wd-vit-tagger-v3");
    var connectionString = app.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Missing database connection string.");
    var result = await new WdVocabularySeeder(connectionString).RunAsync(
        Path.Combine(modelDirectory, "selected_tags.csv"));
    Console.WriteLine($"WD vocabulary: {result.Eligible} eligible, {result.Inserted} inserted, " +
        $"{result.Existing} existing, {result.Conflicts} conflicts.");
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

var appVersion = Environment.GetEnvironmentVariable("NODECK_VERSION") is { Length: > 0 } configuredVersion
    ? configuredVersion
    : "dev";
var webRoot = app.Environment.WebRootPath;
var indexFile = webRoot is null ? null : Path.Combine(webRoot, "index.html");
var serveFrontend = indexFile is not null && File.Exists(indexFile);

if (serveFrontend)
{
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = context =>
        {
            var headers = context.Context.Response.Headers;
            headers.CacheControl = context.Context.Request.Path.StartsWithSegments("/assets")
                ? "public, max-age=31536000, immutable"
                : "no-cache";
        }
    });
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", version = appVersion }));

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
app.MapRecommendationEndpoints();
app.MapCatalogEndpoints();
app.MapDuplicateEndpoints();
app.MapGet("/api/learned-tags", async (LearnedTagRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetStatusAsync(cancellationToken)));

if (serveFrontend)
{
    // SPA fallback: deep links such as /tags?tab=artist load the app on refresh.
    app.MapFallback(async context =>
    {
        var path = context.Request.Path;
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) ||
            path.StartsWithSegments("/api") || path.StartsWithSegments("/health") ||
            Path.HasExtension(path.Value))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(indexFile!);
    });
}

app.Run();

using MySqlConnector;
using MyApp.Api.Cards;
using MyApp.Api.Decks;
using MyApp.Api.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<CardRepository>();
builder.Services.AddScoped<DeckRepository>();
builder.Services.AddSingleton<CardDatabaseInitializer>();
builder.Services.AddSingleton<DeckDatabaseInitializer>();
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
    await scope.ServiceProvider.GetRequiredService<CardDatabaseInitializer>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<DeckDatabaseInitializer>().InitializeAsync();
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
app.MapDeckEndpoints();
app.MapFavoriteEndpoints();

app.Run();

using MySqlConnector;

namespace MyApp.Api.Catalog;

/// <summary>Materialises system meta tags for the whole library at startup.</summary>
public sealed class SystemMetaTagBackfillService : BackgroundService
{
    private readonly string _connectionString;
    private readonly ILogger<SystemMetaTagBackfillService> _logger;

    public SystemMetaTagBackfillService(IConfiguration configuration, ILogger<SystemMetaTagBackfillService> logger)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            var (cards, skipped) = await RunOnceAsync(stoppingToken);
            _logger.LogInformation("System meta tag sync finished: {Cards} cards processed, {Skipped} names skipped.",
                cards, skipped.Count);
            foreach (var name in skipped)
                _logger.LogWarning("System meta tag '{Name}' was skipped: a tag with that name already exists.", name);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "System meta tag sync failed.");
        }
    }

    public async Task<(int Cards, IReadOnlyList<string> Skipped)> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await SystemMetaTags.SyncLibraryAsync(connection, cancellationToken);
    }
}

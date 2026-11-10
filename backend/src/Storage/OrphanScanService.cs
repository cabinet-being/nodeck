using Dapper;
using MySqlConnector;

namespace MyApp.Api.Storage;

/// <summary>Runs once at startup and only logs storage directories that have no card row.</summary>
public sealed class OrphanScanService : BackgroundService
{
    private const int MaxLoggedPaths = 100;
    private readonly string _connectionString;
    private readonly string _appDataRoot;
    private readonly ILogger<OrphanScanService> _logger;
    private readonly bool _enabled;

    public OrphanScanService(IConfiguration configuration, ILogger<OrphanScanService> logger)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
        _appDataRoot = configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _logger = logger;
        _enabled = !string.Equals(configuration["Storage:OrphanScanOnStartup"], "false", StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        await Task.Yield();
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(stoppingToken);
            var ids = (await connection.QueryAsync<long>(new CommandDefinition(
                "SELECT id FROM cards;", cancellationToken: stoppingToken))).ToHashSet();
            var orphans = OrphanStorageScanner.FindOrphans(_appDataRoot, ids, TimeSpan.FromHours(1));
            if (orphans.Count == 0) return;
            _logger.LogWarning("Found {Count} storage directories without a card row (older than 1 hour); nothing was deleted.",
                orphans.Count);
            foreach (var path in orphans.Take(MaxLoggedPaths))
                _logger.LogWarning("Orphan storage directory: {Path}", path);
            if (orphans.Count > MaxLoggedPaths)
                _logger.LogWarning("{Remaining} more orphan directories not listed.", orphans.Count - MaxLoggedPaths);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Orphan storage scan failed.");
        }
    }
}

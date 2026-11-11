using Dapper;
using MySqlConnector;

namespace MyApp.Api.Analysis.LearnedTags;

/// <summary>
/// Queues the low-priority feature backfill, retrains custom-tag models when examples change, and publishes
/// learned proposals for cards not yet scored by the current models. A database lock fences other servers.
/// </summary>
public sealed class LearnedTagWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinRetrainInterval = TimeSpan.FromMinutes(10);
    private const string LockName = "nodeck:learned-tags";
    private readonly IConfiguration _configuration;
    private readonly LearnedTagRepository _repository;
    private readonly ILogger<LearnedTagWorker> _logger;

    public LearnedTagWorker(IConfiguration configuration, LearnedTagRepository repository,
        ILogger<LearnedTagWorker> logger)
    {
        _configuration = configuration;
        _repository = repository;
        _logger = logger;
    }

    private bool Setting(string name) => !string.Equals(
        _configuration[name] ?? Environment.GetEnvironmentVariable(name), "false", StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Setting("NODECK_LEARNED_TAGS"))
        {
            _logger.LogInformation("Learned custom tag suggestions are turned off.");
            return;
        }
        var featureModelKey = WdViTTagger.PinnedModelKey;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new MySqlConnection(_configuration.GetConnectionString("Default")
                    ?? throw new InvalidOperationException("Missing database connection string."));
                await connection.OpenAsync(stoppingToken);
                if (await connection.ExecuteScalarAsync<int?>("SELECT GET_LOCK(@LockName, 0);",
                        new { LockName }) == 1)
                {
                    try
                    {
                        await RunOnceAsync(featureModelKey, stoppingToken);
                    }
                    finally
                    {
                        await connection.ExecuteScalarAsync<int?>("SELECT RELEASE_LOCK(@LockName);",
                            new { LockName });
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Learned tag worker will retry after a delay.");
            }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task RunOnceAsync(string featureModelKey, CancellationToken cancellationToken)
    {
        if (Setting("NODECK_LEARNED_TAGS_BACKFILL"))
        {
            var queued = await _repository.EnqueueBackfillAsync(featureModelKey, cancellationToken: cancellationToken);
            if (queued > 0)
                _logger.LogInformation("Queued {Count} media cards for low-priority visual feature backfill.", queued);
        }

        var state = await _repository.ReadStateAsync(cancellationToken);
        var fingerprint = await _repository.ComputeFingerprintAsync(featureModelKey, cancellationToken);
        if (state is null || state.FeatureModelKey != featureModelKey ||
            (state.Fingerprint != fingerprint && DateTime.UtcNow - state.TrainedAtUtc >= MinRetrainInterval))
        {
            var summary = await _repository.TrainAsync(featureModelKey, cancellationToken);
            _logger.LogInformation(
                "Trained learned tag generation {Generation}: {Tags} tags from {Cards} cards, {Enabled} proposing.",
                summary.Generation, summary.TagsTrained, summary.FeatureCards, summary.TagsEnabled);
        }

        var scored = 0;
        int batch;
        while ((batch = await _repository.ScoreAsync(featureModelKey, cancellationToken: cancellationToken)) > 0)
            scored += batch;
        if (scored > 0)
            _logger.LogInformation("Scored {Count} cards with learned custom tag models.", scored);
    }
}

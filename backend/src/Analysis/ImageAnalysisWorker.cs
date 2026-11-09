using MyApp.Api.Catalog;

namespace MyApp.Api.Analysis;

/// <summary>Processes one media card at a time while a database advisory lock fences other servers.</summary>
public sealed class ImageAnalysisWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AnalysisBudget = TimeSpan.FromMinutes(4);
    private readonly IConfiguration _configuration;
    private readonly AnalysisQueueRepository _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImageAnalysisWorker> _logger;
    private readonly IVideoFrameSampler _sampler = new FfmpegVideoFrameSampler();
    private IVisualTagger? _tagger;

    public ImageAnalysisWorker(IConfiguration configuration, AnalysisQueueRepository queue,
        IServiceScopeFactory scopeFactory, ILogger<ImageAnalysisWorker> logger)
    {
        _configuration = configuration;
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!ModelFilesExist() || !await _queue.IsVocabularyReadyAsync(stoppingToken))
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                await using var lockConnection = await _queue.OpenLockConnectionAsync(stoppingToken);
                if (!await AnalysisQueueRepository.TryAcquireInferenceLockAsync(lockConnection, stoppingToken))
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }
                var hadLease = false;
                try
                {
                    // A present but unusable model is a readiness failure, not a card attempt.
                    _tagger ??= CreateTagger();
                    var lease = await _queue.ClaimAsync(stoppingToken);
                    if (lease is not null)
                    {
                        hadLease = true;
                        _logger.LogInformation("Analyzing media card {CardId}, attempt {Attempt}.",
                            lease.CardId, lease.Attempts);
                        try
                        {
                            using var analysisBudget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            analysisBudget.CancelAfter(AnalysisBudget);
                            using var scope = _scopeFactory.CreateScope();
                            var catalog = scope.ServiceProvider.GetRequiredService<CardCatalogRepository>();
                            var analysis = new CardImageAnalysisService(_configuration, catalog, _tagger, _sampler);
                            var prepared = await analysis.PrepareAsync(lease.CardId, analysisBudget.Token);
                            if (await _queue.CompleteAsync(lease, prepared, stoppingToken))
                                _logger.LogInformation("Analyzed media card {CardId} ({Count} suggestions).",
                                    lease.CardId, prepared.Result.SubmittedSuggestions);
                            else
                                _logger.LogInformation("Discarded stale media analysis for card {CardId}.", lease.CardId);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception error)
                        {
                            _logger.LogWarning(error, "Media analysis failed for card {CardId}, attempt {Attempt}.",
                                lease.CardId, lease.Attempts);
                            await _queue.FailAsync(lease, error, stoppingToken);
                        }
                    }
                }
                finally
                {
                    await AnalysisQueueRepository.ReleaseInferenceLockAsync(lockConnection);
                }
                if (!hadLease) await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Image analysis worker will retry after a delay.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private bool ModelFilesExist()
    {
        var dataRoot = _configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        var modelDirectory = _configuration["NODECK_TAGGER_MODEL_DIR"]
            ?? Environment.GetEnvironmentVariable("NODECK_TAGGER_MODEL_DIR")
            ?? Path.Combine(dataRoot, "models", "wd-vit-tagger-v3");
        return File.Exists(Path.Combine(modelDirectory, "model.onnx")) &&
            File.Exists(Path.Combine(modelDirectory, "selected_tags.csv"));
    }

    private IVisualTagger CreateTagger()
    {
        var dataRoot = _configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        var modelDirectory = _configuration["NODECK_TAGGER_MODEL_DIR"]
            ?? Environment.GetEnvironmentVariable("NODECK_TAGGER_MODEL_DIR")
            ?? Path.Combine(dataRoot, "models", "wd-vit-tagger-v3");
        return new WdViTTagger(Path.Combine(modelDirectory, "model.onnx"),
            Path.Combine(modelDirectory, "selected_tags.csv"));
    }

    public override void Dispose()
    {
        _tagger?.Dispose();
        base.Dispose();
    }
}

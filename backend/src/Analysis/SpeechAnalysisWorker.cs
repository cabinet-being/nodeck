namespace MyApp.Api.Analysis;

/// <summary>Processes speech jobs separately from visual tagging with renewable leases.</summary>
public sealed class SpeechAnalysisWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AnalysisBudget = TimeSpan.FromMinutes(22);
    private readonly IConfiguration _configuration;
    private readonly SpeechAnalysisQueueRepository _queue;
    private readonly AnalysisQueueRepository _inferenceLock;
    private readonly ILogger<SpeechAnalysisWorker> _logger;
    private WhisperCppSpeechRecognizer? _recognizer;

    public SpeechAnalysisWorker(IConfiguration configuration, SpeechAnalysisQueueRepository queue,
        AnalysisQueueRepository inferenceLock, ILogger<SpeechAnalysisWorker> logger)
    {
        _configuration = configuration;
        _queue = queue;
        _inferenceLock = inferenceLock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!ModelFilesExist())
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }
                // Model checksum or runtime failures are readiness failures, not card attempts.
                _recognizer ??= new WhisperCppSpeechRecognizer(_configuration);
                await _recognizer.EnsureReadyAsync(stoppingToken);

                // Image tagging takes seconds per card and is what an import waits for; a speech job
                // can hold the shared inference lock for many minutes, so it gives way to them.
                if (_inferenceLock.ImageWorkerReady && await _inferenceLock.HasReadyRegularJobsAsync(stoppingToken))
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                await using var lockConnection = await _inferenceLock.OpenLockConnectionAsync(stoppingToken);
                if (!await AnalysisQueueRepository.TryAcquireInferenceLockAsync(lockConnection, stoppingToken))
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }
                var hadLease = false;
                try
                {
                    var lease = await _queue.ClaimAsync(stoppingToken);
                    if (lease is not null)
                    {
                        hadLease = true;
                        await ProcessAsync(lease, stoppingToken);
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
                _logger.LogWarning("Speech analysis worker will retry after {ErrorType}.",
                    error.GetType().Name);
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(SpeechAnalysisJobLease lease, CancellationToken stoppingToken)
    {
        using var analysisCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        analysisCts.CancelAfter(AnalysisBudget);
        var renewal = RenewLeaseAsync(lease, analysisCts);
        try
        {
            var service = new CardSpeechAnalysisService(_configuration, _recognizer!);
            var prepared = await service.PrepareAsync(lease.CardId, analysisCts.Token);
            analysisCts.Token.ThrowIfCancellationRequested();
            if (await _queue.CompleteAsync(lease, prepared, analysisCts.Token))
                _logger.LogInformation("Analyzed speech for card {CardId}.", lease.CardId);
            else
                _logger.LogInformation("Discarded stale speech analysis for card {CardId}.", lease.CardId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogWarning("Speech analysis failed for card {CardId}, attempt {Attempt} ({ErrorType}).",
                lease.CardId, lease.Attempts, error.GetType().Name);
            await _queue.FailAsync(lease, error, stoppingToken);
        }
        finally
        {
            analysisCts.Cancel();
            await renewal;
        }
    }

    private async Task RenewLeaseAsync(SpeechAnalysisJobLease lease, CancellationTokenSource analysisCts)
    {
        try
        {
            while (!analysisCts.IsCancellationRequested)
            {
                await Task.Delay(RenewInterval, analysisCts.Token);
                if (!await _queue.RenewAsync(lease, analysisCts.Token))
                {
                    analysisCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (analysisCts.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogWarning("Speech lease renewal failed for card {CardId} ({ErrorType}).",
                lease.CardId, error.GetType().Name);
            analysisCts.Cancel();
        }
    }

    private bool ModelFilesExist()
    {
        var dataRoot = _configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        var modelDirectory = _configuration["NODECK_WHISPER_MODEL_DIR"]
            ?? Environment.GetEnvironmentVariable("NODECK_WHISPER_MODEL_DIR")
            ?? Path.Combine(dataRoot, "models", "whisper");
        return File.Exists(Path.Combine(modelDirectory, "ggml-base.bin")) &&
            File.Exists(Path.Combine(modelDirectory, "ggml-silero-v6.2.0.bin"));
    }
}

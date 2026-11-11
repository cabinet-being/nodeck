using System.Security.Cryptography;

namespace MyApp.Api.Analysis;

/// <summary>
/// Downloads the pinned ML model files on first start so no manual install step is needed.
/// Existing files are trusted and never re-hashed.
/// </summary>
public sealed class ModelDownloadService : BackgroundService
{
    internal sealed record ModelFile(string Url, string Path, string Sha256);

    private const string WdBase = "https://huggingface.co/SmilingWolf/wd-vit-tagger-v3/resolve/7f6b584d0bd3f55c4531f14ba3d4761b2bccdc0f";
    private const string WhisperBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1";
    private const string VadBase = "https://huggingface.co/ggml-org/whisper-vad/resolve/9ffd54a1e1ee413ddf265af9913beaf518d1639b";

    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);

    private readonly IConfiguration? _configuration;
    private readonly ILogger<ModelDownloadService> _logger;
    private readonly HttpClient _http;
    private readonly IReadOnlyList<ModelFile>? _files;
    private readonly bool _ownsClient;
    private readonly TimeSpan _startDelay = StartDelay;
    private readonly TimeSpan _minRetry = MinRetryDelay;

    public ModelDownloadService(IConfiguration configuration, ILogger<ModelDownloadService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var version = Environment.GetEnvironmentVariable("NODECK_VERSION");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"nodeck/{(string.IsNullOrWhiteSpace(version) ? "dev" : version.Trim())}");
        _ownsClient = true;
    }

    internal ModelDownloadService(HttpClient http, IReadOnlyList<ModelFile> files, ILogger<ModelDownloadService> logger,
        TimeSpan? startDelay = null, TimeSpan? minRetryDelay = null)
    {
        _http = http;
        _files = files;
        _logger = logger;
        _startDelay = startDelay ?? TimeSpan.Zero;
        _minRetry = minRetryDelay ?? MinRetryDelay;
    }

    public override void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_files is null && !AutoDownloadEnabled())
            {
                _logger.LogInformation("Model auto-download is disabled (NODECK_MODEL_AUTO_DOWNLOAD).");
                return;
            }

            await Task.Delay(_startDelay, stoppingToken);
            var delay = _minRetry;
            while (!stoppingToken.IsCancellationRequested)
            {
                bool done;
                try
                {
                    done = await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Model download pass failed.");
                    done = false;
                }

                if (done)
                {
                    return;
                }

                _logger.LogWarning("Model download incomplete; retrying in {Delay}.", delay);
                await Task.Delay(delay, stoppingToken);
                delay = delay * 2 > MaxRetryDelay ? MaxRetryDelay : delay * 2;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var allPresent = true;
        foreach (var file in _files ?? ResolveDefaultFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(file.Path)!;
            Directory.CreateDirectory(directory);
            var temporary = file.Path + ".download";
            if (File.Exists(temporary))
            {
                TryDelete(temporary);
            }

            if (File.Exists(file.Path))
            {
                continue;
            }

            try
            {
                if (!await DownloadAsync(file, temporary, cancellationToken))
                {
                    allPresent = false;
                }
            }
            catch (OperationCanceledException)
            {
                TryDelete(temporary);
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException)
            {
                _logger.LogWarning(exception, "Failed to download {Name}.", Path.GetFileName(file.Path));
                TryDelete(temporary);
                allPresent = false;
            }
        }

        return allPresent;
    }

    private async Task<bool> DownloadAsync(ModelFile file, string temporary, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(file.Path);
        using var response = await _http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        _logger.LogInformation("Downloading model file {Name}{Size}.", name,
            total is { } length ? $" ({ToMegabytes(length)} MB)" : string.Empty);

        long written = 0;
        var nextPercent = 10;
        using (var hash = SHA256.Create())
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.TransformBlock(buffer, 0, read, null, 0);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    if (total is > 0)
                    {
                        var percent = (int)(written * 100 / total.Value);
                        if (percent >= nextPercent && percent < 100)
                        {
                            _logger.LogInformation("{Name}: {Percent}% ({Done}/{Total} MB)", name, percent,
                                ToMegabytes(written), ToMegabytes(total.Value));
                            nextPercent = percent / 10 * 10 + 10;
                        }
                    }
                }
            }

            hash.TransformFinalBlock([], 0, 0);
            var actual = Convert.ToHexString(hash.Hash!).ToLowerInvariant();
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Checksum mismatch for {Name}: expected {Expected}, got {Actual}. Discarding download.",
                    name, file.Sha256, actual);
                TryDelete(temporary);
                return false;
            }
        }

        File.Move(temporary, file.Path);
        _logger.LogInformation("Downloaded model file {Name} ({Size} MB).", name, ToMegabytes(written));
        return true;
    }

    private bool AutoDownloadEnabled()
    {
        var value = _configuration?["NODECK_MODEL_AUTO_DOWNLOAD"]
            ?? Environment.GetEnvironmentVariable("NODECK_MODEL_AUTO_DOWNLOAD");
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        value = value.Trim();
        return !(value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0");
    }

    private List<ModelFile> ResolveDefaultFiles()
    {
        var dataRoot = _configuration?["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        var wdDirectory = _configuration?["NODECK_TAGGER_MODEL_DIR"]
            ?? Environment.GetEnvironmentVariable("NODECK_TAGGER_MODEL_DIR")
            ?? Path.Combine(dataRoot, "models", "wd-vit-tagger-v3");
        var whisperDirectory = _configuration?["NODECK_WHISPER_MODEL_DIR"]
            ?? Environment.GetEnvironmentVariable("NODECK_WHISPER_MODEL_DIR")
            ?? Path.Combine(dataRoot, "models", "whisper");
        return
        [
            new($"{WdBase}/selected_tags.csv", Path.Combine(wdDirectory, "selected_tags.csv"),
                "298633d94d0031d2081c0893f29c82eab7f0df00b08483ba8f29d1e979441217"),
            new($"{WdBase}/model.onnx", Path.Combine(wdDirectory, "model.onnx"),
                "35f23693620b668f4d53fd3c62bf65e40af739bc52c7eb0fbc49258b58d065b6"),
            new($"{WhisperBase}/ggml-base.bin", Path.Combine(whisperDirectory, "ggml-base.bin"),
                "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
            new($"{VadBase}/ggml-silero-v6.2.0.bin", Path.Combine(whisperDirectory, "ggml-silero-v6.2.0.bin"),
                "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987"),
        ];
    }

    private static long ToMegabytes(long bytes) => bytes / (1024 * 1024);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

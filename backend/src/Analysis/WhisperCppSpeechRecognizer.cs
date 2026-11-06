using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyApp.Api.Analysis;

/// <summary>Runs the pinned whisper.cpp CLI on a bounded extract of original video audio.</summary>
public sealed class WhisperCppSpeechRecognizer : ISpeechRecognizer
{
    public const string PinnedModelKey = "whisper.cpp-v1.9.5:ggml-base+silero-v6.2.0";
    private const string BaseHash = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";
    private const string VadHash = "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987";
    private const double MaximumSeconds = 600;
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RecognitionTimeout = TimeSpan.FromMinutes(20);
    private readonly string _baseModelPath;
    private readonly string _vadModelPath;
    private readonly string _cliPath;
    private readonly SemaphoreSlim _verificationGate = new(1, 1);
    private volatile bool _modelsVerified;

    public WhisperCppSpeechRecognizer(IConfiguration configuration)
    {
        var appDataRoot = configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        var modelDirectory = configuration["NODECK_WHISPER_MODEL_DIR"]
            ?? Environment.GetEnvironmentVariable("NODECK_WHISPER_MODEL_DIR")
            ?? Path.Combine(appDataRoot, "models", "whisper");
        _baseModelPath = Path.Combine(modelDirectory, "ggml-base.bin");
        _vadModelPath = Path.Combine(modelDirectory, "ggml-silero-v6.2.0.bin");
        _cliPath = configuration["NODECK_WHISPER_CLI"]
            ?? Environment.GetEnvironmentVariable("NODECK_WHISPER_CLI")
            ?? "whisper-cli";
    }

    public async Task<SpeechRecognitionResult> RecognizeAsync(
        string originalPath, double durationSeconds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(originalPath)) throw new ArgumentException("A video path is required.", nameof(originalPath));
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureReadyAsync(cancellationToken);

        var spanSeconds = Math.Min(durationSeconds, MaximumSeconds);
        var workDirectory = Path.Combine(Path.GetTempPath(), "nodeck-whisper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var wavePath = Path.Combine(workDirectory, "audio.wav");
        var outputPrefix = Path.Combine(workDirectory, "result");
        var jsonPath = outputPrefix + ".json";
        try
        {
            await RunAsync("ffmpeg", [
                "-nostdin", "-v", "error", "-y", "-protocol_whitelist", "file",
                "-i", originalPath, "-map", "0:a:0", "-vn", "-sn", "-dn",
                "-t", spanSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wavePath,
            ], ExtractionTimeout, "FFmpeg audio extraction", cancellationToken);
            var processedSeconds = ReadWaveDuration(wavePath, (decimal)spanSeconds);
            if (processedSeconds <= 0)
                throw new InvalidOperationException("FFmpeg produced no audio for speech recognition.");

            await RunAsync(_cliPath, [
                "--vad", "--vad-model", _vadModelPath, "-m", _baseModelPath,
                "-l", "auto", "-oj", "-of", outputPrefix, "-f", wavePath,
                "--no-gpu", "-t", "2",
            ], RecognitionTimeout, "Speech recognition", cancellationToken);

            if (!File.Exists(jsonPath) || new FileInfo(jsonPath).Length > 8_000_000)
                throw new InvalidOperationException("Speech recognition produced invalid JSON output.");
            var json = await File.ReadAllTextAsync(jsonPath, cancellationToken);
            return ParseJson(json, processedSeconds, durationSeconds > MaximumSeconds);
        }
        finally
        {
            try { Directory.Delete(workDirectory, recursive: true); }
            catch (IOException) { /* A failed cleanup must not replace the recognition error. */ }
            catch (UnauthorizedAccessException) { /* Temporary files remain subject to OS cleanup. */ }
        }
    }

    /// <summary>Checks both pinned models and the configured CLI once before work is claimed.</summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_modelsVerified) return;
        await _verificationGate.WaitAsync(cancellationToken);
        try
        {
            if (_modelsVerified) return;
            await VerifyModelAsync(_baseModelPath, BaseHash, cancellationToken);
            await VerifyModelAsync(_vadModelPath, VadHash, cancellationToken);
            await RunAsync(_cliPath, ["--version"], TimeSpan.FromSeconds(10),
                "Speech recognition CLI readiness check", cancellationToken);
            _modelsVerified = true;
        }
        finally { _verificationGate.Release(); }
    }

    private static async Task VerifyModelAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("A required speech recognition model is missing.");
        await using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A speech recognition model failed SHA-256 verification.");
    }

    private static decimal ReadWaveDuration(string path, decimal maximumSeconds)
    {
        if (!File.Exists(path)) return 0;
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 12 || new string(reader.ReadChars(4)) != "RIFF")
            throw new InvalidOperationException("FFmpeg produced invalid WAV audio.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
            throw new InvalidOperationException("FFmpeg produced invalid WAV audio.");
        var validFormat = false;
        long dataBytes = 0;
        while (stream.Position <= stream.Length - 8)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadUInt32();
            var chunkEnd = stream.Position + chunkSize;
            if (chunkEnd > stream.Length)
                throw new InvalidOperationException("FFmpeg produced invalid WAV audio.");
            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                    throw new InvalidOperationException("FFmpeg produced invalid WAV audio.");
                var format = reader.ReadUInt16();
                var channels = reader.ReadUInt16();
                var sampleRate = reader.ReadUInt32();
                reader.ReadUInt32(); // byte rate
                reader.ReadUInt16(); // block alignment
                var bitsPerSample = reader.ReadUInt16();
                validFormat = format == 1 && channels == 1 && sampleRate == 16000 && bitsPerSample == 16;
            }
            else if (chunkId == "data") dataBytes = chunkSize;
            stream.Position = chunkEnd + (chunkSize & 1);
        }
        if (!validFormat) throw new InvalidOperationException("FFmpeg produced unexpected WAV audio format.");
        return Math.Min(maximumSeconds, dataBytes / 32000m);
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, string operation, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        Process started;
        try
        {
            started = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start {operation}.");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException($"Unable to start {operation}.");
        }
        using var process = started;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        // CLI output may contain recognized words; discard both streams without logging them.
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{operation} failed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException($"{operation} exceeded its time limit.");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Parses v1.9.5 segment offsets; transcript text is never included in failures.</summary>
    public static SpeechRecognitionResult ParseJson(string json, decimal processedSeconds, bool truncated)
    {
        if (processedSeconds is < 0 or > 600) throw new ArgumentOutOfRangeException(nameof(processedSeconds));
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("transcription", out var transcription) ||
                transcription.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Speech recognition produced invalid JSON output.");
            string? language = null;
            if (document.RootElement.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("language", out var languageElement) &&
                languageElement.ValueKind == JsonValueKind.String)
            {
                var value = languageElement.GetString()?.Trim().ToLowerInvariant();
                if (value is { Length: >= 2 and <= 3 } && value.All(char.IsAsciiLetterLower))
                    language = value;
            }
            var segments = new List<SpeechSegment>();
            foreach (var item in transcription.EnumerateArray())
            {
                if (segments.Count >= 1000) break;
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("offsets", out var offsets) || offsets.ValueKind != JsonValueKind.Object ||
                    !offsets.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.Number ||
                    !from.TryGetInt64(out var startMs) ||
                    !offsets.TryGetProperty("to", out var to) || to.ValueKind != JsonValueKind.Number ||
                    !to.TryGetInt64(out var endMs)) continue;
                var text = textElement.GetString()?.Trim() ?? "";
                if (!IsMeaningful(text) || startMs < 0 || endMs <= startMs || startMs >= processedSeconds * 1000) continue;
                if (text.Length > 500) text = text[..500];
                var startSeconds = startMs / 1000m;
                var endSeconds = Math.Min(endMs / 1000m, processedSeconds);
                if (endSeconds <= startSeconds) continue;
                segments.Add(new SpeechSegment(startSeconds, endSeconds, text));
            }
            if (segments.Count == 0) language = null;
            return new SpeechRecognitionResult(
                segments.Count > 0 ? true : null,
                language is null ? null : language == "en",
                language, processedSeconds, truncated, PinnedModelKey, segments);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Speech recognition produced invalid JSON output.");
        }
    }

    private static bool IsMeaningful(string text)
    {
        if (text.Length == 0 || !text.Any(char.IsLetter)) return false;
        if ((text.StartsWith('[') && text.EndsWith(']')) ||
            (text.StartsWith('(') && text.EndsWith(')')) ||
            text.Contains('♪') || text.Contains('♫')) return false;
        return true;
    }
}

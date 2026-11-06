using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace MyApp.Api.Analysis;

public sealed record SampledVideoFrame(string Path, double TimestampSeconds, int SegmentIndex);

/// <summary>Owns the extracted frames until the caller finishes using them.</summary>
public sealed class SampledVideoFrames : IAsyncDisposable
{
    private readonly string? _directory;
    private bool _disposed;

    public SampledVideoFrames(IReadOnlyList<SampledVideoFrame> frames)
    {
        Frames = frames ?? throw new ArgumentNullException(nameof(frames));
    }

    internal SampledVideoFrames(string directory, IReadOnlyList<SampledVideoFrame> frames)
    {
        _directory = directory;
        Frames = frames;
    }

    public IReadOnlyList<SampledVideoFrame> Frames { get; }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_directory is not null && Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}

public interface IVideoFrameSampler
{
    Task<SampledVideoFrames> SampleAsync(string originalPath, double durationSeconds, CancellationToken ct);
}

/// <summary>Extracts bounded, local PNG samples with FFmpeg for visual analysis.</summary>
public sealed class FfmpegVideoFrameSampler : IVideoFrameSampler
{
    private static readonly TimeSpan PerFrameTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(4);

    public async Task<SampledVideoFrames> SampleAsync(
        string originalPath, double durationSeconds, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        if (!System.IO.Path.IsPathFullyQualified(originalPath) || !File.Exists(originalPath))
            throw new FileNotFoundException("The original video must be an existing local file.", originalPath);

        ct.ThrowIfCancellationRequested();
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "nodeck-video-frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        totalCts.CancelAfter(TotalTimeout);
        var frames = new List<SampledVideoFrame>();
        var pathsByHash = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            foreach (var (timestamp, segmentIndex) in BuildSampleTimes(durationSeconds))
            {
                totalCts.Token.ThrowIfCancellationRequested();
                var candidatePath = System.IO.Path.Combine(directory, $"frame-{frames.Count:D2}-{segmentIndex:D2}.png");
                bool extracted;
                var extractedAt = timestamp;
                try
                {
                    extracted = await ExtractAsync(originalPath, candidatePath, timestamp, totalCts.Token);
                    if (!extracted && timestamp > 0)
                    {
                        // Sparse frame rates may have no frame after a seek near the file's end.
                        // Try the preceding whole-second frame without changing its segment.
                        File.Delete(candidatePath);
                        extractedAt = Math.Floor(timestamp);
                        if (extractedAt == timestamp)
                            extractedAt = Math.Max(0, timestamp - 1);
                        extracted = await ExtractAsync(originalPath, candidatePath, extractedAt,
                            totalCts.Token);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && totalCts.IsCancellationRequested)
                {
                    throw new TimeoutException("Video frame sampling exceeded its total time limit.");
                }

                if (!extracted)
                {
                    File.Delete(candidatePath);
                    continue;
                }

                await using var stream = File.OpenRead(candidatePath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, totalCts.Token));
                if (pathsByHash.TryGetValue(hash, out var existingPath))
                {
                    await stream.DisposeAsync();
                    File.Delete(candidatePath);
                    frames.Add(new SampledVideoFrame(existingPath, extractedAt, segmentIndex));
                }
                else
                {
                    pathsByHash.Add(hash, candidatePath);
                    frames.Add(new SampledVideoFrame(candidatePath, extractedAt, segmentIndex));
                }
            }

            if (frames.Count == 0)
                throw new InvalidOperationException("FFmpeg could not extract a video frame from the local file.");

            return new SampledVideoFrames(directory, frames.AsReadOnly());
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    /// <summary>Returns up to three distinct millisecond timestamps in each of ten segments.</summary>
    public static IReadOnlyList<(double TimestampSeconds, int SegmentIndex)> BuildSampleTimes(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 1)
            return [(0, 0)];

        const int segmentCount = 10;
        var offset = Math.Min(3, durationSeconds * 0.01);
        var seenMilliseconds = new HashSet<long>();
        var times = new List<(double TimestampSeconds, int SegmentIndex)>(30);
        for (var segment = 0; segment < segmentCount; segment++)
        {
            var center = durationSeconds * (segment + 0.5) / segmentCount;
            foreach (var candidate in new[] { center - offset, center, center + offset })
            {
                var milliseconds = (long)Math.Round(
                    Math.Clamp(candidate, 0, durationSeconds) * 1000,
                    MidpointRounding.AwayFromZero);
                if (seenMilliseconds.Add(milliseconds))
                    times.Add((milliseconds / 1000.0, segment));
            }
        }

        return times;
    }

    private static async Task<bool> ExtractAsync(
        string originalPath, string outputPath, double timestamp, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "-nostdin", "-v", "error", "-y",
            "-ss", timestamp.ToString("0.###", CultureInfo.InvariantCulture),
            "-protocol_whitelist", "file", "-i", originalPath,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-frames:v", "1", "-vf", "scale=512:512:force_original_aspect_ratio=decrease",
            "-threads", "1", outputPath,
        }) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start FFmpeg.");
        using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        frameCts.CancelAfter(PerFrameTimeout);
        var errorTask = process.StandardError.ReadToEndAsync(frameCts.Token);
        try
        {
            await process.WaitForExitAsync(frameCts.Token);
            await errorTask;
            return process.ExitCode == 0 && File.Exists(outputPath);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            return false;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MyApp.Api.Analysis;

public sealed record SampledVideoFrame(string Path, double TimestampSeconds, int SegmentIndex,
    bool IsSceneCandidate = false);

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
    private static readonly TimeSpan SceneProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScenePhaseTimeout = TimeSpan.FromSeconds(15);
    private const int ThumbnailSide = 32;
    private const int ThumbnailBytes = ThumbnailSide * ThumbnailSide * 3;
    private const double SignificantGapDifference = 0.12;
    private const double DistinctCandidateDifference = 0.08;

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

            // Scene probes are best effort: baseline frames remain usable if image decoding,
            // FFmpeg, or this phase's short deadline fails.
            using var sceneCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
            sceneCts.CancelAfter(ScenePhaseTimeout);
            try
            {
                await AddSceneCandidatesAsync(originalPath, durationSeconds, directory, frames,
                    pathsByHash, sceneCts.Token);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The optional scene phase must not discard successfully extracted baseline frames.
            }

            ct.ThrowIfCancellationRequested();
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

    /// <summary>Mean absolute RGB difference, normalized to the range 0..1.</summary>
    public static double ScoreVisualDifference(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        if (first.Length != ThumbnailBytes || second.Length != ThumbnailBytes)
            throw new ArgumentException("Both thumbnails must contain 32x32 RGB pixels.");

        long difference = 0;
        for (var i = 0; i < ThumbnailBytes; i++)
            difference += Math.Abs(first[i] - second[i]);
        return difference / (double)(ThumbnailBytes * 255);
    }

    /// <summary>Ranks significant cross-segment gaps and selects at most one midpoint per segment.</summary>
    public static IReadOnlyList<(double TimestampSeconds, int SegmentIndex)> RankSceneProbeTimes(
        IReadOnlyList<(double TimestampSeconds, int SegmentIndex, byte[] Thumbnail)> baseline,
        double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 || baseline.Count < 2)
            return [];

        var ordered = baseline.OrderBy(frame => frame.TimestampSeconds).ToArray();
        var existingTimes = ordered.Select(frame => (long)Math.Round(frame.TimestampSeconds * 1000,
            MidpointRounding.AwayFromZero)).ToHashSet();
        var gaps = new List<(double Time, int Segment, double Difference)>();
        for (var i = 1; i < ordered.Length; i++)
        {
            var left = ordered[i - 1];
            var right = ordered[i];
            if (left.SegmentIndex == right.SegmentIndex || left.TimestampSeconds >= right.TimestampSeconds)
                continue;

            var difference = ScoreVisualDifference(left.Thumbnail, right.Thumbnail);
            if (difference < SignificantGapDifference)
                continue;

            var milliseconds = (long)Math.Round((left.TimestampSeconds + right.TimestampSeconds) * 500,
                MidpointRounding.AwayFromZero);
            var time = milliseconds / 1000.0;
            if (time <= left.TimestampSeconds || time >= right.TimestampSeconds || existingTimes.Contains(milliseconds))
                continue;

            var segment = Math.Clamp((int)Math.Floor(10 * time / durationSeconds), 0, 9);
            gaps.Add((time, segment, difference));
        }

        var usedSegments = new HashSet<int>();
        return gaps.OrderByDescending(gap => gap.Difference).ThenBy(gap => gap.Time)
            .Where(gap => usedSegments.Add(gap.Segment))
            .Take(10)
            .Select(gap => (gap.Time, gap.Segment))
            .ToArray();
    }

    private static async Task AddSceneCandidatesAsync(string originalPath, double durationSeconds,
        string directory, List<SampledVideoFrame> frames, Dictionary<string, string> pathsByHash,
        CancellationToken ct)
    {
        var baseline = new List<(SampledVideoFrame Frame, byte[] Thumbnail)>(frames.Count);
        var thumbnailsByPath = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            ct.ThrowIfCancellationRequested();
            if (!thumbnailsByPath.TryGetValue(frame.Path, out var thumbnail))
            {
                thumbnail = ReadThumbnail(frame.Path);
                thumbnailsByPath.Add(frame.Path, thumbnail);
            }
            baseline.Add((frame, thumbnail));
        }

        var ordered = baseline.OrderBy(item => item.Frame.TimestampSeconds).ToArray();
        var probes = RankSceneProbeTimes(ordered.Select(item =>
            (item.Frame.TimestampSeconds, item.Frame.SegmentIndex, item.Thumbnail)).ToArray(), durationSeconds);
        var existingTimes = frames.Select(frame => (long)Math.Round(frame.TimestampSeconds * 1000,
            MidpointRounding.AwayFromZero)).ToHashSet();
        foreach (var (time, segment) in probes)
        {
            ct.ThrowIfCancellationRequested();
            var candidatePath = System.IO.Path.Combine(directory, $"scene-{segment:D2}.png");
            try
            {
                if (!await ExtractAsync(originalPath, candidatePath, time, ct, SceneProbeTimeout))
                    continue;

                await using var stream = File.OpenRead(candidatePath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                await stream.DisposeAsync();
                if (pathsByHash.ContainsKey(hash) || existingTimes.Contains((long)Math.Round(time * 1000)))
                    continue;

                var thumbnail = ReadThumbnail(candidatePath);
                var rightIndex = Array.FindIndex(ordered, item => item.Frame.TimestampSeconds > time);
                if (rightIndex <= 0 ||
                    ScoreVisualDifference(thumbnail, ordered[rightIndex - 1].Thumbnail) < DistinctCandidateDifference ||
                    ScoreVisualDifference(thumbnail, ordered[rightIndex].Thumbnail) < DistinctCandidateDifference)
                    continue;

                pathsByHash.Add(hash, candidatePath);
                existingTimes.Add((long)Math.Round(time * 1000));
                frames.Add(new SampledVideoFrame(candidatePath, time, segment, IsSceneCandidate: true));
            }
            finally
            {
                if (!frames.Any(frame => frame.Path == candidatePath))
                    File.Delete(candidatePath);
            }
        }
    }

    private static byte[] ReadThumbnail(string path)
    {
        using var image = Image.Load<Rgb24>(path);
        image.Mutate(context => context.Resize(ThumbnailSide, ThumbnailSide));
        var bytes = new byte[ThumbnailBytes];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < ThumbnailSide; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < ThumbnailSide; x++)
                {
                    var pixel = row[x];
                    var index = (y * ThumbnailSide + x) * 3;
                    bytes[index] = pixel.R;
                    bytes[index + 1] = pixel.G;
                    bytes[index + 2] = pixel.B;
                }
            }
        });
        return bytes;
    }

    private static async Task<bool> ExtractAsync(
        string originalPath, string outputPath, double timestamp, CancellationToken ct,
        TimeSpan? timeout = null)
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
        frameCts.CancelAfter(timeout ?? PerFrameTimeout);
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

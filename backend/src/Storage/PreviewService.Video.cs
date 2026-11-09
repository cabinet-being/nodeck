using System.Diagnostics;
using System.Globalization;
using MyApp.Api.Cards;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static MyApp.Api.Storage.MediaProcess;
using static MyApp.Api.Storage.StorageDirectories;
using static MyApp.Api.Storage.VideoProbe;

namespace MyApp.Api.Storage;

public sealed partial class PreviewService
{
    private async Task<CardFileAssets> SaveVideoAndCreatePreviewAsync(long cardId, IFormFile video)
    {
        if (video.Length <= 0)
        {
            throw new InvalidOperationException("Uploaded video is empty.");
        }

        var originalFileName = SafeUploadName(video.FileName);
        var cardDirectory = Path.Combine(_appDataRoot, "cards", cardId.ToString());
        var previewDirectory = Path.Combine(_appDataRoot, "cache", "cards", cardId.ToString());
        DeleteDirectoryIfExists(cardDirectory);
        DeleteDirectoryIfExists(previewDirectory);
        Directory.CreateDirectory(cardDirectory);
        Directory.CreateDirectory(previewDirectory);

        try
        {
            return await CreateVideoAssetsAsync(
                cardId,
                video,
                cardDirectory,
                previewDirectory,
                originalFileName);
        }
        catch
        {
            DeleteCardFiles(cardId);
            throw;
        }
    }

    private async Task<CardFileAssets> PrepareVideoReplacementAsync(
        long cardId,
        IFormFile video,
        string stagingCardDirectory,
        string stagingPreviewDirectory)
    {
        if (video.Length <= 0)
        {
            throw new InvalidOperationException("Uploaded video is empty.");
        }

        return await CreateVideoAssetsAsync(
            cardId,
            video,
            stagingCardDirectory,
            stagingPreviewDirectory,
            SafeUploadName(video.FileName));
    }

    private async Task<CardFileAssets> CreateVideoAssetsAsync(
        long cardId,
        IFormFile video,
        string cardDirectory,
        string previewDirectory,
        string originalFileName)
    {
        var temporaryUploadPath = Path.Combine(cardDirectory, "upload.video");

        await SaveUploadedFileAsync(video, temporaryUploadPath);

        var probe = await ProbeVideoAsync(temporaryUploadPath);
        var finalExtension = probe.MimeType switch
        {
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "video/webm" => ".webm",
            _ => throw new InvalidOperationException("Unsupported video format. Only MP4, MOV, and WebM are supported."),
        };

        var relativeContentPath = BuildContentPath(cardId, finalExtension);
        var relativePreviewPath = BuildPreviewPath(cardId, ".gif");
        var originalPath = Path.Combine(cardDirectory, $"original{finalExtension}");
        var previewPath = Path.Combine(previewDirectory, "preview.gif");

        if (!string.Equals(temporaryUploadPath, originalPath, StringComparison.Ordinal))
        {
            File.Move(temporaryUploadPath, originalPath, overwrite: true);
        }

        var preview = await GenerateAnimatedAssetsAsync(originalPath, previewPath,
            Path.Combine(previewDirectory, "poster.webp"), probe.Duration);

        string? playbackPath = null;
        if (!IsBrowserCompatible(probe))
        {
            var derivativePath = Path.Combine(cardDirectory, "playback.mp4");
            var transcode = await RunProcessAsync("ffmpeg", [
                "-y", "-i", originalPath,
                "-map", "0:v:0", "-map", "0:a:0?",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "veryfast", "-crf", "23", "-threads", "2",
                "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", derivativePath,
            ], TranscodingTimeout);
            if (transcode.ExitCode != 0 || !File.Exists(derivativePath) || new FileInfo(derivativePath).Length == 0)
                throw new InvalidOperationException("Failed to create a browser-compatible playback video.");
            playbackPath = Path.Combine("cards", cardId.ToString(), "playback.mp4").Replace('\\', '/');
        }

        var fileInfo = new FileInfo(originalPath);

        return new CardFileAssets(
            relativeContentPath,
            preview.Animated ? relativePreviewPath : BuildPosterPath(cardId),
            "video",
            probe.MimeType,
            originalFileName,
            fileInfo.Length,
            probe.Width,
            probe.Height,
            probe.Duration,
            HasAudio: probe.HasAudio,
            Container: probe.Container,
            VideoCodec: probe.VideoCodec,
            AudioCodec: probe.AudioCodec,
            PlaybackPath: playbackPath,
            AudioChannels: probe.AudioChannels,
            AudioSampleRate: probe.AudioSampleRate,
            PosterPath: BuildPosterPath(cardId),
            PreviewStartSeconds: preview.Start,
            PreviewSelection: preview.Selection,
            PreviewScore: Math.Max(0, preview.Score),
            FrameRate: probe.FrameRate);
    }

    internal sealed record AnimatedPreviewResult(double Start, double Score, string Selection, bool Animated);

    private async Task<AnimatedPreviewResult> GenerateAnimatedAssetsAsync(string originalPath, string previewPath,
        string posterPath, double duration)
    {
        const int candidateCount = 10;
        var window = duration > 0 ? Math.Min(Math.Max(duration, 0.1), 3.0) : 3.0;
        var latestStart = Math.Max(0, duration - window);
        var candidates = new List<(double Start, double Score)>();
        var samplePath = Path.Combine(Path.GetDirectoryName(previewPath)!, "sample.png");
        var clock = Stopwatch.StartNew();

        try
        {
            var indices = latestStart > 0
                ? new[] { 0, 9, 4, 2, 7, 1, 6, 3, 8, 5 }
                : new[] { 0 };
            foreach (var index in indices)
            {
                var scanTimeLeft = TimeSpan.FromSeconds(25) - clock.Elapsed;
                if (scanTimeLeft <= TimeSpan.Zero) break;
                var start = latestStart * index / (candidateCount - 1);
                var sampleTime = start + (duration > 0 ? window / 2 : 0);
                try
                {
                    var sample = await RunProcessAsync("ffmpeg", [
                        "-v", "error", "-y", "-ss", FormatSeconds(sampleTime), "-i", originalPath,
                        "-an", "-frames:v", "1", "-vf", "scale=96:96:force_original_aspect_ratio=decrease",
                        "-threads", "1", samplePath,
                    ], TimeSpan.FromTicks(Math.Min(scanTimeLeft.Ticks, TimeSpan.FromSeconds(4).Ticks)));
                    if (sample.ExitCode != 0 || !File.Exists(samplePath)) continue;
                    using var image = await Image.LoadAsync<Rgb24>(samplePath);
                    candidates.Add((start, PreviewSelection.Score(image)));
                }
                catch (InvalidOperationException) { }
                catch (UnknownImageFormatException) { }
                catch (InvalidImageContentException) { }
                finally { File.Delete(samplePath); }
            }

            if (candidates.Count == 0) candidates.Add((0, 0));
            else if (candidates.All(candidate => candidate.Start != 0)) candidates.Add((0, -1));
            var ordered = candidates.OrderByDescending(item => item.Score).ThenBy(item => item.Start).Take(2).ToList();
            if (ordered.All(candidate => candidate.Start != 0)) ordered.Add((0, -1));
            foreach (var candidate in ordered)
            {
                try
                {
                    if (TimeSpan.FromSeconds(90) - clock.Elapsed <= TimeSpan.Zero) break;
                    await RenderAnimatedAssetsAtStartAsync(originalPath, previewPath, posterPath,
                        candidate.Start, duration, clock);
                    return new AnimatedPreviewResult(candidate.Start, candidate.Score,
                        candidate.Score >= _previewQualityThreshold ? "auto" : "fallback", true);
                }
                catch (InvalidOperationException)
                {
                    File.Delete(previewPath);
                    File.Delete(posterPath);
                }
            }

            // Animation failed everywhere: keep the upload with a poster-only preview at the best start.
            try
            {
                await RenderPosterAsync(originalPath, posterPath, ordered[0].Start, duration, Stopwatch.StartNew());
                return new AnimatedPreviewResult(ordered[0].Start, ordered[0].Score, "poster_only", false);
            }
            catch (InvalidOperationException)
            {
                File.Delete(posterPath);
            }

            throw new InvalidOperationException("Failed to generate animated preview and poster.");
        }
        finally { File.Delete(samplePath); }
    }

    private async Task RenderAnimatedAssetsAtStartAsync(string originalPath, string previewPath,
        string posterPath, double start, double duration, Stopwatch clock)
    {
        var window = duration > 0 ? Math.Min(Math.Max(duration, 0.1), 3.0) : 3.0;
        await RenderAnimatedPreviewAsync(originalPath, previewPath, start, window, clock);
        await RenderPosterAsync(originalPath, posterPath, start, duration, clock);
    }

    private async Task RenderPosterAsync(string originalPath, string posterPath, double start,
        double duration, Stopwatch clock)
    {
        var window = duration > 0 ? Math.Min(Math.Max(duration, 0.1), 3.0) : 3.0;
        var posterTime = start + (duration > 0 ? window / 2 : 0);
        var poster = await RunProcessAsync("ffmpeg", [
            "-v", "error", "-y", "-ss", FormatSeconds(posterTime), "-i", originalPath,
            "-an", "-frames:v", "1", "-vf", $"scale={MaxPreviewDimension}:{MaxPreviewDimension}:force_original_aspect_ratio=decrease",
            "-c:v", "libwebp", "-quality", PreviewQuality.ToString(CultureInfo.InvariantCulture),
            "-threads", "1", posterPath,
        ], TimeSpan.FromTicks(Math.Max(1, Math.Min(
            (TimeSpan.FromSeconds(90) - clock.Elapsed).Ticks, TimeSpan.FromSeconds(20).Ticks))));
        if (poster.ExitCode != 0 || !File.Exists(posterPath) || new FileInfo(posterPath).Length == 0)
            throw new InvalidOperationException("Failed to generate preview poster.");
    }

    private async Task RenderAnimatedPreviewAsync(string originalPath, string previewPath,
        double start, double duration, Stopwatch clock)
    {
        if (AnimatedRenderOverride is { } renderOverride)
        {
            await renderOverride(originalPath, previewPath, start);
            return;
        }
        var palettePath = Path.Combine(Path.GetDirectoryName(previewPath)!, "palette.png");
        var scaleFilter = $"fps=10,scale={MaxPreviewDimension}:{MaxPreviewDimension}:force_original_aspect_ratio=decrease:flags=lanczos";
        var inputArguments = new[] { "-ss", FormatSeconds(start), "-i", originalPath };
        try
        {
            var palette = await RunProcessAsync("ffmpeg", [
                "-v", "error", "-y", .. inputArguments, "-t", FormatSeconds(duration), "-an",
                "-vf", $"{scaleFilter},palettegen", "-threads", "2", palettePath,
            ], TimeSpan.FromTicks(Math.Max(1, Math.Min(
                (TimeSpan.FromSeconds(90) - clock.Elapsed).Ticks, TimeSpan.FromSeconds(25).Ticks))));
            if (palette.ExitCode != 0 || !File.Exists(palettePath))
                throw new InvalidOperationException("Failed to generate preview palette.");

            var preview = await RunProcessAsync("ffmpeg", [
                "-v", "error", "-y", .. inputArguments, "-i", palettePath, "-t", FormatSeconds(duration), "-an", "-filter_complex",
                $"{scaleFilter}[x];[x][1:v]paletteuse", "-threads", "2", "-loop", "0", previewPath,
            ], TimeSpan.FromTicks(Math.Max(1, Math.Min(
                (TimeSpan.FromSeconds(90) - clock.Elapsed).Ticks, TimeSpan.FromSeconds(25).Ticks))));
            if (preview.ExitCode != 0 || !File.Exists(previewPath) || new FileInfo(previewPath).Length == 0)
                throw new InvalidOperationException("Failed to generate animated preview.");
        }
        finally { File.Delete(palettePath); }
    }
}

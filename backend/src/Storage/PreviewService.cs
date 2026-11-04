using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MyApp.Api.Cards;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MyApp.Api.Storage;

public sealed class PreviewService
{
    private const int MaxPreviewDimension = 512;
    private const int PreviewQuality = 75;
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TranscodingTimeout = TimeSpan.FromMinutes(10);

    private readonly string _appDataRoot;

    public PreviewService(IConfiguration configuration)
    {
        _appDataRoot = configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
    }

    public async Task<CardFileAssets> SaveMediaAndCreatePreviewAsync(long cardId, IFormFile media)
    {
        try
        {
            return await DetectImageExtensionAsync(media) switch
            {
                ".gif" => await SaveGifAndCreatePreviewAsync(cardId, media),
                not null => await SaveImageAndCreatePreviewAsync(cardId, media),
                _ => await SaveVideoAndCreatePreviewAsync(cardId, media),
            };
        }
        catch (Exception exception)
        {
            var prefix = $"File '{SafeUploadName(media.FileName)}':";
            throw new InvalidOperationException(exception.Message.StartsWith(prefix, StringComparison.Ordinal)
                ? exception.Message
                : $"{prefix} {exception.Message}", exception);
        }
    }

    public async Task<CardFileAssets> SaveImageAndCreatePreviewAsync(long cardId, IFormFile image)
    {
        if (image.Length <= 0)
        {
            throw new InvalidOperationException($"File '{SafeUploadName(image.FileName)}': uploaded image is empty.");
        }

        var originalFileName = SafeUploadName(image.FileName);
        var originalExtension = await DetectImageExtensionAsync(image)
            ?? throw new InvalidOperationException($"File '{originalFileName}': Unsupported image content. Only JPEG, PNG, and WebP are supported.");
        if (originalExtension == ".gif")
            throw new InvalidOperationException($"File '{originalFileName}': GIF is not supported in this image collection.");
        var cardDirectory = Path.Combine(_appDataRoot, "cards", cardId.ToString());
        var previewDirectory = Path.Combine(_appDataRoot, "cache", "cards", cardId.ToString());
        DeleteDirectoryIfExists(cardDirectory);
        DeleteDirectoryIfExists(previewDirectory);
        Directory.CreateDirectory(cardDirectory);
        Directory.CreateDirectory(previewDirectory);

        var relativeContentPath = BuildContentPath(cardId, originalExtension);
        var relativePreviewPath = BuildPreviewPath(cardId, ".webp");
        var originalPath = Path.Combine(_appDataRoot, relativeContentPath);
        var previewPath = Path.Combine(_appDataRoot, relativePreviewPath);

        await SaveUploadedFileAsync(image, originalPath);

        try
        {
            return await CreateImagePreviewAsync(
                image,
                originalPath,
                previewPath,
                relativeContentPath,
                relativePreviewPath,
                originalFileName);
        }
        catch (Exception exception)
        {
            DeleteCardFiles(cardId);
            throw new InvalidOperationException($"File '{originalFileName}': image could not be read or previewed.", exception);
        }
    }

    public async Task<PendingCardFileReplacement> PrepareMediaReplacementAsync(long cardId, IFormFile media)
    {
        var stagingRoot = Path.Combine(_appDataRoot, "tmp", "replacements", Guid.NewGuid().ToString("N"));
        var stagingCardDirectory = Path.Combine(stagingRoot, "cards", cardId.ToString());
        var stagingPreviewDirectory = Path.Combine(stagingRoot, "cache", "cards", cardId.ToString());

        Directory.CreateDirectory(stagingCardDirectory);
        Directory.CreateDirectory(stagingPreviewDirectory);

        try
        {
            CardFileAssets assets;

            var imageExtension = await DetectImageExtensionAsync(media);
            if (imageExtension == ".gif")
            {
                assets = await PrepareGifReplacementAsync(cardId, media, stagingCardDirectory, stagingPreviewDirectory);
            }
            else if (imageExtension is not null)
            {
                assets = await PrepareImageReplacementAsync(cardId, media, stagingCardDirectory, stagingPreviewDirectory);
            }
            else
            {
                assets = await PrepareVideoReplacementAsync(cardId, media, stagingCardDirectory, stagingPreviewDirectory);
            }

            return new PendingCardFileReplacement(
                _appDataRoot,
                stagingRoot,
                stagingCardDirectory,
                stagingPreviewDirectory,
                Path.Combine(_appDataRoot, "cards", cardId.ToString()),
                Path.Combine(_appDataRoot, "cache", "cards", cardId.ToString()),
                assets);
        }
        catch (Exception exception)
        {
            DeleteDirectoryIfExists(stagingRoot);
            throw new InvalidOperationException($"File '{SafeUploadName(media.FileName)}': {exception.Message}", exception);
        }
    }

    public async Task<PendingPreviewReplacement> PreparePreviewOverrideAsync(
        long cardId, string contentPath, double duration, double? startSeconds)
    {
        if (!double.IsFinite(duration) || duration < 0)
            throw new InvalidOperationException("Preview duration must be finite and nonnegative.");
        var latestStart = Math.Max(0, duration - Math.Min(3, duration));
        if (startSeconds is not null &&
            (!double.IsFinite(startSeconds.Value) || startSeconds.Value < 0 || startSeconds.Value > latestStart))
            throw new InvalidOperationException($"startSeconds must be between 0 and {latestStart}.");

        var originalPath = GetFullPath(contentPath);
        if (!File.Exists(originalPath))
            throw new InvalidOperationException("Original media file is missing.");

        var stagingRoot = Path.Combine(_appDataRoot, "tmp", "preview-overrides", Guid.NewGuid().ToString("N"));
        var stagingPreviewDirectory = Path.Combine(stagingRoot, "preview");
        Directory.CreateDirectory(stagingPreviewDirectory);
        var previewPath = Path.Combine(stagingPreviewDirectory, "preview.gif");
        var posterPath = Path.Combine(stagingPreviewDirectory, "poster.webp");

        try
        {
            double selectedStart;
            if (startSeconds is null)
            {
                selectedStart = await GenerateAnimatedAssetsAsync(originalPath, previewPath, posterPath, duration);
            }
            else
            {
                var clock = Stopwatch.StartNew();
                await RenderAnimatedAssetsAtStartAsync(originalPath, previewPath, posterPath,
                    startSeconds.Value, duration, clock);
                selectedStart = startSeconds.Value;
            }

            return new PendingPreviewReplacement(
                stagingRoot, stagingPreviewDirectory,
                Path.Combine(_appDataRoot, "cache", "cards", cardId.ToString()),
                Path.Combine(_appDataRoot, "tmp", "preview-override-backups", Guid.NewGuid().ToString("N")),
                selectedStart);
        }
        catch
        {
            DeleteDirectoryIfExists(stagingRoot);
            throw;
        }
    }

    public FileStream? OpenRead(string relativePath)
    {
        var fullPath = GetFullPath(relativePath);

        return File.Exists(fullPath) ? File.OpenRead(fullPath) : null;
    }

    public string GetContentType(string relativePath)
    {
        return Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            _ => "application/octet-stream",
        };
    }

    public void DeleteCardFiles(long cardId)
    {
        var cardDirectory = Path.Combine(_appDataRoot, "cards", cardId.ToString());
        var previewDirectory = Path.Combine(_appDataRoot, "cache", "cards", cardId.ToString());

        DeleteDirectoryIfExists(cardDirectory);
        DeleteDirectoryIfExists(previewDirectory);
    }

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

    private async Task<CardFileAssets> SaveGifAndCreatePreviewAsync(long cardId, IFormFile gif)
    {
        if (gif.Length <= 0)
        {
            throw new InvalidOperationException("Uploaded GIF is empty.");
        }

        var originalFileName = SafeUploadName(gif.FileName);
        var cardDirectory = Path.Combine(_appDataRoot, "cards", cardId.ToString());
        var previewDirectory = Path.Combine(_appDataRoot, "cache", "cards", cardId.ToString());
        DeleteDirectoryIfExists(cardDirectory);
        DeleteDirectoryIfExists(previewDirectory);
        Directory.CreateDirectory(cardDirectory);
        Directory.CreateDirectory(previewDirectory);

        try
        {
            return await CreateGifAssetsAsync(
                cardId,
                gif,
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

    private async Task<CardFileAssets> PrepareImageReplacementAsync(
        long cardId,
        IFormFile image,
        string stagingCardDirectory,
        string stagingPreviewDirectory)
    {
        if (image.Length <= 0)
        {
            throw new InvalidOperationException("Uploaded image is empty.");
        }

        var originalFileName = SafeUploadName(image.FileName);
        var originalExtension = await DetectImageExtensionAsync(image)
            ?? throw new InvalidOperationException("Unsupported image content. Only JPEG, PNG, and WebP are supported.");
        var relativeContentPath = BuildContentPath(cardId, originalExtension);
        var relativePreviewPath = BuildPreviewPath(cardId, ".webp");
        var originalPath = Path.Combine(stagingCardDirectory, $"original{originalExtension}");
        var previewPath = Path.Combine(stagingPreviewDirectory, "preview.webp");

        await SaveUploadedFileAsync(image, originalPath);

        return await CreateImagePreviewAsync(
            image,
            originalPath,
            previewPath,
            relativeContentPath,
            relativePreviewPath,
            originalFileName);
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

    private async Task<CardFileAssets> PrepareGifReplacementAsync(
        long cardId,
        IFormFile gif,
        string stagingCardDirectory,
        string stagingPreviewDirectory)
    {
        if (gif.Length <= 0)
        {
            throw new InvalidOperationException("Uploaded GIF is empty.");
        }

        return await CreateGifAssetsAsync(
            cardId,
            gif,
            stagingCardDirectory,
            stagingPreviewDirectory,
            SafeUploadName(gif.FileName));
    }

    private async Task<CardFileAssets> CreateImagePreviewAsync(
        IFormFile image,
        string originalPath,
        string previewPath,
        string relativeContentPath,
        string relativePreviewPath,
        string originalFileName)
    {
        using var loadedImage = await Image.LoadAsync(originalPath);
        var format = loadedImage.Metadata.DecodedImageFormat;
        var mimeType = format?.DefaultMimeType;
        if (mimeType is not ("image/jpeg" or "image/png" or "image/webp"))
            throw new InvalidOperationException("Unsupported image content. Only JPEG, PNG, and WebP are supported.");
        var width = loadedImage.Width;
        var height = loadedImage.Height;

        if (Math.Max(width, height) > MaxPreviewDimension)
        {
            loadedImage.Mutate(context => context.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(MaxPreviewDimension, MaxPreviewDimension),
            }));
        }

        await loadedImage.SaveAsWebpAsync(previewPath, new WebpEncoder
        {
            Quality = PreviewQuality,
        });

        var fileInfo = new FileInfo(originalPath);

        return new CardFileAssets(
            relativeContentPath,
            relativePreviewPath,
            "image",
            mimeType,
            originalFileName,
            fileInfo.Length,
            width,
            height);
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

        var previewStart = await GenerateAnimatedAssetsAsync(originalPath, previewPath,
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
            relativePreviewPath,
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
            PreviewStartSeconds: previewStart);
    }

    private async Task<CardFileAssets> CreateGifAssetsAsync(
        long cardId,
        IFormFile gif,
        string cardDirectory,
        string previewDirectory,
        string originalFileName)
    {
        var temporaryUploadPath = Path.Combine(cardDirectory, "upload.gif");

        await SaveUploadedFileAsync(gif, temporaryUploadPath);

        var metadata = await ReadGifMetadataAsync(temporaryUploadPath);
        var relativeContentPath = BuildContentPath(cardId, ".gif");
        var relativePreviewPath = BuildPreviewPath(cardId, ".gif");
        var originalPath = Path.Combine(cardDirectory, "original.gif");
        var previewPath = Path.Combine(previewDirectory, "preview.gif");

        File.Move(temporaryUploadPath, originalPath, overwrite: true);
        var previewStart = await GenerateAnimatedAssetsAsync(originalPath, previewPath,
            Path.Combine(previewDirectory, "poster.webp"), metadata.Duration);

        var fileInfo = new FileInfo(originalPath);

        return new CardFileAssets(
            relativeContentPath,
            relativePreviewPath,
            "gif",
            "image/gif",
            originalFileName,
            fileInfo.Length,
            metadata.Width,
            metadata.Height,
            metadata.Duration,
            metadata.FrameCount,
            PosterPath: BuildPosterPath(cardId),
            PreviewStartSeconds: previewStart);
    }

    private async Task<GifProbeResult> ReadGifMetadataAsync(string gifPath)
    {
        try
        {
            using var loadedImage = await Image.LoadAsync(gifPath);
            var format = loadedImage.Metadata.DecodedImageFormat;

            if (!string.Equals(format?.DefaultMimeType, "image/gif", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Uploaded file is not a valid GIF.");
            }

            var duration = 0.0;

            foreach (var frame in loadedImage.Frames)
            {
                var frameDelay = frame.Metadata.GetGifMetadata().FrameDelay;

                if (frameDelay > 0)
                {
                    duration += frameDelay / 100.0;
                }
            }

            return new GifProbeResult(
                loadedImage.Width,
                loadedImage.Height,
                loadedImage.Frames.Count,
                duration);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Uploaded GIF could not be read.", exception);
        }
    }

    private async Task<VideoProbeResult> ProbeVideoAsync(string videoPath)
    {
        var result = await RunProcessAsync("ffprobe", [
            "-v",
            "error",
            "-print_format",
            "json",
            "-show_entries",
            "format=format_name,duration:stream=codec_type,codec_name,pix_fmt,width,height,channels,sample_rate",
            videoPath,
        ]);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Unsupported or unreadable media content. Use JPEG, PNG, WebP, GIF, MP4, MOV, or WebM.");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var formatName = root.GetProperty("format").GetProperty("format_name").GetString() ?? "";
        var container = await DetectVideoContainerAsync(videoPath, formatName);
        var mimeType = container switch
        {
            "mp4" => "video/mp4",
            "mov" => "video/quicktime",
            "webm" => "video/webm",
            _ => null,
        };

        if (mimeType is null)
        {
            throw new InvalidOperationException("Unsupported video format. Only MP4, MOV, and WebM are supported.");
        }

        var durationText = root.GetProperty("format").TryGetProperty("duration", out var durationElement)
            ? durationElement.GetString()
            : null;

        if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            || duration <= 0)
        {
            throw new InvalidOperationException("Uploaded video has no readable duration.");
        }

        var width = 0;
        var height = 0;
        var hasAudio = false;
        string? videoCodec = null;
        string? audioCodec = null;
        string? pixelFormat = null;
        int? audioChannels = null;
        int? audioSampleRate = null;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var codecType = stream.GetProperty("codec_type").GetString();
            if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase))
            {
                hasAudio = true;
                audioCodec ??= stream.TryGetProperty("codec_name", out var audioCodecElement) ? audioCodecElement.GetString() : null;
                audioChannels ??= stream.TryGetProperty("channels", out var channelsElement) && channelsElement.TryGetInt32(out var channels) ? channels : null;
                audioSampleRate ??= stream.TryGetProperty("sample_rate", out var sampleRateElement)
                    && int.TryParse(sampleRateElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleRate)
                    ? sampleRate : null;
                continue;
            }
            if (!string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase))
                continue;

            videoCodec ??= stream.TryGetProperty("codec_name", out var videoCodecElement) ? videoCodecElement.GetString() : null;
            pixelFormat ??= stream.TryGetProperty("pix_fmt", out var pixelFormatElement) ? pixelFormatElement.GetString() : null;

            var streamWidth = stream.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : 0;
            var streamHeight = stream.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : 0;

            if (streamWidth > 0 && streamHeight > 0 && width == 0)
            {
                width = streamWidth;
                height = streamHeight;
            }
        }

        if (width > 0 && height > 0)
            return new VideoProbeResult(mimeType, container!, width, height, duration, hasAudio, videoCodec, audioCodec, pixelFormat, audioChannels, audioSampleRate);

        throw new InvalidOperationException("Uploaded file does not contain a readable video stream.");
    }

    private async Task<double> GenerateAnimatedAssetsAsync(string originalPath, string previewPath,
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
                    return candidate.Start;
                }
                catch (InvalidOperationException)
                {
                    File.Delete(previewPath);
                    File.Delete(posterPath);
                }
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

    private static string FormatSeconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;

        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"{fileName} is not available in the backend runtime.", exception);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => process.WaitForExit((int)(timeout ?? ProcessingTimeout).TotalMilliseconds));

            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"{fileName} timed out while processing the uploaded video.");
            }

            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
    }

    private string GetFullPath(string relativePath)
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_appDataRoot, normalizedPath));
        var rootPath = Path.GetFullPath(_appDataRoot);

        if (!fullPath.StartsWith(rootPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Storage path escapes the application data directory.");
        }

        return fullPath;
    }

    private static async Task SaveUploadedFileAsync(IFormFile file, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        await using var output = File.Create(outputPath);
        await using var input = file.OpenReadStream();
        await input.CopyToAsync(output);
    }

    private static string BuildContentPath(long cardId, string extension) =>
        Path.Combine("cards", cardId.ToString(), $"original{extension}").Replace('\\', '/');

    private static string BuildPreviewPath(long cardId, string extension) =>
        Path.Combine("cache", "cards", cardId.ToString(), $"preview{extension}").Replace('\\', '/');

    private static string BuildPosterPath(long cardId) =>
        Path.Combine("cache", "cards", cardId.ToString(), "poster.webp").Replace('\\', '/');

    private static async Task<string?> DetectImageExtensionAsync(IFormFile file)
    {
        var header = new byte[12];
        await using var stream = file.OpenReadStream();
        var bytesRead = 0;
        while (bytesRead < header.Length)
        {
            var count = await stream.ReadAsync(header.AsMemory(bytesRead));
            if (count == 0) break;
            bytesRead += count;
        }
        var bytes = header.AsSpan(0, bytesRead);
        if (bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return ".jpg";
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return ".png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return ".gif";
        return null;
    }

    private static string SafeUploadName(string fileName)
    {
        var name = fileName.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        name = new string(name.Where(character => !char.IsControl(character) && character is not '\'' and not '"').Take(120).ToArray());
        return string.IsNullOrWhiteSpace(name) ? "unnamed upload" : name;
    }

    private static async Task<string?> DetectVideoContainerAsync(string path, string formatName)
    {
        var formats = formatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = new byte[4096];
        await using var stream = File.OpenRead(path);
        var count = await stream.ReadAsync(header);
        var bytes = header.AsSpan(0, count);
        if (formats.Contains("webm") || formats.Contains("matroska"))
            return bytes.StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }) && bytes.IndexOf("webm"u8) >= 0 ? "webm" : null;
        if (!formats.Any(format => format is "mp4" or "mov" or "m4a" or "3gp" or "3g2" or "mj2"))
            return null;
        if (bytes.Length < 12 || !bytes[4..8].SequenceEqual("ftyp"u8)) return null;
        return bytes[8..12].SequenceEqual("qt  "u8) ? "mov" : "mp4";
    }

    private static bool IsBrowserCompatible(VideoProbeResult probe)
    {
        return probe.Container switch
        {
            "mp4" => probe.VideoCodec == "h264" && probe.PixelFormat == "yuv420p" && (!probe.HasAudio || probe.AudioCodec == "aac"),
            "webm" => (probe.VideoCodec is "vp8" or "vp9") && probe.PixelFormat == "yuv420p" && (!probe.HasAudio || probe.AudioCodec is "opus" or "vorbis"),
            _ => false,
        };
    }

    private static void DeleteDirectoryIfExists(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record VideoProbeResult(string MimeType, string Container, int Width, int Height, double Duration, bool HasAudio, string? VideoCodec, string? AudioCodec, string? PixelFormat, int? AudioChannels, int? AudioSampleRate);

    private sealed record GifProbeResult(int Width, int Height, int FrameCount, double Duration);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

public sealed class PendingCardFileReplacement
{
    private readonly string _stagingRoot;
    private readonly string _stagingCardDirectory;
    private readonly string _stagingPreviewDirectory;
    private readonly string _finalCardDirectory;
    private readonly string _finalPreviewDirectory;
    private readonly string _backupRoot;
    private readonly string _backupCardDirectory;
    private readonly string _backupPreviewDirectory;

    public PendingCardFileReplacement(
        string appDataRoot,
        string stagingRoot,
        string stagingCardDirectory,
        string stagingPreviewDirectory,
        string finalCardDirectory,
        string finalPreviewDirectory,
        CardFileAssets assets)
    {
        _stagingRoot = stagingRoot;
        _stagingCardDirectory = stagingCardDirectory;
        _stagingPreviewDirectory = stagingPreviewDirectory;
        _finalCardDirectory = finalCardDirectory;
        _finalPreviewDirectory = finalPreviewDirectory;
        _backupRoot = Path.Combine(appDataRoot, "tmp", "replacement-backups", Guid.NewGuid().ToString("N"));
        _backupCardDirectory = Path.Combine(_backupRoot, "cards");
        _backupPreviewDirectory = Path.Combine(_backupRoot, "preview");
        Assets = assets;
    }

    public CardFileAssets Assets { get; }

    public Task PromoteAsync()
    {
        Directory.CreateDirectory(_backupRoot);

        MoveDirectoryIfExists(_finalCardDirectory, _backupCardDirectory);
        MoveDirectoryIfExists(_finalPreviewDirectory, _backupPreviewDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(_finalCardDirectory)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_finalPreviewDirectory)!);
        Directory.Move(_stagingCardDirectory, _finalCardDirectory);
        Directory.Move(_stagingPreviewDirectory, _finalPreviewDirectory);

        return Task.CompletedTask;
    }

    public void Rollback()
    {
        DeleteDirectoryIfExists(_finalCardDirectory);
        DeleteDirectoryIfExists(_finalPreviewDirectory);
        MoveDirectoryIfExists(_backupCardDirectory, _finalCardDirectory);
        MoveDirectoryIfExists(_backupPreviewDirectory, _finalPreviewDirectory);
        CleanupStaging();
    }

    public void CleanupAfterCommit()
    {
        DeleteDirectoryIfExists(_backupRoot);
        CleanupStaging();
    }

    public void CleanupStaging()
    {
        DeleteDirectoryIfExists(_stagingRoot);
    }

    private static void MoveDirectoryIfExists(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);
    }

    private static void DeleteDirectoryIfExists(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class PendingPreviewReplacement
{
    private readonly string _stagingRoot;
    private readonly string _stagingPreviewDirectory;
    private readonly string _finalPreviewDirectory;
    private readonly string _backupPreviewDirectory;
    private bool _oldPreviewBackedUp;
    private bool _newPreviewPromoted;

    public PendingPreviewReplacement(string stagingRoot, string stagingPreviewDirectory,
        string finalPreviewDirectory, string backupPreviewDirectory, double previewStartSeconds)
    {
        _stagingRoot = stagingRoot;
        _stagingPreviewDirectory = stagingPreviewDirectory;
        _finalPreviewDirectory = finalPreviewDirectory;
        _backupPreviewDirectory = backupPreviewDirectory;
        PreviewStartSeconds = previewStartSeconds;
    }

    public double PreviewStartSeconds { get; }

    public Task PromoteAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_backupPreviewDirectory)!);
        if (Directory.Exists(_finalPreviewDirectory))
        {
            Directory.Move(_finalPreviewDirectory, _backupPreviewDirectory);
            _oldPreviewBackedUp = true;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_finalPreviewDirectory)!);
        Directory.Move(_stagingPreviewDirectory, _finalPreviewDirectory);
        _newPreviewPromoted = true;
        return Task.CompletedTask;
    }

    public void Rollback()
    {
        if (_newPreviewPromoted && Directory.Exists(_finalPreviewDirectory))
            Directory.Delete(_finalPreviewDirectory, recursive: true);
        if (_oldPreviewBackedUp && Directory.Exists(_backupPreviewDirectory))
            Directory.Move(_backupPreviewDirectory, _finalPreviewDirectory);
        if (Directory.Exists(_stagingRoot))
            Directory.Delete(_stagingRoot, recursive: true);
    }

    public void CleanupAfterCommit()
    {
        if (Directory.Exists(_backupPreviewDirectory))
            Directory.Delete(_backupPreviewDirectory, recursive: true);
        if (Directory.Exists(_stagingRoot))
            Directory.Delete(_stagingRoot, recursive: true);
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MyApp.Api.Cards;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
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

        await GenerateVideoPreviewAsync(originalPath, previewPath, probe.Duration);

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
            AudioSampleRate: probe.AudioSampleRate);
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
        await GenerateGifPreviewAsync(originalPath, previewPath, metadata.Duration);

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
            metadata.FrameCount);
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

    private async Task GenerateVideoPreviewAsync(string originalPath, string previewPath, double duration)
    {
        var previewDuration = Math.Min(duration, 3.0).ToString("0.###", CultureInfo.InvariantCulture);
        var palettePath = Path.Combine(Path.GetDirectoryName(previewPath)!, "palette.png");
        var scaleFilter = $"fps=10,scale='if(gte(iw,ih),min({MaxPreviewDimension},iw),-2)':'if(gte(ih,iw),min({MaxPreviewDimension},ih),-2)':flags=lanczos";

        var palette = await RunProcessAsync("ffmpeg", [
            "-y",
            "-t",
            previewDuration,
            "-i",
            originalPath,
            "-an",
            "-vf",
            $"{scaleFilter},palettegen",
            palettePath,
        ]);

        if (palette.ExitCode != 0)
        {
            throw new InvalidOperationException("Failed to generate video preview palette.");
        }

        ProcessResult preview;

        try
        {
            preview = await RunProcessAsync("ffmpeg", [
                "-y",
                "-t",
                previewDuration,
                "-i",
                originalPath,
                "-i",
                palettePath,
                "-an",
                "-filter_complex",
                $"{scaleFilter}[x];[x][1:v]paletteuse",
                "-loop",
                "0",
                previewPath,
            ]);
        }
        finally
        {
            File.Delete(palettePath);
        }

        if (preview.ExitCode != 0 || !File.Exists(previewPath))
        {
            throw new InvalidOperationException("Failed to generate animated video preview.");
        }
    }

    private async Task GenerateGifPreviewAsync(string originalPath, string previewPath, double duration)
    {
        var previewDuration = Math.Min(duration, 3.0);
        var palettePath = Path.Combine(Path.GetDirectoryName(previewPath)!, "palette.png");
        var scaleFilter = $"scale='if(gte(iw,ih),min({MaxPreviewDimension},iw),-2)':'if(gte(ih,iw),min({MaxPreviewDimension},ih),-2)':flags=lanczos";
        var durationArguments = previewDuration > 0
            ? new[] { "-t", previewDuration.ToString("0.###", CultureInfo.InvariantCulture) }
            : Array.Empty<string>();

        var paletteArguments = new List<string>
        {
            "-y",
        };
        paletteArguments.AddRange(durationArguments);
        paletteArguments.AddRange([
            "-i",
            originalPath,
            "-vf",
            $"{scaleFilter},palettegen",
            palettePath,
        ]);

        var palette = await RunProcessAsync("ffmpeg", paletteArguments);

        if (palette.ExitCode != 0)
        {
            throw new InvalidOperationException("Failed to generate GIF preview palette.");
        }

        ProcessResult preview;

        try
        {
            var previewArguments = new List<string>
            {
                "-y",
            };
            previewArguments.AddRange(durationArguments);
            previewArguments.AddRange([
                "-i",
                originalPath,
                "-i",
                palettePath,
                "-filter_complex",
                $"{scaleFilter}[x];[x][1:v]paletteuse",
                "-loop",
                "0",
                previewPath,
            ]);

            preview = await RunProcessAsync("ffmpeg", previewArguments);
        }
        finally
        {
            File.Delete(palettePath);
        }

        if (preview.ExitCode != 0 || !File.Exists(previewPath))
        {
            throw new InvalidOperationException("Failed to generate compressed GIF preview.");
        }
    }

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

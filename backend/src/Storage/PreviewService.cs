using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MyApp.Api.Cards;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace MyApp.Api.Storage;

public sealed class PreviewService
{
    private const int MaxPreviewDimension = 512;
    private const int PreviewQuality = 75;
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromSeconds(60);

    private readonly string _appDataRoot;

    public PreviewService(IConfiguration configuration)
    {
        _appDataRoot = configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
    }

    public async Task<CardFileAssets> SaveMediaAndCreatePreviewAsync(long cardId, IFormFile media)
    {
        if (LooksLikeSupportedStaticImage(media))
        {
            try
            {
                return await SaveImageAndCreatePreviewAsync(cardId, media);
            }
            catch
            {
                DeleteCardFiles(cardId);
            }
        }

        return await SaveVideoAndCreatePreviewAsync(cardId, media);
    }

    public async Task<CardFileAssets> SaveImageAndCreatePreviewAsync(long cardId, IFormFile image)
    {
        if (image.Length <= 0)
        {
            throw new InvalidOperationException("Uploaded image is empty.");
        }

        var originalFileName = Path.GetFileName(image.FileName);
        var originalExtension = GetSafeImageExtension(originalFileName, image.ContentType);
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
        catch
        {
            DeleteCardFiles(cardId);
            throw;
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

            if (LooksLikeSupportedStaticImage(media))
            {
                try
                {
                    assets = await PrepareImageReplacementAsync(cardId, media, stagingCardDirectory, stagingPreviewDirectory);
                }
                catch
                {
                    DeleteDirectoryIfExists(stagingCardDirectory);
                    DeleteDirectoryIfExists(stagingPreviewDirectory);
                    Directory.CreateDirectory(stagingCardDirectory);
                    Directory.CreateDirectory(stagingPreviewDirectory);
                    assets = await PrepareVideoReplacementAsync(cardId, media, stagingCardDirectory, stagingPreviewDirectory);
                }
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

        var originalFileName = Path.GetFileName(video.FileName);
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

        var originalFileName = Path.GetFileName(image.FileName);
        var originalExtension = GetSafeImageExtension(originalFileName, image.ContentType);
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
            Path.GetFileName(video.FileName));
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
        var mimeType = format?.DefaultMimeType ?? image.ContentType;
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
        var uploadExtension = GetSafeVideoExtension(originalFileName, video.ContentType);
        var temporaryUploadPath = Path.Combine(cardDirectory, $"upload{uploadExtension}");

        await SaveUploadedFileAsync(video, temporaryUploadPath);

        var probe = await ProbeVideoAsync(temporaryUploadPath);
        var finalExtension = probe.MimeType switch
        {
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            _ => throw new InvalidOperationException("Unsupported video format. Only MP4 and WebM are supported."),
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
            probe.Duration);
    }

    private async Task<VideoProbeResult> ProbeVideoAsync(string videoPath)
    {
        var result = await RunProcessAsync("ffprobe", [
            "-v",
            "error",
            "-print_format",
            "json",
            "-show_entries",
            "format=format_name,duration:stream=codec_type,width,height",
            videoPath,
        ]);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Uploaded video could not be read.");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var formatName = root.GetProperty("format").GetProperty("format_name").GetString() ?? "";
        var mimeType = ResolveVideoMimeType(formatName);

        if (mimeType is null)
        {
            throw new InvalidOperationException("Unsupported video format. Only MP4 and WebM are supported.");
        }

        var durationText = root.GetProperty("format").TryGetProperty("duration", out var durationElement)
            ? durationElement.GetString()
            : null;

        if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            || duration <= 0)
        {
            throw new InvalidOperationException("Uploaded video has no readable duration.");
        }

        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            if (!string.Equals(stream.GetProperty("codec_type").GetString(), "video", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var width = stream.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : 0;
            var height = stream.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : 0;

            if (width > 0 && height > 0)
            {
                return new VideoProbeResult(mimeType, width, height, duration);
            }
        }

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

    private async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
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
            var exited = await Task.Run(() => process.WaitForExit((int)ProcessingTimeout.TotalMilliseconds));

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

    private static bool LooksLikeSupportedStaticImage(IFormFile file)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var contentType = file.ContentType.ToLowerInvariant();

        return extension is ".jpg" or ".jpeg" or ".png" or ".webp"
            || contentType is "image/jpeg" or "image/png" or "image/webp";
    }

    private static string GetSafeImageExtension(string originalFileName, string? contentType)
    {
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();

        if (extension is ".jpg" or ".jpeg" or ".png" or ".webp")
        {
            return extension;
        }

        return contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".img",
        };
    }

    private static string GetSafeVideoExtension(string originalFileName, string? contentType)
    {
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();

        if (extension is ".mp4" or ".webm")
        {
            return extension;
        }

        return contentType?.ToLowerInvariant() switch
        {
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            _ => ".video",
        };
    }

    private static string? ResolveVideoMimeType(string formatName)
    {
        var formats = formatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (formats.Any(format => format is "mp4" or "mov" or "m4a" or "3gp" or "3g2" or "mj2"))
        {
            return "video/mp4";
        }

        if (formats.Contains("matroska") || formats.Contains("webm"))
        {
            return "video/webm";
        }

        return null;
    }

    private static void DeleteDirectoryIfExists(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record VideoProbeResult(string MimeType, int Width, int Height, double Duration);

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

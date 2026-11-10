using System.Diagnostics;
using MyApp.Api.Cards;
using static MyApp.Api.Storage.StorageDirectories;
using static MyApp.Api.Storage.VideoProbe;

namespace MyApp.Api.Storage;

public sealed partial class PreviewService
{
    private const int MaxPreviewDimension = 512;
    private const int PreviewQuality = 75;
    private static readonly TimeSpan TranscodingTimeout = TimeSpan.FromMinutes(10);

    private readonly string _appDataRoot;
    private readonly MediaProcessingGate _gate;
    private readonly double _previewQualityThreshold;
    private readonly long _maxImagePixels;

    /// <summary>Test seam: replaces the animated preview render (throw InvalidOperationException to simulate failure).</summary>
    internal Func<string, string, double, Task>? AnimatedRenderOverride { get; set; }

    public PreviewService(IConfiguration configuration)
    {
        _gate = new MediaProcessingGate(ReadInt(configuration, "Media:MaxConcurrentProcessing", 2));
        _previewQualityThreshold = double.TryParse(configuration["Media:PreviewQualityThreshold"],
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var threshold)
            ? threshold : 0.25;
        _maxImagePixels = ReadInt(configuration, "Media:MaxImagePixels", 50_000_000);
        _appDataRoot = configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    public async Task<CardFileAssets> SaveMediaAndCreatePreviewAsync(long cardId, IFormFile media)
    {
        try
        {
            using var slot = await _gate.AcquireAsync();
            var assets = await DetectImageExtensionAsync(media) switch
            {
                ".gif" => await SaveGifAndCreatePreviewAsync(cardId, media),
                not null => await SaveImageCoreAsync(cardId, media),
                _ => await SaveVideoAndCreatePreviewAsync(cardId, media),
            };
            return assets with { ContentSha256 = await HashUploadAsync(media) };
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
        using var slot = await _gate.AcquireAsync();
        var assets = await SaveImageCoreAsync(cardId, image);
        return assets with { ContentSha256 = await HashUploadAsync(image) };
    }

    private static async Task<string> HashUploadAsync(IFormFile file)
    {
        await using var stream = file.OpenReadStream();
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private async Task<CardFileAssets> SaveImageCoreAsync(long cardId, IFormFile image)
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
            if (exception is ImageTooLargeException)
                throw new InvalidOperationException($"File '{originalFileName}': {exception.Message}", exception);
            throw new InvalidOperationException($"File '{originalFileName}': image could not be read or previewed.", exception);
        }
    }

    public async Task<PendingCardFileReplacement> PrepareMediaReplacementAsync(long cardId, IFormFile media)
    {
        using var slot = await _gate.AcquireAsync();
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
            assets = assets with { ContentSha256 = await HashUploadAsync(media) };

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

        using var slot = await _gate.AcquireAsync();
        var stagingRoot = Path.Combine(_appDataRoot, "tmp", "preview-overrides", Guid.NewGuid().ToString("N"));
        var stagingPreviewDirectory = Path.Combine(stagingRoot, "preview");
        Directory.CreateDirectory(stagingPreviewDirectory);
        var previewPath = Path.Combine(stagingPreviewDirectory, "preview.gif");
        var posterPath = Path.Combine(stagingPreviewDirectory, "poster.webp");

        try
        {
            AnimatedPreviewResult result;
            if (startSeconds is null)
            {
                result = await GenerateAnimatedAssetsAsync(originalPath, previewPath, posterPath, duration);
            }
            else
            {
                var clock = Stopwatch.StartNew();
                await RenderAnimatedAssetsAtStartAsync(originalPath, previewPath, posterPath,
                    startSeconds.Value, duration, clock);
                result = new AnimatedPreviewResult(startSeconds.Value, 0, "manual", true);
            }

            return new PendingPreviewReplacement(
                stagingRoot, stagingPreviewDirectory,
                Path.Combine(_appDataRoot, "cache", "cards", cardId.ToString()),
                Path.Combine(_appDataRoot, "tmp", "preview-override-backups", Guid.NewGuid().ToString("N")),
                result.Start,
                result.Selection,
                result.Animated ? "preview.gif" : "poster.webp",
                result.Selection == "manual" ? null : Math.Max(0, result.Score));
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

    public bool CanReadStoredFile(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        try
        {
            using var stream = OpenRead(relativePath);
            return stream is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
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

    private string GetFullPath(string relativePath)
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_appDataRoot, normalizedPath));
        var rootPath = Path.GetFullPath(_appDataRoot);
        var pathFromRoot = Path.GetRelativePath(rootPath, fullPath);

        if (Path.IsPathRooted(pathFromRoot) ||
            pathFromRoot == ".." ||
            pathFromRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
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
}

using MyApp.Api.Cards;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace MyApp.Api.Storage;

public sealed class PreviewService
{
    private const int MaxPreviewDimension = 512;
    private const int PreviewQuality = 75;

    private readonly string _appDataRoot;

    public PreviewService(IConfiguration configuration)
    {
        _appDataRoot = configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
    }

    public async Task<CardFileAssets> SaveImageAndCreatePreviewAsync(long cardId, IFormFile image)
    {
        if (image.Length <= 0)
        {
            throw new InvalidOperationException("Uploaded image is empty.");
        }

        var originalFileName = Path.GetFileName(image.FileName);
        var originalExtension = GetSafeExtension(originalFileName, image.ContentType);
        var cardDirectory = Path.Combine(_appDataRoot, "cards", cardId.ToString());
        var previewDirectory = Path.Combine(_appDataRoot, "cache", "cards", cardId.ToString());
        Directory.CreateDirectory(cardDirectory);
        Directory.CreateDirectory(previewDirectory);

        var relativeContentPath = Path.Combine("cards", cardId.ToString(), $"original{originalExtension}")
            .Replace('\\', '/');
        var relativePreviewPath = Path.Combine("cache", "cards", cardId.ToString(), "preview.webp")
            .Replace('\\', '/');

        var originalPath = Path.Combine(_appDataRoot, relativeContentPath);
        var previewPath = Path.Combine(_appDataRoot, relativePreviewPath);

        await using (var output = File.Create(originalPath))
        await using (var input = image.OpenReadStream())
        {
            await input.CopyToAsync(output);
        }

        try
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
        catch
        {
            DeleteCardFiles(cardId);
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
            _ => "application/octet-stream",
        };
    }

    public void DeleteCardFiles(long cardId)
    {
        var cardDirectory = Path.Combine(_appDataRoot, "cards", cardId.ToString());
        var previewDirectory = Path.Combine(_appDataRoot, "cache", "cards", cardId.ToString());

        if (Directory.Exists(cardDirectory))
        {
            Directory.Delete(cardDirectory, recursive: true);
        }

        if (Directory.Exists(previewDirectory))
        {
            Directory.Delete(previewDirectory, recursive: true);
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

    private static string GetSafeExtension(string originalFileName, string? contentType)
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
}

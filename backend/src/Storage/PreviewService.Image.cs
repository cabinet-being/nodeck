using MyApp.Api.Cards;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace MyApp.Api.Storage;

public sealed partial class PreviewService
{
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
}

using MyApp.Api.Cards;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
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
        var info = await Image.IdentifyAsync(originalPath);
        if ((long)info.Width * info.Height > _maxImagePixels)
            throw new ImageTooLargeException(
                $"Image is too large: {info.Width}x{info.Height} exceeds {_maxImagePixels} pixels.");

        // Animated WebP: media_type stays "image"; only the first frame is decoded for the static preview.
        var webp = info.Metadata.DecodedImageFormat?.DefaultMimeType == "image/webp"
            ? WebpAnimationReader.Read(originalPath) : null;
        var animated = webp is not null;
        var frameCount = webp?.FrameCount ?? 0;
        double? animatedDuration = webp?.Duration;

        using var loadedImage = await Image.LoadAsync(new DecoderOptions { MaxFrames = 1 }, originalPath);
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
            height,
            animated ? animatedDuration : null,
            animated ? frameCount : null,
            PreviewSelection: animated ? "first_frame" : null,
            Animated: animated ? true : null);
    }

    private sealed class ImageTooLargeException(string message) : InvalidOperationException(message);
}

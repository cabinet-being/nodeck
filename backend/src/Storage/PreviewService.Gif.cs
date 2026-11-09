using MyApp.Api.Cards;
using static MyApp.Api.Storage.GifMetadataReader;
using static MyApp.Api.Storage.StorageDirectories;

namespace MyApp.Api.Storage;

public sealed partial class PreviewService
{
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

    private async Task<CardFileAssets> CreateGifAssetsAsync(
        long cardId,
        IFormFile gif,
        string cardDirectory,
        string previewDirectory,
        string originalFileName)
    {
        var temporaryUploadPath = Path.Combine(cardDirectory, "upload.gif");

        await SaveUploadedFileAsync(gif, temporaryUploadPath);

        var metadata = ReadGifMetadata(temporaryUploadPath);
        var relativeContentPath = BuildContentPath(cardId, ".gif");
        var relativePreviewPath = BuildPreviewPath(cardId, ".gif");
        var originalPath = Path.Combine(cardDirectory, "original.gif");
        var previewPath = Path.Combine(previewDirectory, "preview.gif");

        File.Move(temporaryUploadPath, originalPath, overwrite: true);
        var preview = await GenerateAnimatedAssetsAsync(originalPath, previewPath,
            Path.Combine(previewDirectory, "poster.webp"), metadata.Duration);

        var fileInfo = new FileInfo(originalPath);

        return new CardFileAssets(
            relativeContentPath,
            preview.Animated ? relativePreviewPath : BuildPosterPath(cardId),
            "gif",
            "image/gif",
            originalFileName,
            fileInfo.Length,
            metadata.Width,
            metadata.Height,
            metadata.Duration,
            metadata.FrameCount,
            PosterPath: BuildPosterPath(cardId),
            PreviewStartSeconds: preview.Start,
            PreviewSelection: preview.Selection,
            PreviewScore: Math.Max(0, preview.Score),
            Animated: metadata.FrameCount > 1);
    }
}

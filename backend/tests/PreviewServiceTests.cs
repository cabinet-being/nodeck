using Microsoft.Extensions.Configuration;
using MyApp.Api.Cards;
using MyApp.Api.Storage;

namespace MyApp.Api.Tests;

public sealed class PreviewServiceTests
{
    [Theory]
    [InlineData("cards/1/original.jpg", "image/jpeg")]
    [InlineData("cards/1/original.png", "image/png")]
    [InlineData("cards/1/original.webp", "image/webp")]
    [InlineData("cards/1/original.gif", "image/gif")]
    [InlineData("cache/cards/1/preview.gif", "image/gif")]
    [InlineData("cards/1/original.mp4", "video/mp4")]
    [InlineData("cards/1/original.webm", "video/webm")]
    public void GetContentTypeMapsSupportedMedia(string path, string expectedContentType)
    {
        var service = CreateService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.Equal(expectedContentType, service.GetContentType(path));
    }

    [Fact]
    public void CardFileAssetsPreservesGifFrameCount()
    {
        var assets = new CardFileAssets(
            "cards/1/original.gif",
            "cache/cards/1/preview.gif",
            "gif",
            "image/gif",
            "animation.gif",
            1024,
            320,
            240,
            1.25,
            8);

        Assert.Equal("gif", assets.MediaType);
        Assert.Equal(8, assets.FrameCount);
    }

    [Fact]
    public async Task PendingReplacementRestoresPreviousFilesOnRollback()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var finalCardDirectory = Path.Combine(root, "cards", "12");
        var finalPreviewDirectory = Path.Combine(root, "cache", "cards", "12");
        var stagingRoot = Path.Combine(root, "tmp", "replacements", Guid.NewGuid().ToString("N"));
        var stagingCardDirectory = Path.Combine(stagingRoot, "cards", "12");
        var stagingPreviewDirectory = Path.Combine(stagingRoot, "cache", "cards", "12");

        Directory.CreateDirectory(finalCardDirectory);
        Directory.CreateDirectory(finalPreviewDirectory);
        Directory.CreateDirectory(stagingCardDirectory);
        Directory.CreateDirectory(stagingPreviewDirectory);

        await File.WriteAllTextAsync(Path.Combine(finalCardDirectory, "original.jpg"), "old-content");
        await File.WriteAllTextAsync(Path.Combine(finalPreviewDirectory, "preview.webp"), "old-preview");
        await File.WriteAllTextAsync(Path.Combine(stagingCardDirectory, "original.mp4"), "new-content");
        await File.WriteAllTextAsync(Path.Combine(stagingPreviewDirectory, "preview.gif"), "new-preview");

        var replacement = new PendingCardFileReplacement(
            root,
            stagingRoot,
            stagingCardDirectory,
            stagingPreviewDirectory,
            finalCardDirectory,
            finalPreviewDirectory,
            new CardFileAssets(
                "cards/12/original.mp4",
                "cache/cards/12/preview.gif",
                "video",
                "video/mp4",
                "clip.mp4",
                11,
                640,
                360,
                1.5));

        await replacement.PromoteAsync();
        replacement.Rollback();

        Assert.True(File.Exists(Path.Combine(finalCardDirectory, "original.jpg")));
        Assert.True(File.Exists(Path.Combine(finalPreviewDirectory, "preview.webp")));
        Assert.False(File.Exists(Path.Combine(finalCardDirectory, "original.mp4")));
    }

    private static PreviewService CreateService(string root)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NODECK_APP_DATA"] = root,
            })
            .Build();

        return new PreviewService(configuration);
    }
}

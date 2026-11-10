using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Cards;
using MyApp.Api.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp.PixelFormats;

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
    [InlineData("cards/1/original.mov", "video/quicktime")]
    [InlineData("cards/1/original.webm", "video/webm")]
    public void GetContentTypeMapsSupportedMedia(string path, string expectedContentType)
    {
        var service = CreateService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.Equal(expectedContentType, service.GetContentType(path));
    }

    [Fact]
    public async Task OpenReadRejectsSiblingDirectoryWithStorageRootPrefix()
    {
        var parent = Path.Combine(Path.GetTempPath(), "nodeck-preview-tests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "data");
        var sibling = Path.Combine(parent, "data-evil");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "allowed.txt"), "inside root");
            await File.WriteAllTextAsync(Path.Combine(sibling, "secret.txt"), "outside root");
            var service = CreateService(root);

            using (var stream = Assert.IsType<FileStream>(service.OpenRead("allowed.txt")))
            using (var reader = new StreamReader(stream))
            {
                Assert.Equal("inside root", await reader.ReadToEndAsync());
            }

            Assert.Throws<InvalidOperationException>(() => service.OpenRead("../data-evil/secret.txt"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task CanReadStoredFileAcceptsOnlyExistingFilesWithinStorageRoot()
    {
        var parent = Path.Combine(Path.GetTempPath(), "nodeck-preview-tests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "data");
        var sibling = Path.Combine(parent, "data-evil");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "allowed.txt"), "inside root");
            await File.WriteAllTextAsync(Path.Combine(sibling, "secret.txt"), "outside root");
            var service = CreateService(root);

            Assert.True(service.CanReadStoredFile("allowed.txt"));
            Assert.False(service.CanReadStoredFile("missing.txt"));
            Assert.False(service.CanReadStoredFile(null));
            Assert.False(service.CanReadStoredFile("../data-evil/secret.txt"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
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

    [Theory]
    [InlineData("picture.jpg", "image/jpeg")]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("mystery.bin", "application/octet-stream")]
    public async Task ImageTypeFollowsDecodedContentDespiteMisleadingUploadLabels(string fileName, string contentType)
    {
        var root = NewTempRoot();
        try
        {
            using var image = new Image<Rgba32>(4, 3);
            await using var bytes = new MemoryStream();
            await image.SaveAsPngAsync(bytes);
            var upload = Upload(bytes.ToArray(), fileName, contentType);

            var assets = await CreateService(root).SaveMediaAndCreatePreviewAsync(15, upload);

            Assert.Equal("image", assets.MediaType);
            Assert.Equal("image/png", assets.MimeType);
            Assert.Equal("cards/15/original.png", assets.ContentPath);
            Assert.Equal(4, assets.Width);
            Assert.Equal(3, assets.Height);
            Assert.Equal(bytes.ToArray(), await File.ReadAllBytesAsync(Path.Combine(root, assets.ContentPath)));
            Assert.True(File.Exists(Path.Combine(root, assets.PreviewPath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("broken.jpg", "image/jpeg")]
    [InlineData("broken.mp4", "video/mp4")]
    [InlineData("broken.dat", "application/octet-stream")]
    public async Task CorruptMediaIsRejectedWithoutLeavingCardFiles(string fileName, string contentType)
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var upload = Upload("not a media file"u8.ToArray(), fileName, contentType);

            await Assert.ThrowsAnyAsync<Exception>(() => CreateService(root).SaveMediaAndCreatePreviewAsync(16, upload));

            Assert.False(Directory.Exists(Path.Combine(root, "cards", "16")));
            Assert.False(Directory.Exists(Path.Combine(root, "cache", "cards", "16")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedImageContentIsRejectedEvenWhenNamedJpeg()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            using var image = new Image<Rgba32>(4, 3);
            await using var bytes = new MemoryStream();
            await image.SaveAsBmpAsync(bytes);
            var upload = Upload(bytes.ToArray(), "picture.jpg", "image/jpeg");

            await Assert.ThrowsAnyAsync<Exception>(() => CreateService(root).SaveMediaAndCreatePreviewAsync(19, upload));

            Assert.False(Directory.Exists(Path.Combine(root, "cards", "19")));
            Assert.False(Directory.Exists(Path.Combine(root, "cache", "cards", "19")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("mov", "video/mp4", "video/quicktime", "mov", "mpeg4", "pcm_s16le", ".mov", true)]
    [InlineData("webm", "video/mp4", "video/webm", "webm", "vp8", "vorbis", ".webm", false)]
    public async Task VideoTypeAndAudioComeFromContent(
        string format, string declaredType, string expectedType, string expectedContainer,
        string expectedVideoCodec, string expectedAudioCodec, string expectedExtension, bool needsPlayback)
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var source = Path.Combine(root, $"sample.{format}");
            await GenerateVideoAsync(source, format);
            var upload = Upload(await File.ReadAllBytesAsync(source), "wrong.mp4", declaredType);

            var assets = await CreateService(root).SaveMediaAndCreatePreviewAsync(17, upload);

            Assert.Equal("video", assets.MediaType);
            Assert.Equal(expectedType, assets.MimeType);
            Assert.Equal(expectedContainer, assets.Container);
            Assert.Equal(expectedVideoCodec, assets.VideoCodec);
            Assert.Equal(expectedAudioCodec, assets.AudioCodec);
            Assert.EndsWith(expectedExtension, assets.ContentPath);
            Assert.Equal(32, assets.Width);
            Assert.Equal(24, assets.Height);
            Assert.True(assets.Duration > 0);
            Assert.True(assets.HasAudio);
            Assert.NotNull(assets.FrameRate);
            Assert.Equal(4.0, assets.FrameRate!.Value, 2);
            Assert.True(File.Exists(Path.Combine(root, assets.PreviewPath)));
            if (needsPlayback)
            {
                Assert.Equal("cards/17/playback.mp4", assets.PlaybackPath);
                Assert.True(new FileInfo(Path.Combine(root, assets.PlaybackPath!)).Length > 0);
            }
            else
            {
                Assert.Null(assets.PlaybackPath);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedReplacementKeepsPreviousCardFiles()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var oldCard = Path.Combine(root, "cards", "18");
            var oldPreview = Path.Combine(root, "cache", "cards", "18");
            Directory.CreateDirectory(oldCard);
            Directory.CreateDirectory(oldPreview);
            await File.WriteAllTextAsync(Path.Combine(oldCard, "original.png"), "original data");
            await File.WriteAllTextAsync(Path.Combine(oldPreview, "preview.webp"), "preview data");
            var upload = Upload("invalid video"u8.ToArray(), "replacement.mov", "video/quicktime");

            await Assert.ThrowsAnyAsync<Exception>(() => CreateService(root).PrepareMediaReplacementAsync(18, upload));

            Assert.Equal("original data", await File.ReadAllTextAsync(Path.Combine(oldCard, "original.png")));
            Assert.Equal("preview data", await File.ReadAllTextAsync(Path.Combine(oldPreview, "preview.webp")));
            Assert.False(Directory.Exists(Path.Combine(root, "tmp", "replacements"))
                && Directory.EnumerateFileSystemEntries(Path.Combine(root, "tmp", "replacements")).Any());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RollbackRestoresOriginalAndRemovesPlaybackDerivative()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var cardDirectory = Path.Combine(root, "cards", "20");
            var previewDirectory = Path.Combine(root, "cache", "cards", "20");
            Directory.CreateDirectory(cardDirectory);
            Directory.CreateDirectory(previewDirectory);
            await File.WriteAllTextAsync(Path.Combine(cardDirectory, "original.png"), "old content");
            await File.WriteAllTextAsync(Path.Combine(previewDirectory, "preview.webp"), "old preview");
            var source = Path.Combine(root, "replacement.mov");
            await GenerateVideoAsync(source, "mov");
            var upload = Upload(await File.ReadAllBytesAsync(source), "replacement.mov", "video/quicktime");

            var replacement = await CreateService(root).PrepareMediaReplacementAsync(20, upload);
            Assert.Equal("cards/20/playback.mp4", replacement.Assets.PlaybackPath);
            await replacement.PromoteAsync();
            Assert.True(File.Exists(Path.Combine(cardDirectory, "playback.mp4")));

            replacement.Rollback();

            Assert.Equal("old content", await File.ReadAllTextAsync(Path.Combine(cardDirectory, "original.png")));
            Assert.Equal("old preview", await File.ReadAllTextAsync(Path.Combine(previewDirectory, "preview.webp")));
            Assert.False(File.Exists(Path.Combine(cardDirectory, "original.mov")));
            Assert.False(File.Exists(Path.Combine(cardDirectory, "playback.mp4")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GifPreviewAndPosterSelectVisibleSceneAfterDarkIntroduction()
    {
        var root = NewTempRoot();
        try
        {
            var upload = Upload(await GenerateTwoSceneGifAsync(), "scenes.gif", "image/gif");

            var assets = await CreateService(root).SaveMediaAndCreatePreviewAsync(21, upload);

            Assert.Equal("gif", assets.MediaType);
            Assert.True(assets.PreviewStartSeconds > 0, "Preview should skip the dark introduction.");
            Assert.Equal("cache/cards/21/poster.webp", assets.PosterPath);
            await AssertRedPosterAsync(root, assets.PosterPath);
            Assert.True(File.Exists(Path.Combine(root, assets.PreviewPath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GifMetadataCountsFramesAndSumsDifferentFrameDelays()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var upload = Upload(await GenerateGifWithDifferentDelaysAsync(), "delays.gif", "image/gif");

            var assets = await CreateService(root).SaveMediaAndCreatePreviewAsync(28, upload);

            Assert.Equal("gif", assets.MediaType);
            Assert.Equal(3, assets.FrameCount);
            Assert.Equal(0.45, Assert.IsType<double>(assets.Duration), precision: 2);
            Assert.Equal(8, assets.Width);
            Assert.Equal(8, assets.Height);
            Assert.True(File.Exists(Path.Combine(root, assets.ContentPath)));
            Assert.True(File.Exists(Path.Combine(root, assets.PreviewPath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TruncatedOrMalformedGifIsRejectedWithoutLeavingFiles(int corruption)
    {
        var root = NewTempRoot();
        try
        {
            var validGif = await GenerateGifWithDifferentDelaysAsync();
            Assert.Equal(0x3b, validGif[^1]);
            var contents = corruption switch
            {
                0 => validGif[..20], // Incomplete global color table.
                1 => validGif[..^1], // Missing trailer after otherwise complete frames.
                _ => validGif,
            };
            if (corruption == 2)
                contents[^1] = 0x00; // Invalid block marker in place of trailer.
            var upload = Upload(contents, "broken.gif", "image/gif");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateService(root).SaveMediaAndCreatePreviewAsync(29, upload));

            Assert.False(Directory.Exists(Path.Combine(root, "cards", "29")));
            Assert.False(Directory.Exists(Path.Combine(root, "cache", "cards", "29")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GifWithOversizedLogicalScreenIsRejectedWithoutLeavingFiles()
    {
        var root = NewTempRoot();
        try
        {
            // GIF89a, 5000 x 5000 logical screen, no global color table, trailer.
            byte[] contents = [
                0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
                0x88, 0x13, 0x88, 0x13, 0x00, 0x00, 0x00, 0x3b,
            ];
            var upload = Upload(contents, "oversized.gif", "image/gif");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateService(root).SaveMediaAndCreatePreviewAsync(30, upload));

            Assert.False(Directory.Exists(Path.Combine(root, "cards", "30")));
            Assert.False(Directory.Exists(Path.Combine(root, "cache", "cards", "30")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VideoPreviewAndPosterSelectVisibleSceneAfterDarkIntroduction()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var source = Path.Combine(root, "scenes.mp4");
            await GenerateTwoSceneVideoAsync(source);
            var upload = Upload(await File.ReadAllBytesAsync(source), "scenes.mp4", "video/mp4");

            var assets = await CreateService(root).SaveMediaAndCreatePreviewAsync(22, upload);
            var lenient = await CreateService(root, new() { ["Media:PreviewQualityThreshold"] = "0.05" })
                .SaveMediaAndCreatePreviewAsync(22, Upload(await File.ReadAllBytesAsync(source), "scenes.mp4", "video/mp4"));
            Assert.Equal("auto", lenient.PreviewSelection);

            Assert.Equal("video", assets.MediaType);
            Assert.True(assets.PreviewStartSeconds > 0, "Preview should skip the dark introduction.");
            // Pure red scene scores about 0.097: below the default 0.25 threshold, above 0.05.
            Assert.Equal("fallback", assets.PreviewSelection);
            Assert.InRange(assets.PreviewScore!.Value, 0.05, 0.25);
            Assert.Equal("cache/cards/22/poster.webp", assets.PosterPath);
            await AssertRedPosterAsync(root, assets.PosterPath);
            Assert.True(File.Exists(Path.Combine(root, assets.PreviewPath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ShortUniformGifUsesStartAndStillCreatesPoster()
    {
        var root = NewTempRoot();
        try
        {
            var upload = Upload(await GenerateUniformGifAsync(), "uniform.gif", "image/gif");

            var assets = await CreateService(root).SaveMediaAndCreatePreviewAsync(23, upload);

            Assert.Equal(0, assets.PreviewStartSeconds);
            Assert.Equal("fallback", assets.PreviewSelection);
            Assert.NotNull(assets.PreviewScore);
            Assert.True(assets.PreviewScore < 0.25);
            Assert.Equal("cache/cards/23/poster.webp", assets.PosterPath);
            await AssertRedPosterAsync(root, assets.PosterPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RollbackRestoresPreviousPosterAndRemovesReplacementPoster()
    {
        var root = NewTempRoot();
        try
        {
            var previewDirectory = Path.Combine(root, "cache", "cards", "24");
            Directory.CreateDirectory(previewDirectory);
            var oldPoster = Path.Combine(previewDirectory, "poster.webp");
            await File.WriteAllTextAsync(oldPoster, "old poster");
            await File.WriteAllTextAsync(Path.Combine(previewDirectory, "preview.webp"), "old preview");
            var upload = Upload(await GenerateTwoSceneGifAsync(), "replacement.gif", "image/gif");

            var replacement = await CreateService(root).PrepareMediaReplacementAsync(24, upload);
            Assert.Equal("cache/cards/24/poster.webp", replacement.Assets.PosterPath);
            await replacement.PromoteAsync();
            await AssertRedPosterAsync(root, replacement.Assets.PosterPath);

            replacement.Rollback();

            Assert.Equal("old poster", await File.ReadAllTextAsync(oldPoster));
            Assert.Equal("old preview", await File.ReadAllTextAsync(Path.Combine(previewDirectory, "preview.webp")));
            Assert.False(File.Exists(Path.Combine(previewDirectory, "preview.gif")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ManualPreviewStartSelectsRequestedSceneAndResetSelectsAutomaticScene()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root);
            var upload = Upload(await GenerateTwoSceneGifAsync(), "scenes.gif", "image/gif");
            var assets = await service.SaveMediaAndCreatePreviewAsync(25, upload);
            var lenientAuto = await CreateService(root, new() { ["Media:PreviewQualityThreshold"] = "0.05" })
                .PreparePreviewOverrideAsync(25, assets.ContentPath, assets.Duration!.Value, null);
            Assert.Equal("auto", lenientAuto.PreviewSelection);
            lenientAuto.Rollback();
            Assert.True(assets.PreviewStartSeconds > 0);
            await AssertRedPosterAsync(root, assets.PosterPath);

            var manual = await service.PreparePreviewOverrideAsync(25, assets.ContentPath, assets.Duration!.Value, 0);
            Assert.Equal(0, manual.PreviewStartSeconds);
            Assert.Equal("manual", manual.PreviewSelection);
            Assert.Equal("preview.gif", manual.PreviewFileName);
            Assert.Null(manual.PreviewScore);
            await manual.PromoteAsync();
            await AssertBlackPosterAsync(root, assets.PosterPath);
            Assert.True(new FileInfo(Path.Combine(root, assets.PreviewPath)).Length > 0);
            manual.CleanupAfterCommit();

            var automatic = await service.PreparePreviewOverrideAsync(25, assets.ContentPath, assets.Duration.Value, null);
            Assert.True(automatic.PreviewStartSeconds > 0);
            Assert.Equal("fallback", automatic.PreviewSelection); // flat red scene scores below the default 0.25
            Assert.NotNull(automatic.PreviewScore);
            await automatic.PromoteAsync();
            await AssertRedPosterAsync(root, assets.PosterPath);
            Assert.True(new FileInfo(Path.Combine(root, assets.PreviewPath)).Length > 0);
            automatic.CleanupAfterCommit();
            AssertNoTemporaryPreviewFiles(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(6.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task InvalidPreviewStartPreservesExistingPreviewAndPoster(double startSeconds)
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root);
            var upload = Upload(await GenerateTwoSceneGifAsync(), "scenes.gif", "image/gif");
            var assets = await service.SaveMediaAndCreatePreviewAsync(26, upload);
            var previewPath = Path.Combine(root, assets.PreviewPath);
            var posterPath = Path.Combine(root, Assert.IsType<string>(assets.PosterPath));
            var previousPreview = await File.ReadAllBytesAsync(previewPath);
            var previousPoster = await File.ReadAllBytesAsync(posterPath);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.PreparePreviewOverrideAsync(26, assets.ContentPath, assets.Duration!.Value, startSeconds));

            Assert.Equal(previousPreview, await File.ReadAllBytesAsync(previewPath));
            Assert.Equal(previousPoster, await File.ReadAllBytesAsync(posterPath));
            AssertNoTemporaryPreviewFiles(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreviewOverrideRollbackRestoresBothPreviousFilesAndCleansTemporaryFiles()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root);
            var upload = Upload(await GenerateTwoSceneGifAsync(), "scenes.gif", "image/gif");
            var assets = await service.SaveMediaAndCreatePreviewAsync(27, upload);
            var previewPath = Path.Combine(root, assets.PreviewPath);
            var posterPath = Path.Combine(root, Assert.IsType<string>(assets.PosterPath));
            var previousPreview = await File.ReadAllBytesAsync(previewPath);
            var previousPoster = await File.ReadAllBytesAsync(posterPath);

            var replacement = await service.PreparePreviewOverrideAsync(27, assets.ContentPath, assets.Duration!.Value, 0);
            await replacement.PromoteAsync();
            await AssertBlackPosterAsync(root, assets.PosterPath);
            replacement.Rollback();

            Assert.Equal(previousPreview, await File.ReadAllBytesAsync(previewPath));
            Assert.Equal(previousPoster, await File.ReadAllBytesAsync(posterPath));
            AssertNoTemporaryPreviewFiles(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static PreviewService CreateService(string root, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["NODECK_APP_DATA"] = root };
        if (extra is not null)
            foreach (var pair in extra) settings[pair.Key] = pair.Value;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        return new PreviewService(configuration);
    }

    private static async Task<byte[]> GeneratePngAsync(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, Color.Red);
        await using var bytes = new MemoryStream();
        await image.SaveAsPngAsync(bytes);
        return bytes.ToArray();
    }

    private static async Task<byte[]> GenerateAnimatedWebpAsync()
    {
        using var image = new Image<Rgba32>(16, 12, Color.Red);
        image.Frames.RootFrame.Metadata.GetWebpMetadata().FrameDelay = 100;
        foreach (var (color, delay) in new[] { (Color.Blue, 200u), (Color.Green, 300u) })
        {
            using var frame = new Image<Rgba32>(16, 12, color);
            frame.Frames.RootFrame.Metadata.GetWebpMetadata().FrameDelay = delay;
            image.Frames.AddFrame(frame.Frames.RootFrame);
        }
        await using var bytes = new MemoryStream();
        await image.SaveAsWebpAsync(bytes);
        return bytes.ToArray();
    }

    private static void AssertNoCardFiles(string root, long cardId)
    {
        foreach (var dir in new[] { Path.Combine(root, "cards", cardId.ToString()), Path.Combine(root, "cache", "cards", cardId.ToString()) })
            Assert.False(Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(), $"{dir} should be empty or absent.");
    }

    [Fact]
    public async Task AllAnimatedRendersFailingKeepsPosterOnlyUpload()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root);
            service.AnimatedRenderOverride = (_, _, _) => throw new InvalidOperationException("forced");
            var upload = Upload(await GenerateTwoSceneGifAsync(), "scenes.gif", "image/gif");

            var assets = await service.SaveMediaAndCreatePreviewAsync(40, upload);

            Assert.Equal("poster_only", assets.PreviewSelection);
            Assert.Equal("cache/cards/40/poster.webp", assets.PreviewPath);
            Assert.Equal(assets.PosterPath, assets.PreviewPath);
            Assert.True(File.Exists(Path.Combine(root, assets.PosterPath!)));
            Assert.False(File.Exists(Path.Combine(root, "cache", "cards", "40", "preview.gif")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PosterOnlyOverrideReportsPosterFileAndSelection()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root);
            var assets = await service.SaveMediaAndCreatePreviewAsync(41, Upload(await GenerateTwoSceneGifAsync(), "scenes.gif", "image/gif"));
            service.AnimatedRenderOverride = (_, _, _) => throw new InvalidOperationException("forced");

            var pending = await service.PreparePreviewOverrideAsync(41, assets.ContentPath, assets.Duration!.Value, null);

            Assert.Equal("poster_only", pending.PreviewSelection);
            Assert.Equal("poster.webp", pending.PreviewFileName);
            Assert.NotNull(pending.PreviewScore);
            pending.Rollback();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ManualPreviewRenderFailureKeepsOldPreview()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root);
            var assets = await service.SaveMediaAndCreatePreviewAsync(42, Upload(await GenerateTwoSceneGifAsync(), "scenes.gif", "image/gif"));
            var previewPath = Path.Combine(root, assets.PreviewPath);
            var before = await File.ReadAllBytesAsync(previewPath);
            service.AnimatedRenderOverride = (_, _, _) => throw new InvalidOperationException("forced");

            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.PreparePreviewOverrideAsync(42, assets.ContentPath, assets.Duration!.Value, 0));

            Assert.Equal(before, await File.ReadAllBytesAsync(previewPath));
            AssertNoTemporaryPreviewFiles(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PosterFailureAfterAnimationFailureReturnsErrorAndLeavesNoFiles()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root);
            var upload = Upload(await GenerateTwoSceneGifAsync(), "scenes.gif", "image/gif");
            // Make the poster render fail by deleting the original once the animated override runs.
            service.AnimatedRenderOverride = (originalPath, _, _) =>
            {
                File.Delete(originalPath);
                throw new InvalidOperationException("forced");
            };

            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.SaveMediaAndCreatePreviewAsync(43, upload));

            AssertNoCardFiles(root, 43);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImageAbovePixelLimitIsRejectedWithoutLeavingFiles()
    {
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root, new() { ["Media:MaxImagePixels"] = "100" });
            var upload = Upload(await GeneratePngAsync(20, 10), "big.png", "image/png");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SaveMediaAndCreatePreviewAsync(44, upload));

            Assert.Contains("Image is too large: 20x10 exceeds 100 pixels.", exception.Message);
            AssertNoCardFiles(root, 44);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImageAtPixelLimitIsAccepted()
    {
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root, new() { ["Media:MaxImagePixels"] = "200" });
            var assets = await service.SaveMediaAndCreatePreviewAsync(45, Upload(await GeneratePngAsync(20, 10), "ok.png", "image/png"));

            Assert.Equal("image", assets.MediaType);
            Assert.Null(assets.PreviewSelection);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImageReplacementAbovePixelLimitIsRejectedAndLeavesNoFiles()
    {
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root, new() { ["Media:MaxImagePixels"] = "100" });
            var upload = Upload(await GeneratePngAsync(20, 10), "big.png", "image/png");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.PrepareMediaReplacementAsync(46, upload));

            Assert.Contains("Image is too large: 20x10 exceeds 100 pixels.", exception.Message);
            AssertNoCardFiles(root, 46);
            Assert.False(Directory.Exists(Path.Combine(root, "tmp", "replacements")) &&
                Directory.EnumerateFileSystemEntries(Path.Combine(root, "tmp", "replacements")).Any());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnimatedWebpStaysImageWithFrameCountDurationAndFirstFramePreview()
    {
        var root = NewTempRoot();
        try
        {
            var webp = await GenerateAnimatedWebpAsync();
            using (var fixture = Image.Load(webp)) Assert.Equal(3, fixture.Frames.Count);
            var upload = Upload(webp, "anim.webp", "image/webp");

            var assets = await CreateService(root).SaveMediaAndCreatePreviewAsync(47, upload);

            Assert.Equal("image", assets.MediaType);
            Assert.Equal("image/webp", assets.MimeType);
            Assert.True(assets.Animated);
            Assert.Equal(3, assets.FrameCount);
            Assert.Equal(0.6, assets.Duration!.Value, 2);
            Assert.Equal("first_frame", assets.PreviewSelection);
            Assert.True(File.Exists(Path.Combine(root, assets.PreviewPath)));
            using var preview = await Image.LoadAsync(Path.Combine(root, assets.PreviewPath));
            Assert.Single(preview.Frames);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StaticImageHasNoAnimationMetadata()
    {
        var root = NewTempRoot();
        try
        {
            var assets = await CreateService(root).SaveMediaAndCreatePreviewAsync(48, Upload(await GeneratePngAsync(8, 8), "s.png", "image/png"));

            Assert.Null(assets.Animated);
            Assert.Null(assets.FrameCount);
            Assert.Null(assets.PreviewSelection);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MediaProcessingGateBlocksSecondAcquisitionUntilRelease()
    {
        var gate = new MediaProcessingGate(1);
        var first = await gate.AcquireAsync();
        var second = gate.AcquireAsync();

        await Task.Delay(200);
        Assert.False(second.IsCompleted);

        first.Dispose();
        first.Dispose(); // double dispose must not release twice
        var slot = await second.WaitAsync(TimeSpan.FromSeconds(5));
        var third = gate.AcquireAsync();
        await Task.Delay(200);
        Assert.False(third.IsCompleted);
        slot.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task MediaProcessingGateAllowsConfiguredConcurrency()
    {
        var gate = new MediaProcessingGate(2);
        var a = await gate.AcquireAsync();
        var b = await gate.AcquireAsync();
        var c = gate.AcquireAsync();
        await Task.Delay(100);
        Assert.False(c.IsCompleted);
        a.Dispose();
        (await c.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task PreviewServiceSerializesProcessingWhenLimitIsOne()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root, new() { ["Media:MaxConcurrentProcessing"] = "1" });
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            service.AnimatedRenderOverride = async (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("forced");
            };
            var gif = await GenerateTwoSceneGifAsync();

            var firstTask = service.SaveMediaAndCreatePreviewAsync(50, Upload(gif, "a.gif", "image/gif"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var secondTask = service.SaveMediaAndCreatePreviewAsync(51, Upload(gif, "b.gif", "image/gif"));
            await Task.Delay(500);

            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False(secondTask.IsCompleted);

            release.SetResult();
            await firstTask.WaitAsync(TimeSpan.FromSeconds(60));
            await secondTask.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(calls >= 2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ApplyAssetMetadataWritesPreviewKeysAndRemovesThemWhenNull()
    {
        var metadata = new JsonObject
        {
            ["preview_selection"] = "auto",
            ["preview_score"] = 0.5,
            ["frame_rate"] = 30.0,
            ["animated"] = true,
        };
        var empty = new CardFileAssets("c", "p", "image", "image/png", "f.png", 1, 1, 1);

        CardMetadata.ApplyAssetMetadata(metadata, empty);

        foreach (var key in new[] { "preview_selection", "preview_score", "frame_rate", "animated" })
            Assert.False(metadata.ContainsKey(key), key);

        var full = empty with { PreviewSelection = "fallback", PreviewScore = 0.1, FrameRate = 24.5, Animated = true };
        CardMetadata.ApplyAssetMetadata(metadata, full);

        Assert.Equal("fallback", (string?)metadata["preview_selection"]);
        Assert.Equal(0.1, (double?)metadata["preview_score"]);
        Assert.Equal(24.5, (double?)metadata["frame_rate"]);
        Assert.True((bool?)metadata["animated"]);
    }

    [Fact]
    public async Task RotatedPhoneVideoReportsDisplayedSizeAndPortraitPoster()
    {
        if (!VideoToolsAvailable()) return;
        var root = NewTempRoot();
        try
        {
            var plain = Path.Combine(root, "landscape.mov");
            var rotated = Path.Combine(root, "rotated.mov");
            await RunFfmpegAsync("-y", "-f", "lavfi", "-i", "testsrc=s=1280x720:r=10:d=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-shortest",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", plain);
            await RunFfmpegAsync("-y", "-i", plain, "-c", "copy", "-metadata:s:v:0", "rotate=90", rotated);
            var probe = await RunFfprobeAsync("-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=width,height:stream_tags=rotate:stream_side_data=rotation", rotated);
            // Only meaningful when the fixture really carries a display rotation (tag or display matrix).
            if (!probe.Contains("rotat", StringComparison.OrdinalIgnoreCase)) return;
            Assert.Contains("width=1280", probe);

            var rotatedAssets = await CreateService(root).SaveMediaAndCreatePreviewAsync(
                60, Upload(await File.ReadAllBytesAsync(rotated), "rotated.mov", "video/quicktime"));
            var plainAssets = await CreateService(root).SaveMediaAndCreatePreviewAsync(
                61, Upload(await File.ReadAllBytesAsync(plain), "landscape.mov", "video/quicktime"));

            Assert.Equal("video", rotatedAssets.MediaType);
            Assert.Equal(720, rotatedAssets.Width);
            Assert.Equal(1280, rotatedAssets.Height);
            var posterPath = Path.Combine(root, Assert.IsType<string>(rotatedAssets.PosterPath));
            Assert.True(File.Exists(posterPath), "Poster file should exist.");
            using (var poster = await Image.LoadAsync<Rgba32>(posterPath))
                Assert.True(poster.Height > poster.Width, $"Poster should be portrait, got {poster.Width}x{poster.Height}.");

            Assert.Equal(1280, plainAssets.Width);
            Assert.Equal(720, plainAssets.Height);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task RunFfmpegAsync(params string[] arguments) => RunToolAsync("ffmpeg", arguments);

    private static Task<string> RunFfprobeAsync(params string[] arguments) =>
        RunToolAsync("ffprobe", arguments);

    private static async Task<string> RunToolAsync(string tool, string[] arguments)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{tool} failed: {await stderr}");
        return stdout;
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nodeck-preview-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static FormFile Upload(byte[] contents, string fileName, string contentType)
    {
        var stream = new MemoryStream(contents);
        return new FormFile(stream, 0, contents.Length, "media", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private static bool VideoToolsAvailable() => CanRun("ffmpeg") && CanRun("ffprobe");

    private static bool CanRun(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task GenerateVideoAsync(string path, string format)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-y", "-f", "lavfi", "-i", "color=c=blue:s=32x24:r=4:d=1", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-shortest" })
            start.ArgumentList.Add(argument);
        foreach (var argument in format == "mov"
            ? new[] { "-c:v", "mpeg4", "-c:a", "pcm_s16le", "-f", "mov" }
            : new[] { "-c:v", "libvpx", "-c:a", "libvorbis", "-f", "webm" })
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add(path);

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"ffmpeg could not generate the {format} fixture: {await stderr}");
    }

    private static async Task<byte[]> GenerateTwoSceneGifAsync()
    {
        using var gif = new Image<Rgba32>(32, 24, Color.Black);
        gif.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = 10;
        using var red = new Image<Rgba32>(32, 24, Color.Red);
        red.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = 10;
        for (var index = 1; index < 20; index++)
            gif.Frames.AddFrame(gif.Frames.RootFrame);
        for (var index = 0; index < 40; index++)
            gif.Frames.AddFrame(red.Frames.RootFrame);
        await using var bytes = new MemoryStream();
        await gif.SaveAsGifAsync(bytes);
        return bytes.ToArray();
    }

    private static async Task<byte[]> GenerateUniformGifAsync()
    {
        using var gif = new Image<Rgba32>(32, 24, Color.Red);
        gif.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = 10;
        gif.Frames.AddFrame(gif.Frames.RootFrame);
        await using var bytes = new MemoryStream();
        await gif.SaveAsGifAsync(bytes);
        return bytes.ToArray();
    }

    private static async Task<byte[]> GenerateGifWithDifferentDelaysAsync()
    {
        using var gif = new Image<Rgba32>(8, 8, Color.Red);
        gif.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = 7;
        using var blue = new Image<Rgba32>(8, 8, Color.Blue);
        blue.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = 13;
        using var green = new Image<Rgba32>(8, 8, Color.Green);
        green.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = 25;
        gif.Frames.AddFrame(blue.Frames.RootFrame);
        gif.Frames.AddFrame(green.Frames.RootFrame);
        await using var bytes = new MemoryStream();
        await gif.SaveAsGifAsync(bytes);
        return bytes.ToArray();
    }

    private static async Task AssertRedPosterAsync(string root, string? relativePath)
    {
        var posterPath = Path.Combine(root, Assert.IsType<string>(relativePath));
        Assert.True(File.Exists(posterPath), "Poster file should exist.");
        using var poster = await Image.LoadAsync<Rgba32>(posterPath);
        var pixel = poster[poster.Width / 2, poster.Height / 2];
        Assert.True(pixel.R > 150 && pixel.G < 80 && pixel.B < 80,
            $"Expected a red scene in the poster, got RGB({pixel.R}, {pixel.G}, {pixel.B}).");
    }

    private static async Task AssertBlackPosterAsync(string root, string? relativePath)
    {
        var posterPath = Path.Combine(root, Assert.IsType<string>(relativePath));
        Assert.True(File.Exists(posterPath), "Poster file should exist.");
        using var poster = await Image.LoadAsync<Rgba32>(posterPath);
        var pixel = poster[poster.Width / 2, poster.Height / 2];
        Assert.True(pixel.R < 80 && pixel.G < 80 && pixel.B < 80,
            $"Expected a black scene in the poster, got RGB({pixel.R}, {pixel.G}, {pixel.B}).");
    }

    private static void AssertNoTemporaryPreviewFiles(string root)
    {
        foreach (var directoryName in new[] { "preview-overrides", "preview-override-backups" })
        {
            var directory = Path.Combine(root, "tmp", directoryName);
            Assert.False(Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(),
                $"Preview override should clean its {directoryName} directory.");
        }
    }

    private static async Task GenerateTwoSceneVideoAsync(string path)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "-y", "-f", "lavfi", "-i", "color=c=black:s=32x24:r=10:d=2",
            "-f", "lavfi", "-i", "color=c=red:s=32x24:r=10:d=4",
            "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]", "-map", "[v]",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", path,
        })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"ffmpeg could not generate the two-scene fixture: {await stderr}");
    }
}

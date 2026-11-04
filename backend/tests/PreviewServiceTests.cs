using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Cards;
using MyApp.Api.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
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

            Assert.Equal("video", assets.MediaType);
            Assert.True(assets.PreviewStartSeconds > 0, "Preview should skip the dark introduction.");
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
            Assert.True(assets.PreviewStartSeconds > 0);
            await AssertRedPosterAsync(root, assets.PosterPath);

            var manual = await service.PreparePreviewOverrideAsync(25, assets.ContentPath, assets.Duration!.Value, 0);
            Assert.Equal(0, manual.PreviewStartSeconds);
            await manual.PromoteAsync();
            await AssertBlackPosterAsync(root, assets.PosterPath);
            Assert.True(new FileInfo(Path.Combine(root, assets.PreviewPath)).Length > 0);
            manual.CleanupAfterCommit();

            var automatic = await service.PreparePreviewOverrideAsync(25, assets.ContentPath, assets.Duration.Value, null);
            Assert.True(automatic.PreviewStartSeconds > 0);
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

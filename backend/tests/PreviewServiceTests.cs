using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Cards;
using MyApp.Api.Storage;
using SixLabors.ImageSharp;
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
}

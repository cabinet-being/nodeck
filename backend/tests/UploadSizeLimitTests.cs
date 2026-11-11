using System.Net;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace MyApp.Api.Tests;

public sealed class UploadSizeLimitTests
{
    // Random noise does not compress, so the PNG is about 3 bytes per pixel: ~41 MB, above
    // Kestrel's default 30 MB request limit that used to reject ordinary video uploads.
    private static byte[] LargeNoisePng()
    {
        const int side = 3700;
        var random = new Random(7);
        var pixels = new byte[side * side * 3];
        random.NextBytes(pixels);
        using var image = Image.LoadPixelData<Rgb24>(pixels, side, side);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream, new PngEncoder { CompressionLevel = PngCompressionLevel.NoCompression });
        return stream.ToArray();
    }

    [Fact]
    public async Task UploadAboveKestrelDefaultLimitIsAccepted()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING"))) return;
        var bytes = LargeNoisePng();
        Assert.True(bytes.Length > 35_000_000, $"Test image is only {bytes.Length} bytes.");

        await using var host = await ApiTestHost.CreateAsync();
        using var response = await host.PostMediaAsync(bytes, "large.png", "Large upload");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task UploadAboveConfiguredLimitIsRejected()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING"))) return;
        var bytes = new byte[2 * 1024 * 1024 + 1];

        await using var host = await ApiTestHost.CreateAsync(
            new Dictionary<string, string?> { ["NODECK_MAX_UPLOAD_MB"] = "1" });
        using var response = await host.PostMediaAsync(bytes, "too-large.png", "Too large");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}

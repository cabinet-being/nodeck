using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MyApp.Api.Storage;

internal static class PreviewSelection
{
    internal static double Score(Image<Rgb24> image)
    {
        var width = image.Width;
        var height = image.Height;
        var values = new double[width * height];
        double sum = 0;
        double sumSquares = 0;
        double colorful = 0;
        var occupied = 0;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var pixel = image[x, y];
            var luminance = (0.2126 * pixel.R + 0.7152 * pixel.G + 0.0722 * pixel.B) / 255.0;
            values[y * width + x] = luminance;
            sum += luminance;
            sumSquares += luminance * luminance;
            colorful += (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B))) / 255.0;
            if (luminance is > 0.08 and < 0.92) occupied++;
        }

        var count = width * height;
        var mean = sum / count;
        var contrast = Math.Sqrt(Math.Max(0, sumSquares / count - mean * mean));
        double edges = 0;
        for (var y = 0; y < height - 1; y++)
        for (var x = 0; x < width - 1; x++)
        {
            var index = y * width + x;
            edges += Math.Abs(values[index] - values[index + 1]);
            edges += Math.Abs(values[index] - values[index + width]);
        }

        var sharpness = edges / (2 * (width - 1) * (height - 1));
        var exposure = Math.Min(1, Math.Min(mean, 1 - mean) / 0.22);
        return exposure * (0.55 * Math.Min(1, contrast / 0.20)
            + 0.35 * Math.Min(1, sharpness / 0.09)
            + 0.10 * colorful / count)
            * (0.25 + 0.75 * occupied / count);
    }
}

using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MyApp.Api.Duplicates;

/// <summary>
/// 64-bit difference hash (dHash): the image is reduced to 9×8 grey pixels and each bit says whether a pixel
/// is brighter than its right neighbour. Resized, re-encoded or slightly recompressed copies of one picture
/// get hashes a few bits apart.
/// </summary>
public static class PerceptualHash
{
    /// <summary>Hashes at most this many bits apart are listed as similar.</summary>
    public const int SimilarDistance = 4;

    public static async Task<long?> ComputeAsync(string path, CancellationToken cancellationToken = default)
    {
        using var image = await Image.LoadAsync<L8>(path, cancellationToken);
        return Compute(image);
    }

    /// <summary>Returns null for flat images (all bits equal), which would match every other flat image.</summary>
    public static long? Compute(Image<L8> image)
    {
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(9, 8),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Box,
        }));
        ulong hash = 0;
        var bit = 0;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < 8; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < 8; x++, bit++)
                    if (row[x].PackedValue > row[x + 1].PackedValue) hash |= 1UL << bit;
            }
        });
        return hash is 0 or ulong.MaxValue ? null : unchecked((long)hash);
    }

    public static int Distance(long a, long b) => BitOperations.PopCount(unchecked((ulong)(a ^ b)));
}

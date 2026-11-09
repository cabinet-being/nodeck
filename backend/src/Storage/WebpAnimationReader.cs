using System.Buffers.Binary;

namespace MyApp.Api.Storage;

internal static class WebpAnimationReader
{
    private const int MaxFrames = 100_000;

    /// <summary>Walks the RIFF/WebP chunks; returns frame count and total duration (seconds), or null when not animated.</summary>
    internal static (int FrameCount, double Duration)? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var length = stream.Length;
            Span<byte> header = stackalloc byte[12];
            if (stream.Read(header) != 12 || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WEBP"u8))
                return null;

            var animatedFlag = false;
            var frames = 0;
            long totalMs = 0;
            long position = 12;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> anmf = stackalloc byte[16];
            while (position + 8 <= length && frames < MaxFrames)
            {
                stream.Position = position;
                if (stream.Read(chunk) != 8) break;
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                var payloadStart = position + 8;
                if (payloadStart + size > length) break;

                if (chunk[..4].SequenceEqual("VP8X"u8) && size >= 1)
                {
                    var flags = stream.ReadByte();
                    animatedFlag = flags >= 0 && (flags & 0x02) != 0;
                }
                else if (chunk[..4].SequenceEqual("ANMF"u8) && size >= 16)
                {
                    if (stream.Read(anmf) != 16) break;
                    totalMs += anmf[12] | (anmf[13] << 8) | (anmf[14] << 16);
                    frames++;
                }

                position = payloadStart + size + (size & 1);
            }

            return animatedFlag && frames > 1 ? (frames, totalMs / 1000.0) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

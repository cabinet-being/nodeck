namespace MyApp.Api.Storage;

internal static class GifMetadataReader
{
    private const long MaxGifFramePixels = 16_777_216;

    internal static GifProbeResult ReadGifMetadata(string gifPath)
    {
        try
        {
            using var stream = File.OpenRead(gifPath);
            Span<byte> header = stackalloc byte[13];
            stream.ReadExactly(header);
            if (!header[..6].SequenceEqual("GIF87a"u8) && !header[..6].SequenceEqual("GIF89a"u8))
                throw new InvalidOperationException("Uploaded file is not a valid GIF.");

            var width = header[6] | header[7] << 8;
            var height = header[8] | header[9] << 8;
            if (width == 0 || height == 0)
                throw new InvalidOperationException("Uploaded GIF has invalid dimensions.");
            if ((long)width * height > MaxGifFramePixels)
                throw new InvalidOperationException($"Uploaded GIF exceeds the {MaxGifFramePixels:N0}-pixel frame limit.");

            var hasGlobalColorTable = (header[10] & 0x80) != 0;
            if (hasGlobalColorTable)
                SkipGifBytes(stream, 3L * (1 << ((header[10] & 0x07) + 1)));

            var frameCount = 0;
            var duration = 0.0;
            var pendingDelay = 0;
            Span<byte> descriptor = stackalloc byte[9];
            while (true)
            {
                switch (ReadGifByte(stream))
                {
                    case 0x3B: // Trailer
                        if (frameCount == 0)
                            throw new InvalidOperationException("Uploaded GIF contains no image frames.");
                        return new GifProbeResult(width, height, frameCount, duration);

                    case 0x21: // Extension
                    {
                        var label = ReadGifByte(stream);
                        if (label == 0xF9) // Graphic control extension
                        {
                            if (ReadGifByte(stream) != 4)
                                throw new InvalidOperationException("Uploaded GIF has an invalid graphic control block.");
                            ReadGifByte(stream); // Disposal and transparency flags
                            pendingDelay = ReadGifByte(stream) | ReadGifByte(stream) << 8;
                            ReadGifByte(stream); // Transparent color index
                            if (ReadGifByte(stream) != 0)
                                throw new InvalidOperationException("Uploaded GIF has an invalid graphic control block.");
                        }
                        else
                        {
                            SkipGifSubBlocks(stream);
                            if (label == 0x01) // Plain text consumes graphic control settings
                                pendingDelay = 0;
                        }
                        break;
                    }

                    case 0x2C: // Image descriptor
                    {
                        stream.ReadExactly(descriptor);
                        var frameWidth = descriptor[4] | descriptor[5] << 8;
                        var frameHeight = descriptor[6] | descriptor[7] << 8;
                        if (frameWidth == 0 || frameHeight == 0 ||
                            (long)frameWidth * frameHeight > MaxGifFramePixels)
                            throw new InvalidOperationException("Uploaded GIF has invalid or excessive frame dimensions.");
                        var hasLocalColorTable = (descriptor[8] & 0x80) != 0;
                        if (!hasGlobalColorTable && !hasLocalColorTable)
                            throw new InvalidOperationException("Uploaded GIF image has no color table.");
                        if (hasLocalColorTable)
                            SkipGifBytes(stream, 3L * (1 << ((descriptor[8] & 0x07) + 1)));
                        var codeSize = ReadGifByte(stream);
                        if (codeSize is < 2 or > 8)
                            throw new InvalidOperationException("Uploaded GIF has an invalid image data code size.");
                        if (!SkipGifSubBlocks(stream))
                            throw new InvalidOperationException("Uploaded GIF image has no data.");
                        frameCount = checked(frameCount + 1);
                        duration += pendingDelay / 100.0;
                        pendingDelay = 0;
                        break;
                    }

                    default:
                        throw new InvalidOperationException("Uploaded GIF contains an invalid block.");
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidOperationException("Uploaded GIF is truncated.", exception);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Uploaded GIF could not be read.", exception);
        }
    }

    internal static int ReadGifByte(Stream stream)
    {
        var value = stream.ReadByte();
        if (value < 0)
            throw new EndOfStreamException();
        return value;
    }

    internal static void SkipGifBytes(Stream stream, long count)
    {
        if (count > stream.Length - stream.Position)
            throw new EndOfStreamException();
        stream.Seek(count, SeekOrigin.Current);
    }

    internal static bool SkipGifSubBlocks(Stream stream)
    {
        var hasData = false;
        int size;
        while ((size = ReadGifByte(stream)) != 0)
        {
            hasData = true;
            SkipGifBytes(stream, size);
        }
        return hasData;
    }

    internal sealed record GifProbeResult(int Width, int Height, int FrameCount, double Duration);
}

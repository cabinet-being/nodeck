using System.Globalization;
using System.Text.Json;
using static MyApp.Api.Storage.MediaProcess;

namespace MyApp.Api.Storage;

internal static class VideoProbe
{
    internal static async Task<VideoProbeResult> ProbeVideoAsync(string videoPath)
    {
        var result = await RunProcessAsync("ffprobe", [
            "-v",
            "error",
            "-print_format",
            "json",
            "-show_entries",
            "format=format_name,duration:stream=codec_type,codec_name,pix_fmt,width,height,channels,sample_rate,avg_frame_rate,r_frame_rate",
            videoPath,
        ]);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Unsupported or unreadable media content. Use JPEG, PNG, WebP, GIF, MP4, MOV, or WebM.");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var formatName = root.GetProperty("format").GetProperty("format_name").GetString() ?? "";
        var container = await DetectVideoContainerAsync(videoPath, formatName);
        var mimeType = container switch
        {
            "mp4" => "video/mp4",
            "mov" => "video/quicktime",
            "webm" => "video/webm",
            _ => null,
        };

        if (mimeType is null)
        {
            throw new InvalidOperationException("Unsupported video format. Only MP4, MOV, and WebM are supported.");
        }

        var durationText = root.GetProperty("format").TryGetProperty("duration", out var durationElement)
            ? durationElement.GetString()
            : null;

        if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            || duration <= 0)
        {
            throw new InvalidOperationException("Uploaded video has no readable duration.");
        }

        var width = 0;
        var height = 0;
        var hasAudio = false;
        string? videoCodec = null;
        string? audioCodec = null;
        string? pixelFormat = null;
        int? audioChannels = null;
        int? audioSampleRate = null;
        double? frameRate = null;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var codecType = stream.GetProperty("codec_type").GetString();
            if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase))
            {
                hasAudio = true;
                audioCodec ??= stream.TryGetProperty("codec_name", out var audioCodecElement) ? audioCodecElement.GetString() : null;
                audioChannels ??= stream.TryGetProperty("channels", out var channelsElement) && channelsElement.TryGetInt32(out var channels) ? channels : null;
                audioSampleRate ??= stream.TryGetProperty("sample_rate", out var sampleRateElement)
                    && int.TryParse(sampleRateElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleRate)
                    ? sampleRate : null;
                continue;
            }
            if (!string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase))
                continue;

            videoCodec ??= stream.TryGetProperty("codec_name", out var videoCodecElement) ? videoCodecElement.GetString() : null;
            pixelFormat ??= stream.TryGetProperty("pix_fmt", out var pixelFormatElement) ? pixelFormatElement.GetString() : null;

            if (frameRate is null)
                frameRate = ParseFrameRate(stream, "avg_frame_rate") ?? ParseFrameRate(stream, "r_frame_rate");

            var streamWidth = stream.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : 0;
            var streamHeight = stream.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : 0;

            if (streamWidth > 0 && streamHeight > 0 && width == 0)
            {
                width = streamWidth;
                height = streamHeight;
            }
        }

        if (width > 0 && height > 0)
            return new VideoProbeResult(mimeType, container!, width, height, duration, hasAudio, videoCodec, audioCodec, pixelFormat, audioChannels, audioSampleRate, frameRate);

        throw new InvalidOperationException("Uploaded file does not contain a readable video stream.");
    }

    internal static double? ParseFrameRate(JsonElement stream, string property)
    {
        if (!stream.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) return null;
        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)) return null;
        var denominator = 1.0;
        if (parts.Length == 2 &&
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator)) return null;
        if (parts.Length > 2 || !double.IsFinite(numerator) || numerator <= 0 || !double.IsFinite(denominator) || denominator <= 0)
            return null;
        var rate = numerator / denominator;
        return double.IsFinite(rate) && rate > 0 ? rate : null;
    }

    internal static async Task<string?> DetectVideoContainerAsync(string path, string formatName)
    {
        var formats = formatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = new byte[4096];
        await using var stream = File.OpenRead(path);
        var count = await stream.ReadAsync(header);
        var bytes = header.AsSpan(0, count);
        if (formats.Contains("webm") || formats.Contains("matroska"))
            return bytes.StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }) && bytes.IndexOf("webm"u8) >= 0 ? "webm" : null;
        if (!formats.Any(format => format is "mp4" or "mov" or "m4a" or "3gp" or "3g2" or "mj2"))
            return null;
        if (bytes.Length < 12 || !bytes[4..8].SequenceEqual("ftyp"u8)) return null;
        return bytes[8..12].SequenceEqual("qt  "u8) ? "mov" : "mp4";
    }

    internal static bool IsBrowserCompatible(VideoProbeResult probe)
    {
        return probe.Container switch
        {
            "mp4" => probe.VideoCodec == "h264" && probe.PixelFormat == "yuv420p" && (!probe.HasAudio || probe.AudioCodec == "aac"),
            "webm" => (probe.VideoCodec is "vp8" or "vp9") && probe.PixelFormat == "yuv420p" && (!probe.HasAudio || probe.AudioCodec is "opus" or "vorbis"),
            _ => false,
        };
    }

    internal sealed record VideoProbeResult(string MimeType, string Container, int Width, int Height, double Duration, bool HasAudio, string? VideoCodec, string? AudioCodec, string? PixelFormat, int? AudioChannels, int? AudioSampleRate, double? FrameRate = null);
}

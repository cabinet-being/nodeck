using System.Text.Json.Nodes;

namespace MyApp.Api.Cards;

internal static class CardMetadata
{
    internal static JsonObject CreateMetadata() =>
        new()
        {
            ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };

    internal static void ApplyAssetMetadata(JsonObject metadata, CardFileAssets assets)
    {
        metadata["media_type"] = assets.MediaType;
        metadata["mime_type"] = assets.MimeType;
        metadata["original_file_name"] = assets.OriginalFileName;
        metadata["content_path"] = assets.ContentPath;
        metadata["preview_path"] = assets.PreviewPath;
        metadata["file_size"] = assets.FileSize;
        metadata["width"] = assets.Width;
        metadata["height"] = assets.Height;
        metadata["has_audio"] = assets.HasAudio;
        metadata["container"] = assets.Container;
        metadata["video_codec"] = assets.VideoCodec;
        metadata["audio_codec"] = assets.AudioCodec;
        metadata["audio_channels"] = assets.AudioChannels;
        metadata["audio_sample_rate"] = assets.AudioSampleRate;
        metadata["playback_path"] = assets.PlaybackPath;
        metadata["poster_path"] = assets.PosterPath;
        metadata["preview_start_seconds"] = assets.PreviewStartSeconds;
        metadata["preview_start_manual"] = false;

        if (assets.Duration is not null)
        {
            metadata["duration"] = assets.Duration.Value;
        }
        else
        {
            metadata.Remove("duration");
        }

        SetOrRemove(metadata, "preview_selection", assets.PreviewSelection is null ? null : JsonValue.Create(assets.PreviewSelection));
        SetOrRemove(metadata, "preview_score", assets.PreviewScore is null ? null : JsonValue.Create(assets.PreviewScore.Value));
        SetOrRemove(metadata, "frame_rate", assets.FrameRate is null ? null : JsonValue.Create(assets.FrameRate.Value));
        SetOrRemove(metadata, "animated", assets.Animated is null ? null : JsonValue.Create(assets.Animated.Value));

        if (assets.FrameCount is not null)
        {
            metadata["frame_count"] = assets.FrameCount.Value;
        }
        else
        {
            metadata.Remove("frame_count");
        }
    }

    internal static void SetOrRemove(JsonObject metadata, string key, JsonNode? value)
    {
        if (value is null) metadata.Remove(key);
        else metadata[key] = value;
    }

    internal static JsonNode ParseRequiredJson(string json) =>
        JsonNode.Parse(json) ?? new JsonObject();

    internal static JsonObject ParseRequiredJsonObject(string json) =>
        JsonNode.Parse(json) as JsonObject ?? new JsonObject();

    internal static JsonNode? ParseOptionalJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
}

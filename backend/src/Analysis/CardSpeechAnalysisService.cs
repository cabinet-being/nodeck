using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using MySqlConnector;

namespace MyApp.Api.Analysis;

/// <summary>Prepares speech results from original video audio without retaining a transcript.</summary>
public sealed class CardSpeechAnalysisService
{
    private readonly string _connectionString;
    private readonly string _dataRoot;
    private readonly ISpeechRecognizer _recognizer;

    public CardSpeechAnalysisService(IConfiguration configuration, ISpeechRecognizer recognizer)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
        _dataRoot = configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _recognizer = recognizer;
    }

    public async Task<PreparedCardSpeechAnalysis> PrepareAsync(long cardId,
        CancellationToken cancellationToken = default)
    {
        if (cardId <= 0) throw new ArgumentOutOfRangeException(nameof(cardId));
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var asset = await ReadAssetAsync(connection, cardId, cancellationToken);
        var originalPath = ResolveOriginalPath(cardId, asset.ContentPath);
        if (!File.Exists(originalPath))
            throw new InvalidOperationException("The card's original video is missing.");

        var originalHash = await HashFileAsync(originalPath, cancellationToken);
        var result = await _recognizer.RecognizeAsync(originalPath, asset.DurationSeconds, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ProcessedSeconds is < 0 or > 600 ||
            result.ProcessedSeconds > (decimal)Math.Min(asset.DurationSeconds, 600) + 0.1m ||
            (result.EnglishSpeechDetected == true && result.SpeechDetected != true))
            throw new InvalidOperationException("The speech recognizer returned an invalid result.");

        var latest = await ReadAssetAsync(connection, cardId, cancellationToken);
        if (latest != asset ||
            !originalHash.SequenceEqual(await HashFileAsync(originalPath, cancellationToken)))
            throw new InvalidOperationException("The card media changed during speech analysis. Run analysis again.");

        // Segment text is transient. A future reviewed tag matcher may turn it into bounded tag IDs.
        return new PreparedCardSpeechAnalysis(asset.Metadata, result.SpeechDetected,
            result.EnglishSpeechDetected, result.Language, result.ProcessedSeconds,
            result.Truncated, result.ModelKey, EvidenceJson: null);
    }

    private static async Task<CardAudioAsset> ReadAssetAsync(MySqlConnection connection,
        long cardId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<CardRow>(new CommandDefinition(
            "SELECT type, metadata FROM cards WHERE id = @CardId;", new { CardId = cardId },
            cancellationToken: cancellationToken));
        if (row is null) throw new InvalidOperationException("Card not found.");
        if (row.Type != "media")
            throw new InvalidOperationException("Only media cards can be analyzed for speech.");

        using var metadata = JsonDocument.Parse(row.Metadata);
        var root = metadata.RootElement;
        if (!root.TryGetProperty("media_type", out var type) || type.GetString() != "video" ||
            !root.TryGetProperty("has_audio", out var audio) || audio.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Speech analysis requires a video with an audio stream.");
        var content = root.TryGetProperty("content_path", out var path) ? path.GetString() : null;
        if (string.IsNullOrWhiteSpace(content) ||
            !root.TryGetProperty("duration", out var duration) ||
            duration.ValueKind != JsonValueKind.Number ||
            !duration.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds <= 0)
            throw new InvalidOperationException("The video path or duration is missing or invalid.");
        return new CardAudioAsset(content, seconds, row.Metadata);
    }

    private string ResolveOriginalPath(long cardId, string relativePath)
    {
        var expectedPrefix = $"cards/{cardId}/original.";
        if (!relativePath.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            relativePath[expectedPrefix.Length..] is not ("mp4" or "mov" or "webm"))
            throw new InvalidOperationException("The card has an unexpected video path.");
        var root = Path.GetFullPath(_dataRoot);
        var path = Path.GetFullPath(Path.Combine(root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new InvalidOperationException("The card video path escapes the application data directory.");
        return path;
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return await SHA256.HashDataAsync(file, cancellationToken);
    }

    private sealed record CardAudioAsset(string ContentPath, double DurationSeconds, string Metadata);
    private sealed class CardRow
    {
        public string Type { get; set; } = "";
        public string Metadata { get; set; } = "{}";
    }
}

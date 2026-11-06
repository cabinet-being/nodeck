using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using MyApp.Api.Catalog;
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

        var (proposals, evidenceJson) = await MatchSpeechTagsAsync(connection, result, cancellationToken);

        var latest = await ReadAssetAsync(connection, cardId, cancellationToken);
        if (latest != asset ||
            !originalHash.SequenceEqual(await HashFileAsync(originalPath, cancellationToken)))
            throw new InvalidOperationException("The card media changed during speech analysis. Run analysis again.");

        return new PreparedCardSpeechAnalysis(asset.Metadata, result.SpeechDetected,
            result.EnglishSpeechDetected, result.Language, result.ProcessedSeconds,
            result.Truncated, result.ModelKey, evidenceJson, proposals);
    }

    private static async Task<(IReadOnlyList<TagSuggestionProposal> Proposals, string? EvidenceJson)>
        MatchSpeechTagsAsync(MySqlConnection connection, SpeechRecognitionResult result,
            CancellationToken cancellationToken)
    {
        if (result.EnglishSpeechDetected != true) return ([], null);
        var candidates = SpeechTagMatcher.FindCandidates(result.Segments);
        if (candidates.Count == 0) return ([], null);
        var names = candidates.SelectMany(item => new[] { item.Phrase,
                item.Phrase.Replace(' ', '_') })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rows = (await connection.QueryAsync<SpeechTagRow>(new CommandDefinition("""
            SELECT t.id AS TagId, t.name AS MatchName
            FROM tags t
            WHERE t.name IN @Names AND t.legacy_card_id IS NULL AND t.name_conflict = FALSE
              AND ((t.origin = 'danbooru' AND t.category = 'general') OR
                   (t.origin = 'user' AND t.category = 'custom' AND EXISTS (
                       SELECT 1 FROM card_tags ct WHERE ct.tag_id = t.id
                         AND ct.assignment_source IN ('manual', 'confirmed'))))
            UNION ALL
            SELECT t.id AS TagId, a.alias AS MatchName
            FROM tag_aliases a JOIN tags t ON t.id = a.tag_id
            WHERE a.alias IN @Names AND t.legacy_card_id IS NULL AND t.name_conflict = FALSE
              AND ((t.origin = 'danbooru' AND t.category = 'general') OR
                   (t.origin = 'user' AND t.category = 'custom' AND EXISTS (
                       SELECT 1 FROM card_tags ct WHERE ct.tag_id = t.id
                         AND ct.assignment_source IN ('manual', 'confirmed'))));
            """, new { Names = names }, cancellationToken: cancellationToken))).ToArray();

        var byPhrase = candidates.GroupBy(item => item.Phrase, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var matched = rows.Select(row => new
            {
                row.TagId,
                Phrase = NormalizeCatalogPhrase(row.MatchName)
            })
            .Where(row => byPhrase.ContainsKey(row.Phrase))
            .GroupBy(row => row.TagId)
            .Select(group =>
            {
                var occurrences = group.SelectMany(row => byPhrase[row.Phrase])
                    .Distinct().OrderBy(item => item.StartSeconds).ToArray();
                var longest = occurrences.Max(item => item.WordCount);
                var confidence = Math.Min(0.72m,
                    0.52m + 0.04m * (longest - 2) + 0.02m * Math.Min(3, occurrences.Length - 1));
                return new { TagId = group.Key, Confidence = confidence,
                    First = occurrences[0] };
            })
            .OrderByDescending(item => item.Confidence).ThenBy(item => item.TagId)
            .Take(10).ToArray();
        if (matched.Length == 0) return ([], null);

        var proposals = matched.Select(item => new TagSuggestionProposal(item.TagId,
            item.Confidence, SpeechAnalysisQueueRepository.SpeechProposalModelKey)).ToArray();
        var evidence = matched.Select(item => new
        {
            tag_id = item.TagId,
            rule_id = "exact-phrase-v1",
            start = item.First.StartSeconds,
            end = item.First.EndSeconds
        });
        return (proposals, JsonSerializer.Serialize(evidence));
    }

    private static string NormalizeCatalogPhrase(string name) => string.Join(" ",
        name.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries)).ToLowerInvariant();

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
    private sealed class SpeechTagRow
    {
        public long TagId { get; set; }
        public string MatchName { get; set; } = "";
    }
    private sealed class CardRow
    {
        public string Type { get; set; } = "";
        public string Metadata { get; set; } = "{}";
    }
}

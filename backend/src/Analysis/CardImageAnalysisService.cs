using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using MyApp.Api.Catalog;
using MySqlConnector;

namespace MyApp.Api.Analysis;

public sealed record CardImageAnalysisResult(
    long CardId, int Predictions, int MatchedTags, int SubmittedSuggestions);
public sealed record PreparedCardImageAnalysis(
    CardImageAnalysisResult Result, IReadOnlyList<TagSuggestionProposal> Proposals, string Metadata,
    VisualFeatureVector? Features = null, string? FeatureModelKey = null);

/// <summary>Runs uploaded media through a local visual tagger outside the upload transaction.</summary>
public sealed class CardImageAnalysisService
{
    private const int MaxSuggestions = 40;
    private readonly string _connectionString;
    private readonly string _dataRoot;
    private readonly CardCatalogRepository _catalog;
    private readonly IVisualTagger _tagger;
    private readonly IVideoFrameSampler _sampler;

    public CardImageAnalysisService(
        IConfiguration configuration, CardCatalogRepository catalog, IVisualTagger tagger,
        IVideoFrameSampler? sampler = null)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
        _dataRoot = configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _catalog = catalog;
        _tagger = tagger;
        _sampler = sampler ?? new FfmpegVideoFrameSampler();
    }

    public async Task<CardImageAnalysisResult> AnalyzeAsync(long cardId, CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(cardId, cancellationToken);
        await _catalog.UpsertSuggestionsAsync(cardId, prepared.Proposals);
        return prepared.Result;
    }

    public async Task<PreparedCardImageAnalysis> PrepareAsync(long cardId,
        CancellationToken cancellationToken = default)
    {
        if (cardId <= 0) throw new ArgumentOutOfRangeException(nameof(cardId));
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var asset = await ReadAssetAsync(connection, cardId, cancellationToken);
        var originalPath = ResolveAssetPath(cardId, asset.ContentPath, asset.MediaType, isPreview: false);
        var previewPath = ResolveAssetPath(cardId, asset.PreviewPath, asset.MediaType, isPreview: true);
        if (!File.Exists(originalPath) || !File.Exists(previewPath))
            throw new InvalidOperationException("The card's original media or preview is missing.");

        var originalHash = await HashFileAsync(originalPath, cancellationToken);
        var previewHash = asset.MediaType == "image"
            ? await HashFileAsync(previewPath, cancellationToken) : null;
        var analysis = asset.MediaType == "image"
            ? _tagger.Analyze(previewPath)
            : await PredictAnimatedAsync(originalPath, asset.DurationSeconds!.Value, cancellationToken);
        var predictions = analysis.Predictions;
        cancellationToken.ThrowIfCancellationRequested();
        if (predictions.Any(item => item.DanbooruId <= 0 || item.Confidence is < 0 or > 1))
            throw new InvalidOperationException("The visual tagger returned an invalid prediction.");

        var candidates = predictions
            .Where(item => CategoryName(item.Category) is not null)
            .GroupBy(item => item.DanbooruId)
            .Select(group => group.OrderByDescending(item => item.Confidence).First())
            .ToArray();
        var ids = candidates.Select(item => item.DanbooruId).ToArray();
        ImportedTag[] imported = ids.Length == 0 ? Array.Empty<ImportedTag>() : (await connection.QueryAsync<ImportedTag>(new CommandDefinition("""
            SELECT id, danbooru_id AS DanbooruId, category
            FROM tags WHERE origin = 'danbooru' AND danbooru_id IN @Ids;
            """, new { Ids = ids }, cancellationToken: cancellationToken))).ToArray();
        var byExternalId = imported.ToDictionary(item => item.DanbooruId);
        var matched = candidates
            .Where(item => byExternalId.TryGetValue(item.DanbooruId, out var tag) &&
                tag.Category == CategoryName(item.Category))
            .ToArray();
        var proposals = matched
            .OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.DanbooruId)
            .Take(MaxSuggestions)
            .Select(item => new TagSuggestionProposal(
                byExternalId[item.DanbooruId].Id, item.Confidence, _tagger.ModelKey))
            .ToArray();

        // A replacement can keep the same relative path, so compare bytes as well as metadata.
        var latest = await ReadAssetAsync(connection, cardId, cancellationToken);
        if (latest != asset ||
            !originalHash.SequenceEqual(await HashFileAsync(originalPath, cancellationToken)) ||
            (previewHash is not null &&
                !previewHash.SequenceEqual(await HashFileAsync(previewPath, cancellationToken))))
            throw new InvalidOperationException("The card media changed during analysis. Run analysis again.");
        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedCardImageAnalysis(
            new CardImageAnalysisResult(cardId, predictions.Count, matched.Length, proposals.Length),
            proposals, asset.Metadata, analysis.Features, analysis.Features is null ? null : _tagger.ModelKey);
    }

    private static string? CategoryName(int category) => category switch
    {
        0 => "general", 1 => "artist", 3 => "copyright", 4 => "character", 5 => "meta", _ => null
    };

    private async Task<VisualTagAnalysis> PredictAnimatedAsync(
        string originalPath, double durationSeconds, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        await using var sampled = await _sampler.SampleAsync(originalPath, durationSeconds, cancellationToken);
        if (sampled.Frames.Count is < 1 or > 40)
            throw new InvalidOperationException("The video sampler returned an invalid frame count.");

        var analysisByPath = new Dictionary<string, VisualTagAnalysis>(StringComparer.Ordinal);
        var framePredictions = new List<(int Segment, VisualTagPrediction Prediction)>();
        foreach (var frame in sampled.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.IsSceneCandidate && clock.Elapsed >= TimeSpan.FromMinutes(3))
                continue;
            if (!analysisByPath.TryGetValue(frame.Path, out var analysis))
            {
                analysis = _tagger.Analyze(frame.Path);
                analysisByPath.Add(frame.Path, analysis);
            }
            framePredictions.AddRange(analysis.Predictions.Select(prediction => (frame.SegmentIndex, prediction)));
        }

        if (framePredictions.Any(item => item.Prediction.DanbooruId <= 0 ||
            item.Prediction.Confidence is < 0 or > 1))
            throw new InvalidOperationException("The visual tagger returned an invalid prediction.");

        // Frames without a feature vector make the combined vector incomplete, so keep none.
        var features = analysisByPath.Count > 0 && analysisByPath.Values.All(item => item.Features is not null)
            ? VisualFeatureVector.MaxOf(analysisByPath.Values.Select(item => item.Features)) : null;
        var combined = framePredictions
            .GroupBy(item => (item.Prediction.DanbooruId, item.Prediction.Category))
            .Select(group =>
            {
                var perSegment = group.GroupBy(item => item.Segment)
                    .Select(segment => segment.Max(item => item.Prediction.Confidence))
                    .ToArray();
                var peak = perSegment.Max();
                var recurrence = Math.Min(1m, (perSegment.Length - 1) / 2m);
                var score = peak + (1m - peak) * 0.15m * recurrence;
                return new VisualTagPrediction(group.Key.DanbooruId, score, group.Key.Category);
            })
            .ToArray();
        return new VisualTagAnalysis(combined, features);
    }

    private async Task<CardAsset> ReadAssetAsync(MySqlConnection connection, long cardId, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<CardRow>(new CommandDefinition(
            "SELECT type, metadata FROM cards WHERE id = @CardId;", new { CardId = cardId }, cancellationToken: ct));
        if (row is null) throw new CatalogMissingException("Card not found.");
        if (row.Type != "media")
            throw new InvalidOperationException("Only media cards can be analyzed by this command.");
        using var metadata = JsonDocument.Parse(row.Metadata);
        var root = metadata.RootElement;
        var mediaType = root.TryGetProperty("media_type", out var mediaTypeElement)
            ? mediaTypeElement.GetString() : null;
        if (mediaType is not ("image" or "gif" or "video"))
            throw new InvalidOperationException("Only image, GIF, and video cards can be analyzed by this command.");
        var content = root.TryGetProperty("content_path", out var contentPath) ? contentPath.GetString() : null;
        var preview = root.TryGetProperty("preview_path", out var previewPath) ? previewPath.GetString() : null;
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(preview))
            throw new InvalidOperationException("The card media paths are missing.");
        double? duration = null;
        if (mediaType != "image")
        {
            if (!root.TryGetProperty("duration", out var durationElement) ||
                durationElement.ValueKind != JsonValueKind.Number ||
                !durationElement.TryGetDouble(out var seconds) ||
                !double.IsFinite(seconds) || seconds < 0 ||
                (mediaType == "video" && seconds == 0))
                throw new InvalidOperationException("The card media duration is missing or invalid.");
            duration = seconds;
        }
        return new CardAsset(content, preview, mediaType, duration, row.Metadata);
    }

    private string ResolveAssetPath(long cardId, string relativePath, string mediaType, bool isPreview)
    {
        var prefix = $"cards/{cardId}/original.";
        var validOriginalExtensions = mediaType switch
        {
            "image" => new[] { "jpg", "jpeg", "png", "webp" },
            "gif" => new[] { "gif" },
            "video" => new[] { "mp4", "mov", "webm" },
            _ => Array.Empty<string>()
        };
        if (isPreview ? relativePath != $"cache/cards/{cardId}/preview.{(mediaType == "image" ? "webp" : "gif")}" :
            !relativePath.StartsWith(prefix, StringComparison.Ordinal) ||
            !validOriginalExtensions.Contains(relativePath[prefix.Length..], StringComparer.Ordinal))
            throw new InvalidOperationException("The card has an unexpected media path.");
        var root = Path.GetFullPath(_dataRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new InvalidOperationException("The card media path escapes the application data directory.");
        return path;
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return await SHA256.HashDataAsync(file, ct);
    }

    private sealed record CardAsset(string ContentPath, string PreviewPath, string MediaType,
        double? DurationSeconds, string Metadata);
    private sealed class CardRow
    {
        public string Type { get; set; } = "";
        public string Metadata { get; set; } = "{}";
    }

    private sealed class ImportedTag
    {
        public long Id { get; set; }
        public long DanbooruId { get; set; }
        public string Category { get; set; } = "";
    }
}

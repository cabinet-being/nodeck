using Dapper;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Analysis;
using MyApp.Api.Catalog;
using MySqlConnector;

namespace MyApp.Api.Tests;

public sealed class DatabaseTheoryAttribute : TheoryAttribute
{
    public DatabaseTheoryAttribute()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            Skip = "Set NODECK_TEST_CONNECTION_STRING to a dedicated MySQL test database.";
        else if (!new MySqlConnectionStringBuilder(connectionString).Database
                     .Contains("test", StringComparison.OrdinalIgnoreCase))
            Skip = "NODECK_TEST_CONNECTION_STRING must name a dedicated test database.";
    }
}

[Collection("Collection database")]
public sealed class AnimatedVisualAnalysisTests
{
    [DatabaseTheory]
    [InlineData("gif")]
    [InlineData("video")]
    public async Task SamplesOriginalAndAggregatesPeakWithDistinctSegmentRecurrence(string mediaType)
    {
        await using var scope = await TestScope.CreateAsync(mediaType, 12);
        var seed = Random.Shared.NextInt64(1_000_000_000_000, 9_000_000_000_000);
        var recurring = await scope.InsertTagAsync(seed, "general");
        var rare = await scope.InsertTagAsync(seed + 1, "character");
        await scope.InsertTagAsync(seed + 2, "artist");
        var tagger = new FakeTagger(new Dictionary<string, IReadOnlyList<VisualTagPrediction>>
        {
            ["repeat"] = [new(seed, 0.8m, 0), new(seed + 2, 0.95m, 4)],
            ["rare"] = [new(seed + 1, 0.9m, 4)]
        });
        var sampler = new FakeSampler([
            new("repeat", 0.1, 0), new("repeat", 0.2, 0),
            new("repeat", 4.1, 3), new("repeat", 8.1, 7),
            new("rare", 10.1, 9)
        ]);

        var result = await scope.Service(tagger, sampler).AnalyzeAsync(scope.CardId);

        Assert.Equal(scope.OriginalPath, sampler.SeenPath);
        Assert.Equal(12, sampler.SeenDuration);
        Assert.Equal(1, tagger.Calls["repeat"]);
        Assert.Equal(1, tagger.Calls["rare"]);
        Assert.Equal(2, result.MatchedTags);
        var proposals = await new CardCatalogRepository(scope.Configuration)
            .ListPendingSuggestionsAsync(scope.CardId);
        Assert.Equal(2, proposals.Count);
        Assert.Equal(0.83m, proposals.Single(item => item.TagId == recurring).Confidence);
        Assert.Equal(0.9m, proposals.Single(item => item.TagId == rare).Confidence);
    }

    [DatabaseFact]
    public async Task ZeroDurationGifCanAnalyzeItsStartFrame()
    {
        await using var scope = await TestScope.CreateAsync("gif", 0);
        var externalId = Random.Shared.NextInt64(1_000_000_000_000, 9_000_000_000_000);
        var tagId = await scope.InsertTagAsync(externalId, "general");
        var sampler = new FakeSampler([new("start", 0, 0)]);
        var tagger = new FakeTagger(new Dictionary<string, IReadOnlyList<VisualTagPrediction>>
        {
            ["start"] = [new(externalId, 0.91m, 0)]
        });

        var result = await scope.Service(tagger, sampler).AnalyzeAsync(scope.CardId);

        Assert.Equal(0, sampler.SeenDuration);
        Assert.Equal(1, result.SubmittedSuggestions);
        Assert.Equal(tagId, Assert.Single(await new CardCatalogRepository(scope.Configuration)
            .ListPendingSuggestionsAsync(scope.CardId)).TagId);
    }

    [DatabaseFact]
    public async Task ChangedOriginalDuringAnimatedPredictionDoesNotPublishSuggestions()
    {
        await using var scope = await TestScope.CreateAsync("video", 5);
        var externalId = Random.Shared.NextInt64(1_000_000_000_000, 9_000_000_000_000);
        await scope.InsertTagAsync(externalId, "general");
        var tagger = new FakeTagger(new Dictionary<string, IReadOnlyList<VisualTagPrediction>>
        {
            ["frame"] = [new(externalId, 0.9m, 0)]
        }, _ => File.WriteAllBytes(scope.OriginalPath, [9, 8, 7, 6]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Service(tagger, new FakeSampler([new("frame", 0, 0)])).AnalyzeAsync(scope.CardId));

        Assert.Equal(0, await scope.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_tag_suggestions WHERE card_id = @CardId;",
            new { scope.CardId }));
    }

    private sealed class FakeSampler(IReadOnlyList<SampledVideoFrame> frames) : IVideoFrameSampler
    {
        public string? SeenPath { get; private set; }
        public double? SeenDuration { get; private set; }

        public Task<SampledVideoFrames> SampleAsync(string originalPath, double durationSeconds, CancellationToken ct)
        {
            SeenPath = originalPath;
            SeenDuration = durationSeconds;
            return Task.FromResult(new SampledVideoFrames(frames));
        }
    }

    private sealed class FakeTagger(
        IReadOnlyDictionary<string, IReadOnlyList<VisualTagPrediction>> predictions,
        Action<string>? onPredict = null) : IVisualTagger
    {
        public string ModelKey => "fake-animated:v1";
        public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<VisualTagPrediction> Predict(string imagePath)
        {
            Calls[imagePath] = Calls.GetValueOrDefault(imagePath) + 1;
            onPredict?.Invoke(imagePath);
            return predictions[imagePath];
        }

        public void Dispose() { }
    }

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly List<long> _tags = [];

        private TestScope(MySqlConnection connection, IConfiguration configuration, long cardId,
            string root, string extension)
        {
            Connection = connection;
            Configuration = configuration;
            CardId = cardId;
            Root = root;
            OriginalPath = Path.Combine(root, "cards", cardId.ToString(), $"original.{extension}");
        }

        public MySqlConnection Connection { get; }
        public IConfiguration Configuration { get; }
        public long CardId { get; }
        public string Root { get; }
        public string OriginalPath { get; }

        public static async Task<TestScope> CreateAsync(string mediaType, double duration)
        {
            var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
            var root = Path.Combine(Path.GetTempPath(), "nodeck-animated-analysis-tests", Guid.NewGuid().ToString("N"));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
                ["NODECK_APP_DATA"] = root
            }).Build();
            var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "database/init/001_cards.sql")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            foreach (var file in new[] { "001_cards.sql", "002_catalog.sql" })
            {
                var sql = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "database/init", file));
                foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    await connection.ExecuteAsync(statement);
            }
            var cardId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO cards (type, title, metadata)
                VALUES ('media', @Title, JSON_OBJECT('media_type', @MediaType));
                SELECT LAST_INSERT_ID();
                """, new { Title = $"animated-{Guid.NewGuid():N}", MediaType = mediaType });
            var extension = mediaType == "gif" ? "gif" : "mp4";
            var scope = new TestScope(connection, configuration, cardId, root, extension);
            await connection.ExecuteAsync("""
                UPDATE cards SET metadata = JSON_OBJECT(
                    'media_type', @MediaType, 'content_path', @ContentPath,
                    'preview_path', @PreviewPath, 'duration', @Duration)
                WHERE id = @CardId;
                """, new
            {
                MediaType = mediaType, ContentPath = $"cards/{cardId}/original.{extension}",
                PreviewPath = $"cache/cards/{cardId}/preview.gif", Duration = duration, CardId = cardId
            });
            Directory.CreateDirectory(Path.GetDirectoryName(scope.OriginalPath)!);
            var previewPath = Path.Combine(root, "cache", "cards", cardId.ToString(), "preview.gif");
            Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
            await File.WriteAllBytesAsync(scope.OriginalPath, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(previewPath, [5, 6, 7, 8]);
            return scope;
        }

        public CardImageAnalysisService Service(IVisualTagger tagger, IVideoFrameSampler sampler) =>
            new(Configuration, new CardCatalogRepository(Configuration), tagger, sampler);

        public async Task<long> InsertTagAsync(long danbooruId, string category)
        {
            var key = $"animated_{Guid.NewGuid():N}";
            var id = await Connection.ExecuteScalarAsync<long>("""
                INSERT INTO tags (name, canonical_key, category, origin, danbooru_id)
                VALUES (@Key, @Key, @Category, 'danbooru', @DanbooruId);
                SELECT LAST_INSERT_ID();
                """, new { Key = key, Category = category, DanbooruId = danbooruId });
            _tags.Add(id);
            return id;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Connection.ExecuteAsync("DELETE FROM cards WHERE id = @CardId;", new { CardId });
                if (_tags.Count > 0)
                    await Connection.ExecuteAsync("DELETE FROM tags WHERE id IN @Ids;", new { Ids = _tags });
            }
            finally
            {
                await Connection.DisposeAsync();
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
        }
    }
}

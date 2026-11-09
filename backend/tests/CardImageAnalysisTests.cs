using Dapper;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Analysis;
using MyApp.Api.Catalog;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class CardImageAnalysisTests
{
    [DatabaseFact]
    public async Task AnalysisMapsImportedIdsOnlyAndPreservesManualAndRejectedDecisions()
    {
        await using var scope = await TestScope.CreateAsync();
        var externalId = Random.Shared.NextInt64(1_000_000_000_000, 9_000_000_000_000);
        var manual = await scope.InsertTagAsync(externalId, "general");
        var rejected = await scope.InsertTagAsync(externalId + 1, "character");
        var proposed = await scope.InsertTagAsync(externalId + 2, "general");
        var userOwned = await scope.InsertTagAsync(externalId + 3, "general", "user");
        var rating = await scope.InsertTagAsync(externalId + 4, "general");
        var artist = await scope.InsertTagAsync(externalId + 5, "artist");
        var copyright = await scope.InsertTagAsync(externalId + 6, "copyright");
        var meta = await scope.InsertTagAsync(externalId + 7, "meta");
        var wrongCategory = await scope.InsertTagAsync(externalId + 8, "general");
        var assignments = new CardCatalogRepository(scope.Configuration);
        await assignments.SetTagAsync(scope.CardId, manual);
        await assignments.SetTagAsync(scope.CardId, rejected);
        Assert.True(await assignments.DeleteTagAsync(scope.CardId, rejected));
        var tagCountBefore = await scope.Connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tags;");
        var tagger = new FakeTagger(
        [
            new(externalId, 0.95m, 0),
            new(externalId + 1, 0.92m, 4),
            new(externalId + 2, 0.80m, 0),
            new(externalId + 2, 0.85m, 0),
            new(externalId + 3, 0.90m, 0),
            new(externalId + 4, 0.99m, 9),
            new(externalId + 5, 0.75m, 1),
            new(externalId + 6, 0.70m, 3),
            new(externalId + 7, 0.65m, 5),
            new(externalId + 8, 0.96m, 4),
            new(externalId + 99, 0.97m, 0)
        ]);

        var result = await scope.Service(tagger).AnalyzeAsync(scope.CardId);

        Assert.Equal(scope.CardId, result.CardId);
        Assert.Equal(11, result.Predictions);
        Assert.Equal(6, result.MatchedTags);
        Assert.Equal(6, result.SubmittedSuggestions);
        Assert.Equal(scope.PreviewPath, tagger.SeenPath);
        var pending = await assignments.ListPendingSuggestionsAsync(scope.CardId);
        Assert.Equal(new[] { proposed, artist, copyright, meta }.Order(),
            pending.Select(item => item.TagId).Order());
        Assert.Equal(0.85m, pending.Single(item => item.TagId == proposed).Confidence);
        Assert.All(pending, item => Assert.Equal("fake-visual:v1", item.ModelKey));
        var assignmentsAfterAnalysis = await assignments.ListTagsAsync(scope.CardId);
        Assert.Equal("manual", assignmentsAfterAnalysis.Single(item => item.Id == manual).AssignmentSource);
        Assert.All(assignmentsAfterAnalysis.Where(item => item.Id != manual),
            item => Assert.Equal("auto", item.AssignmentSource));
        Assert.Equal("rejected", await scope.SuggestionStateAsync(rejected));
        Assert.Null(await scope.SuggestionStateAsync(manual));
        Assert.Null(await scope.SuggestionStateAsync(userOwned));
        Assert.Null(await scope.SuggestionStateAsync(rating));
        Assert.Null(await scope.SuggestionStateAsync(wrongCategory));
        Assert.Equal(5, await scope.Connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM card_tags WHERE card_id = @CardId;", new { scope.CardId }));
        Assert.Equal(tagCountBefore, await scope.Connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tags;"));
    }

    [DatabaseFact]
    public async Task NonImageCardIsRejectedBeforeInvokingTagger()
    {
        await using var scope = await TestScope.CreateAsync();
        await scope.SetMediaTypeAsync("video");
        var tagger = new FakeTagger([]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Service(tagger).AnalyzeAsync(scope.CardId));

        Assert.Null(tagger.SeenPath);
        Assert.Equal(0, await scope.SuggestionCountAsync());
    }

    [DatabaseFact]
    public async Task MissingCardIsReportedBeforeInvokingTagger()
    {
        await using var scope = await TestScope.CreateAsync();
        var tagger = new FakeTagger([]);

        await Assert.ThrowsAsync<CatalogMissingException>(() =>
            scope.Service(tagger).AnalyzeAsync(long.MaxValue));

        Assert.Null(tagger.SeenPath);
    }

    [DatabaseFact]
    public async Task ChangedOriginalDuringPredictionIsRejectedWithoutSuggestions()
    {
        await using var scope = await TestScope.CreateAsync();
        var externalId = Random.Shared.NextInt64(1_000_000_000_000, 9_000_000_000_000);
        await scope.InsertTagAsync(externalId, "general");
        var tagger = new FakeTagger([new(externalId, 0.9m, 0)], _ =>
            File.WriteAllBytes(scope.OriginalPath, [9, 8, 7, 6]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Service(tagger).AnalyzeAsync(scope.CardId));

        Assert.Equal(scope.PreviewPath, tagger.SeenPath);
        Assert.Equal(0, await scope.SuggestionCountAsync());
        Assert.Equal(0, await scope.AssignmentCountAsync());
    }

    [DatabaseFact]
    public async Task ChangedPreviewDuringPredictionIsRejectedWithoutSuggestions()
    {
        await using var scope = await TestScope.CreateAsync();
        var externalId = Random.Shared.NextInt64(1_000_000_000_000, 9_000_000_000_000);
        await scope.InsertTagAsync(externalId, "general");
        var tagger = new FakeTagger([new(externalId, 0.9m, 0)], imagePath =>
            File.WriteAllBytes(imagePath, [8, 7, 6, 5]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Service(tagger).AnalyzeAsync(scope.CardId));

        Assert.Equal(scope.PreviewPath, tagger.SeenPath);
        Assert.Equal(0, await scope.SuggestionCountAsync());
        Assert.Equal(0, await scope.AssignmentCountAsync());
    }

    [DatabaseFact]
    public async Task ChangedMetadataDuringPredictionIsRejectedWithoutSuggestions()
    {
        await using var scope = await TestScope.CreateAsync();
        var externalId = Random.Shared.NextInt64(1_000_000_000_000, 9_000_000_000_000);
        await scope.InsertTagAsync(externalId, "general");
        var tagger = new FakeTagger([new(externalId, 0.9m, 0)], _ =>
            scope.Connection.Execute(
                "UPDATE cards SET metadata = JSON_SET(metadata, '$.content_path', @Path) WHERE id = @CardId;",
                new { Path = $"cards/{scope.CardId}/original.webp", scope.CardId }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Service(tagger).AnalyzeAsync(scope.CardId));

        Assert.Equal(0, await scope.SuggestionCountAsync());
        Assert.Equal(0, await scope.AssignmentCountAsync());
    }

    [DatabaseFact]
    public async Task AnalysisLimitsSuggestionsToFortyHighestConfidenceTags()
    {
        await using var scope = await TestScope.CreateAsync();
        var externalId = Random.Shared.NextInt64(1_000_000_000_000, 9_000_000_000_000);
        var predictions = new List<VisualTagPrediction>();
        var tagIds = new List<long>();
        for (var index = 0; index < 45; index++)
        {
            tagIds.Add(await scope.InsertTagAsync(externalId + index, "general"));
            predictions.Add(new VisualTagPrediction(externalId + index, (index + 50) / 100m, 0));
        }

        var result = await scope.Service(new FakeTagger(predictions)).AnalyzeAsync(scope.CardId);

        Assert.Equal(45, result.MatchedTags);
        Assert.Equal(40, result.SubmittedSuggestions);
        var pending = await new CardCatalogRepository(scope.Configuration).ListPendingSuggestionsAsync(scope.CardId);
        Assert.Equal(40, pending.Count);
        Assert.Equal(tagIds.Skip(5).Order(), pending.Select(item => item.TagId).Order());
        Assert.Equal(40, await scope.AssignmentCountAsync());
    }

    private sealed class FakeTagger(
        IReadOnlyList<VisualTagPrediction> predictions, Action<string>? onPredict = null) : IVisualTagger
    {
        public string ModelKey => "fake-visual:v1";
        public string? SeenPath { get; private set; }

        public IReadOnlyList<VisualTagPrediction> Predict(string imagePath)
        {
            SeenPath = imagePath;
            onPredict?.Invoke(imagePath);
            return predictions;
        }

        public void Dispose() { }
    }

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly List<long> _tagIds = [];

        private TestScope(MySqlConnection connection, IConfiguration configuration, long cardId, string root)
        {
            Connection = connection;
            Configuration = configuration;
            CardId = cardId;
            Root = root;
            OriginalPath = Path.Combine(root, "cards", cardId.ToString(), "original.png");
            PreviewPath = Path.Combine(root, "cache", "cards", cardId.ToString(), "preview.webp");
        }

        public MySqlConnection Connection { get; }
        public IConfiguration Configuration { get; }
        public long CardId { get; }
        public string Root { get; }
        public string OriginalPath { get; }
        public string PreviewPath { get; }

        public static async Task<TestScope> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
            var root = Path.Combine(Path.GetTempPath(), "nodeck-image-analysis-tests", Guid.NewGuid().ToString("N"));
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
                VALUES ('media', @Title, JSON_OBJECT('media_type', 'image'));
                SELECT LAST_INSERT_ID();
                """, new { Title = $"analysis-{Guid.NewGuid():N}" });
            var scope = new TestScope(connection, configuration, cardId, root);
            await connection.ExecuteAsync("""
                UPDATE cards SET metadata = JSON_OBJECT(
                    'media_type', 'image', 'content_path', @ContentPath, 'preview_path', @PreviewPath)
                WHERE id = @CardId;
                """, new
            {
                ContentPath = $"cards/{cardId}/original.png",
                PreviewPath = $"cache/cards/{cardId}/preview.webp",
                CardId = cardId
            });
            Directory.CreateDirectory(Path.GetDirectoryName(scope.OriginalPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(scope.PreviewPath)!);
            await File.WriteAllBytesAsync(scope.OriginalPath, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(scope.PreviewPath, [5, 6, 7, 8]);
            return scope;
        }

        public CardImageAnalysisService Service(IVisualTagger tagger) =>
            new(Configuration, new CardCatalogRepository(Configuration), tagger);

        public async Task<long> InsertTagAsync(long danbooruId, string category, string origin = "danbooru")
        {
            var token = Guid.NewGuid().ToString("N");
            var id = await Connection.ExecuteScalarAsync<long>("""
                INSERT INTO tags (name, canonical_key, category, origin, danbooru_id)
                VALUES (@Name, @Key, @Category, @Origin, @DanbooruId);
                SELECT LAST_INSERT_ID();
                """, new { Name = $"analysis-{token}", Key = $"analysis-{token}", Category = category,
                    Origin = origin, DanbooruId = danbooruId });
            _tagIds.Add(id);
            return id;
        }

        public Task SetMediaTypeAsync(string mediaType) => Connection.ExecuteAsync(
            "UPDATE cards SET metadata = JSON_SET(metadata, '$.media_type', @MediaType) WHERE id = @CardId;",
            new { MediaType = mediaType, CardId });

        public Task<string?> SuggestionStateAsync(long tagId) => Connection.ExecuteScalarAsync<string?>(
            "SELECT state FROM card_tag_suggestions WHERE card_id = @CardId AND tag_id = @TagId;",
            new { CardId, TagId = tagId });

        public Task<long> SuggestionCountAsync() => Connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM card_tag_suggestions WHERE card_id = @CardId;", new { CardId });

        public Task<long> AssignmentCountAsync() => Connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM card_tags WHERE card_id = @CardId;", new { CardId });

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Connection.ExecuteAsync("DELETE FROM cards WHERE id = @CardId;", new { CardId });
                if (_tagIds.Count > 0)
                    await Connection.ExecuteAsync("DELETE FROM tags WHERE id IN @Ids;", new { Ids = _tagIds });
            }
            finally
            {
                await Connection.DisposeAsync();
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
        }
    }
}

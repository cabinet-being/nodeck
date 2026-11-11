using Dapper;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Analysis;
using MyApp.Api.Analysis.LearnedTags;
using MyApp.Api.Catalog;
using MySqlConnector;

namespace MyApp.Api.Tests;

public sealed class LearnedTagClassifierTests
{
    private const int Dimension = 32;

    [Fact]
    public void SeparableExamplesGetAConservativeThreshold()
    {
        var random = new Random(7);
        var examples = Enumerable.Range(0, 8).Select(_ => new LearnedTagExample(Vector(random, 3), true))
            .Concat(Enumerable.Range(0, 40).Select(_ => new LearnedTagExample(Vector(random), false)))
            .ToArray();

        var model = LearnedTagClassifier.Train(Dimension, examples, 1, 11);

        Assert.True(model.Enabled);
        Assert.InRange(model.Threshold!.Value, 0.5m, 1m);
        Assert.True(model.Precision >= 0.8m);
        Assert.True(model.Score(Vector(new Random(99), 3)) >= (double)model.Threshold);
        Assert.True(model.Score(Vector(new Random(98))) < (double)model.Threshold);
    }

    [Fact]
    public void UnlearnableOrTooFewExamplesProposeNothing()
    {
        var random = new Random(5);
        // Labels unrelated to the features: no threshold reaches the target precision.
        var noise = Enumerable.Range(0, 60)
            .Select(i => new LearnedTagExample(Vector(random), i % 4 == 0)).ToArray();
        Assert.False(LearnedTagClassifier.Train(Dimension, noise, 1, 3).Enabled);

        var few = Enumerable.Range(0, 4).Select(_ => new LearnedTagExample(Vector(random, 3), true))
            .Concat(Enumerable.Range(0, 20).Select(_ => new LearnedTagExample(Vector(random), false)))
            .ToArray();
        var model = LearnedTagClassifier.Train(Dimension, few, 1, 3);
        Assert.False(model.Enabled);
        Assert.Equal(4, model.Positives);
    }

    [Fact]
    public void FeatureVectorsRoundTripAndCombineFramesByPeak()
    {
        var scores = new float[Dimension];
        scores[1] = 0.01f;
        scores[2] = 0.5f;
        scores[9] = 0.25f;
        var first = VisualFeatureVector.FromProbabilities(scores);
        Assert.Equal(new[] { 2, 9 }, first.Indices);

        var decoded = VisualFeatureVector.FromBytes(Dimension, first.ToBytes());
        Assert.Equal(first.Indices, decoded.Indices);
        Assert.Equal(first.Values, decoded.Values);

        scores[2] = 0.1f;
        scores[4] = 0.9f;
        var combined = VisualFeatureVector.MaxOf([first, VisualFeatureVector.FromProbabilities(scores)])!;
        Assert.Equal(new[] { 2, 4, 9 }, combined.Indices);
        Assert.Equal(new[] { 0.5f, 0.9f, 0.25f }, combined.Values);
    }

    /// <summary>Background noise on indices 10 and above, plus a strong signal on the given indices.</summary>
    internal static VisualFeatureVector Vector(Random random, params int[] signal)
    {
        var scores = new float[Dimension];
        for (var i = 10; i < Dimension; i++)
            if (random.NextDouble() < 0.4) scores[i] = 0.05f + (float)random.NextDouble() * 0.9f;
        foreach (var index in signal) scores[index] = 0.85f + (float)random.NextDouble() * 0.1f;
        return VisualFeatureVector.FromProbabilities(scores);
    }
}

public sealed class LearnedTagSuggestionTests
{
    private const string FeatureModel = "fake-features:v1";

    [DatabaseFact]
    public async Task TrainingLearnsACustomTagAndProposesItOnlyOnMatchingNewCards()
    {
        await using var db = await LearnedTagDatabase.CreateAsync();
        var random = new Random(21);
        var toy = await db.CreateTagAsync("toy");
        var fewExamples = await db.CreateTagAsync("rare");
        var danbooru = await db.CreateTagAsync("solo", category: "general", origin: "danbooru");
        var positives = new List<long>();
        for (var i = 0; i < 8; i++)
        {
            var card = await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random, 3));
            await db.Catalog.SetTagAsync(card, toy);
            await db.Catalog.SetTagAsync(card, danbooru);
            positives.Add(card);
        }
        for (var i = 0; i < 4; i++)
            await db.Catalog.SetTagAsync(
                await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random, 5)), fewExamples);
        for (var i = 0; i < 30; i++)
            await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random));

        var summary = await db.Learned.TrainAsync(FeatureModel);

        Assert.Equal(1, summary.Generation);
        Assert.Equal(1, summary.TagsTrained);
        Assert.Equal(1, summary.TagsEnabled);
        var status = await db.Learned.GetStatusAsync();
        var model = Assert.Single(status.Models);
        Assert.Equal(toy, model.TagId);
        Assert.Equal(8, model.Positives);
        Assert.True(model.Enabled);

        var matching = await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(new Random(1), 3));
        var other = await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(new Random(2)));
        var unanalysed = await db.CreateCardAsync();
        while (await db.Learned.ScoreAsync(FeatureModel, 10) > 0) { }

        var suggestion = await db.SuggestionAsync(matching, toy);
        Assert.NotNull(suggestion);
        Assert.Equal("pending", suggestion.State);
        Assert.Equal("learned", suggestion.Source);
        Assert.Equal(LearnedTagRepository.ProposalModelKey(1), suggestion.ModelKey);
        Assert.InRange(suggestion.Confidence!.Value, model.Threshold!.Value, 1m);
        Assert.Equal("auto", await db.AssignmentAsync(matching, toy));
        var pending = Assert.Single(await db.Catalog.ListPendingSuggestionsAsync(matching));
        Assert.Equal(toy, pending.TagId);

        Assert.Null(await db.SuggestionAsync(other, toy));
        Assert.Null(await db.SuggestionAsync(unanalysed, toy));
        foreach (var card in positives)
        {
            Assert.Null(await db.SuggestionAsync(card, toy));
            Assert.Equal("manual", await db.AssignmentAsync(card, toy));
        }
        Assert.Equal(0, (await db.Learned.GetStatusAsync()).PendingCards);
        Assert.Equal(0, await db.Learned.ScoreAsync(FeatureModel));
    }

    [DatabaseFact]
    public async Task DecisionsOnLearnedProposalsAreLoggedAndRejectionsBecomeHardNegatives()
    {
        await using var db = await LearnedTagDatabase.CreateAsync();
        var random = new Random(33);
        var toy = await db.CreateTagAsync("toy");
        for (var i = 0; i < 8; i++)
            await db.Catalog.SetTagAsync(
                await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random, 3)), toy);
        for (var i = 0; i < 30; i++)
            await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random));
        await db.Learned.TrainAsync(FeatureModel);
        var accepted = await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random, 3));
        var rejected = await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random, 3));
        while (await db.Learned.ScoreAsync(FeatureModel) > 0) { }
        var before = await db.Learned.ComputeFingerprintAsync(FeatureModel);

        Assert.True(await db.Catalog.DecideSuggestionAsync(accepted, toy, "accepted"));
        Assert.True(await db.Catalog.DecideSuggestionAsync(rejected, toy, "rejected"));

        Assert.Equal("confirmed", await db.AssignmentAsync(accepted, toy));
        Assert.Null(await db.AssignmentAsync(rejected, toy));
        var log = (await db.Connection.QueryAsync<(long CardId, string Decision, string Source, string ModelKey)>("""
            SELECT card_id, decision, suggestion_source, model_key FROM card_tag_decisions
            WHERE suggested_tag_id = @TagId ORDER BY id;
            """, new { TagId = toy })).ToArray();
        Assert.Equal(new[] { (accepted, "accepted"), (rejected, "rejected") },
            log.Select(row => (row.CardId, row.Decision)));
        Assert.All(log, row => Assert.Equal("learned", row.Source));
        Assert.All(log, row => Assert.Equal(LearnedTagRepository.ProposalModelKey(1), row.ModelKey));
        Assert.NotEqual(before, await db.Learned.ComputeFingerprintAsync(FeatureModel));

        var summary = await db.Learned.TrainAsync(FeatureModel);
        while (await db.Learned.ScoreAsync(FeatureModel) > 0) { }

        Assert.Equal(2, summary.Generation);
        var model = Assert.Single((await db.Learned.GetStatusAsync()).Models);
        Assert.Equal(9, model.Positives);
        Assert.Equal(1, model.HardNegatives);
        Assert.Equal("rejected", (await db.SuggestionAsync(rejected, toy))!.State);
        Assert.Null(await db.AssignmentAsync(rejected, toy));
        Assert.Equal("accepted", (await db.SuggestionAsync(accepted, toy))!.State);
    }

    [DatabaseFact]
    public async Task RetrainingWithdrawsPendingLearnedProposalsButKeepsOtherSources()
    {
        await using var db = await LearnedTagDatabase.CreateAsync();
        var random = new Random(44);
        var toy = await db.CreateTagAsync("toy");
        var spoken = await db.CreateTagAsync("spoken");
        var examples = new List<long>();
        for (var i = 0; i < 6; i++)
        {
            var example = await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random, 3));
            await db.Catalog.SetTagAsync(example, toy);
            examples.Add(example);
        }
        for (var i = 0; i < 30; i++)
            await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random));
        await db.Learned.TrainAsync(FeatureModel);
        var card = await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random, 3));
        await db.Connection.ExecuteAsync("""
            INSERT INTO card_tag_suggestions (card_id, tag_id, state, confidence, model_key, suggestion_source)
            VALUES (@CardId, @TagId, 'pending', 0.6, 'speech-test', 'speech');
            """, new { CardId = card, TagId = spoken });
        while (await db.Learned.ScoreAsync(FeatureModel) > 0) { }
        Assert.Equal("pending", (await db.SuggestionAsync(card, toy))!.State);
        Assert.Equal("auto", await db.AssignmentAsync(card, toy));

        // Two examples removed: the tag no longer has enough to be learned.
        Assert.True(await db.Catalog.DeleteTagAsync(examples[0], toy));
        Assert.True(await db.Catalog.DeleteTagAsync(examples[1], toy));
        var summary = await db.Learned.TrainAsync(FeatureModel);
        Assert.Equal(0, summary.TagsTrained);
        while (await db.Learned.ScoreAsync(FeatureModel) > 0) { }

        Assert.Null(await db.SuggestionAsync(card, toy));
        Assert.Null(await db.AssignmentAsync(card, toy));
        Assert.Equal("speech", (await db.SuggestionAsync(card, spoken))!.Source);
    }

    [DatabaseFact]
    public async Task ReplacedMediaIsNotScoredWithFeaturesOfTheOldRevision()
    {
        await using var db = await LearnedTagDatabase.CreateAsync();
        var random = new Random(55);
        var toy = await db.CreateTagAsync("toy");
        for (var i = 0; i < 8; i++)
            await db.Catalog.SetTagAsync(
                await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random, 3)), toy);
        for (var i = 0; i < 30; i++)
            await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random));
        await db.Learned.TrainAsync(FeatureModel);
        var card = await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(random, 3));
        await db.Connection.ExecuteAsync("""
            UPDATE card_image_analysis_jobs SET revision = @Revision, state = 'pending' WHERE card_id = @CardId;
            """, new { Revision = Guid.NewGuid().ToString("D"), CardId = card });

        while (await db.Learned.ScoreAsync(FeatureModel) > 0) { }

        Assert.Null(await db.SuggestionAsync(card, toy));
    }

    [DatabaseFact]
    public async Task AnalysisStoresFeaturesForTheLeasedRevision()
    {
        await using var db = await LearnedTagDatabase.CreateAsync();
        var card = await db.CreateCardAsync(withFiles: true);
        var revision = Guid.NewGuid().ToString("D");
        await db.Connection.ExecuteAsync("""
            INSERT INTO card_image_analysis_jobs (card_id, revision, state, attempts, available_at_utc)
            VALUES (@CardId, @Revision, 'pending', 0, UTC_TIMESTAMP(6));
            """, new { CardId = card, Revision = revision });
        var features = LearnedTagClassifierTests.Vector(new Random(3), 3);
        var tagger = new FeatureTagger(features);

        var lease = await db.Queue.ClaimAsync(default);
        Assert.NotNull(lease);
        var prepared = await new CardImageAnalysisService(db.Configuration, db.Catalog, tagger)
            .PrepareAsync(card);
        Assert.True(await db.Queue.CompleteAsync(lease, prepared, default));

        var row = await db.Connection.QuerySingleAsync<(string Revision, string ModelKey, int Dimension, byte[] Bytes)>("""
            SELECT revision, model_key, dimension, features FROM card_visual_features WHERE card_id = @CardId;
            """, new { CardId = card });
        Assert.Equal(revision, row.Revision);
        Assert.Equal(FeatureModel, row.ModelKey);
        Assert.Equal(features.Dimension, row.Dimension);
        Assert.Equal(features.Indices, VisualFeatureVector.FromBytes(row.Dimension, row.Bytes).Indices);
    }

    [DatabaseFact]
    public async Task BackfillQueuesCardsWithoutCurrentFeaturesBehindRegularJobs()
    {
        await using var db = await LearnedTagDatabase.CreateAsync();
        var doneWithoutFeatures = await db.CreateCardAsync();
        await db.SetJobAsync(doneWithoutFeatures, "done");
        var withoutJob = await db.CreateCardAsync();
        var current = await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(new Random(1)));
        var failed = await db.CreateCardAsync();
        await db.SetJobAsync(failed, "failed");
        var stale = await db.CreateFeaturedCardAsync(LearnedTagClassifierTests.Vector(new Random(2)));
        await db.Connection.ExecuteAsync(
            "UPDATE card_image_analysis_jobs SET revision = UUID() WHERE card_id = @CardId;", new { CardId = stale });
        var note = await db.Connection.ExecuteScalarAsync<long>("""
            INSERT INTO cards (type, title, metadata) VALUES ('tag', 'not media', JSON_OBJECT());
            SELECT LAST_INSERT_ID();
            """);

        Assert.Equal(3, await db.Learned.EnqueueBackfillAsync(FeatureModel, 10));
        Assert.Equal(0, await db.Learned.EnqueueBackfillAsync(FeatureModel, 10));

        var jobs = (await db.Connection.QueryAsync<(long CardId, string State, int Priority)>(
            "SELECT card_id, state, priority FROM card_image_analysis_jobs;")).ToDictionary(row => row.CardId);
        foreach (var card in new[] { doneWithoutFeatures, withoutJob, stale })
            Assert.Equal(("pending", 1), (jobs[card].State, jobs[card].Priority));
        Assert.Equal("done", jobs[current].State);
        Assert.Equal("failed", jobs[failed].State);
        Assert.False(jobs.ContainsKey(note));

        // A regular job queued later, even a video, is claimed before every backfill job.
        var upload = await db.CreateCardAsync(mediaType: "video");
        await db.Connection.ExecuteAsync("""
            INSERT INTO card_image_analysis_jobs (card_id, revision, state, attempts, available_at_utc)
            VALUES (@CardId, UUID(), 'pending', 0, UTC_TIMESTAMP(6) + INTERVAL 1 SECOND);
            """, new { CardId = upload });
        await db.Connection.ExecuteAsync(
            "UPDATE card_image_analysis_jobs SET available_at_utc = UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE WHERE priority = 1;");
        await db.Connection.ExecuteAsync(
            "UPDATE card_image_analysis_jobs SET available_at_utc = UTC_TIMESTAMP(6) WHERE card_id = @CardId;",
            new { CardId = upload });
        Assert.Equal(upload, (await db.Queue.ClaimAsync(default))!.CardId);
        Assert.Equal(1, await db.Connection.ExecuteScalarAsync<int>(
            "SELECT priority FROM card_image_analysis_jobs WHERE card_id = @CardId;",
            new { CardId = (await db.Queue.ClaimAsync(default))!.CardId }));
    }

    private sealed class FeatureTagger(VisualFeatureVector features) : IVisualTagger
    {
        public string ModelKey => FeatureModel;
        public IReadOnlyList<VisualTagPrediction> Predict(string imagePath) => [];
        public VisualTagAnalysis Analyze(string imagePath) => new([], features);
        public void Dispose() { }
    }

    private sealed record SuggestionRow(string State, string Source, string ModelKey, decimal? Confidence);

    /// <summary>A throwaway database per test, created next to the dedicated test database.</summary>
    private sealed class LearnedTagDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _name;
        private readonly string _root;

        private LearnedTagDatabase(string adminConnectionString, string name, string root,
            MySqlConnection connection, IConfiguration configuration)
        {
            _adminConnectionString = adminConnectionString;
            _name = name;
            _root = root;
            Connection = connection;
            Configuration = configuration;
            Catalog = new CardCatalogRepository(configuration);
            Learned = new LearnedTagRepository(configuration);
            Queue = new AnalysisQueueRepository(configuration);
        }

        public MySqlConnection Connection { get; }
        public IConfiguration Configuration { get; }
        public CardCatalogRepository Catalog { get; }
        public LearnedTagRepository Learned { get; }
        public AnalysisQueueRepository Queue { get; }

        public static async Task<LearnedTagDatabase> CreateAsync()
        {
            var builder = new MySqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!);
            var name = $"nodeck_learned_{Guid.NewGuid():N}_test";
            builder.Database = "";
            var admin = builder.ConnectionString;
            await using (var connection = new MySqlConnection(admin))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync($"CREATE DATABASE `{name}`;");
            }
            builder.Database = name;
            var root = Path.Combine(Path.GetTempPath(), "nodeck-learned-tag-tests", Guid.NewGuid().ToString("N"));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = builder.ConnectionString,
                ["NODECK_APP_DATA"] = root
            }).Build();
            var db = new LearnedTagDatabase(admin, name, root, new MySqlConnection(builder.ConnectionString),
                configuration);
            await db.Connection.OpenAsync();
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "database/init/001_cards.sql")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            foreach (var file in new[] { "001_cards.sql", "002_catalog.sql" })
            {
                var sql = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "database/init", file));
                foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    await db.Connection.ExecuteAsync(statement);
            }
            return db;
        }

        public Task<long> CreateTagAsync(string name, string category = "custom", string origin = "user") =>
            Connection.ExecuteScalarAsync<long>("""
                INSERT INTO tags (name, canonical_key, category, origin) VALUES (@Name, @Name, @Category, @Origin);
                SELECT LAST_INSERT_ID();
                """, new { Name = name, Category = category, Origin = origin });

        public async Task<long> CreateCardAsync(bool withFiles = false, string mediaType = "image")
        {
            var id = await Connection.ExecuteScalarAsync<long>("""
                INSERT INTO cards (type, title, metadata) VALUES ('media', 'learned', JSON_OBJECT('media_type', @MediaType));
                SELECT LAST_INSERT_ID();
                """, new { MediaType = mediaType });
            if (!withFiles) return id;
            await Connection.ExecuteAsync("""
                UPDATE cards SET metadata = JSON_OBJECT('media_type', 'image',
                    'content_path', @Content, 'preview_path', @Preview) WHERE id = @Id;
                """, new { Content = $"cards/{id}/original.png", Preview = $"cache/cards/{id}/preview.webp", Id = id });
            foreach (var (path, bytes) in new[] { ($"cards/{id}/original.png", new byte[] { 1, 2 }),
                         ($"cache/cards/{id}/preview.webp", new byte[] { 3, 4 }) })
            {
                var full = Path.Combine(_root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllBytesAsync(full, bytes);
            }
            return id;
        }

        public Task SetJobAsync(long cardId, string state, string? revision = null) => Connection.ExecuteAsync("""
            INSERT INTO card_image_analysis_jobs (card_id, revision, state, attempts, available_at_utc)
            VALUES (@CardId, @Revision, @State, 1, UTC_TIMESTAMP(6));
            """, new { CardId = cardId, Revision = revision ?? Guid.NewGuid().ToString("D"), State = state });

        /// <summary>A media card analysed by the fake feature source: a done job and features for its revision.</summary>
        public async Task<long> CreateFeaturedCardAsync(VisualFeatureVector features)
        {
            var cardId = await CreateCardAsync();
            var revision = Guid.NewGuid().ToString("D");
            await SetJobAsync(cardId, "done", revision);
            await using var transaction = await Connection.BeginTransactionAsync();
            await LearnedTagRepository.SaveFeaturesAsync(Connection, transaction, cardId, revision, FeatureModel,
                features);
            await transaction.CommitAsync();
            return cardId;
        }

        public Task<SuggestionRow?> SuggestionAsync(long cardId, long tagId) =>
            Connection.QuerySingleOrDefaultAsync<SuggestionRow?>("""
                SELECT state, suggestion_source AS Source, model_key AS ModelKey, confidence
                FROM card_tag_suggestions WHERE card_id = @CardId AND tag_id = @TagId;
                """, new { CardId = cardId, TagId = tagId });

        public Task<string?> AssignmentAsync(long cardId, long tagId) => Connection.ExecuteScalarAsync<string?>(
            "SELECT assignment_source FROM card_tags WHERE card_id = @CardId AND tag_id = @TagId;",
            new { CardId = cardId, TagId = tagId });

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await using var admin = new MySqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE `{_name}`;");
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}

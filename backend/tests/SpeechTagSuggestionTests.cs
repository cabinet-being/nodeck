using Dapper;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Analysis;
using MyApp.Api.Catalog;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class SpeechTagSuggestionTests
{
    [DatabaseFact]
    public async Task EnglishPhrasePublishesOneLowConfidenceProposalWithNumericEvidenceOnly()
    {
        await using var scope = await Scope.CreateAsync();
        var tagId = await scope.TagAsync("blue_hair", "general", "danbooru");
        await scope.QueueAsync();
        var lease = await scope.Queue.ClaimAsync();
        Assert.NotNull(lease);

        var prepared = await scope.PrepareAsync("en", true,
            [new SpeechSegment(0.5m, 1.5m, "She has blue hair. private spoken words")]);
        Assert.Equal(tagId, Assert.Single(prepared.Proposals!).TagId);
        Assert.InRange(prepared.Proposals![0].Confidence, 0.001m, 0.72m);
        Assert.DoesNotContain("blue hair", prepared.EvidenceJson ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private spoken words", prepared.EvidenceJson ?? "", StringComparison.Ordinal);
        using (var evidence = JsonDocument.Parse(prepared.EvidenceJson!))
        {
            var item = evidence.RootElement.EnumerateArray().Single();
            Assert.Equal(JsonValueKind.Number, item.GetProperty("tag_id").ValueKind);
            Assert.Equal(JsonValueKind.Number, item.GetProperty("start").ValueKind);
            Assert.Equal(JsonValueKind.Number, item.GetProperty("end").ValueKind);
        }

        Assert.True(await scope.Queue.CompleteAsync(lease, prepared));
        var suggestion = Assert.Single(await scope.SuggestionsAsync());
        Assert.Equal((tagId, "pending", "speech"),
            (suggestion.TagId, suggestion.State, suggestion.Source));
        Assert.Equal(prepared.Proposals[0].Confidence, suggestion.Confidence);
        Assert.Equal(SpeechAnalysisQueueRepository.SpeechProposalModelKey, suggestion.ModelKey);
        var persisted = await scope.Connection.QuerySingleAsync<(string? EvidenceJson, string? LastError)>(
            "SELECT evidence_json AS EvidenceJson, last_error AS LastError FROM card_speech_analysis_jobs WHERE card_id = @CardId;",
            new { scope.CardId });
        Assert.DoesNotContain("blue hair", persisted.EvidenceJson ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private spoken words", persisted.EvidenceJson ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("private spoken words", persisted.LastError ?? "", StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task AliasAndCustomNameReuseExistingTags()
    {
        await using var scope = await Scope.CreateAsync();
        var aliasedTag = await scope.TagAsync("azure_hair", "general", "danbooru");
        await scope.Connection.ExecuteAsync(
            "INSERT INTO tag_aliases (tag_id, alias, origin) VALUES (@TagId, 'blue_hair', 'danbooru');",
            new { TagId = aliasedTag });
        var customTag = await scope.TagAsync("scarlet cloak", "custom", "user");
        await scope.SeedPriorManualUseAsync(customTag);

        var prepared = await scope.PrepareAsync("en", true,
            [new SpeechSegment(0, 2, "Blue hair. Scarlet cloak.")]);

        Assert.Equal(new[] { aliasedTag, customTag },
            prepared.Proposals!.Select(item => item.TagId).Order().ToArray());
        Assert.All(prepared.Proposals!, item => Assert.InRange(item.Confidence, 0.001m, 0.72m));
        await scope.QueueAsync();
        var lease = await scope.Queue.ClaimAsync();
        Assert.NotNull(lease);
        Assert.True(await scope.Queue.CompleteAsync(lease, prepared));
        Assert.Equal(new[] { aliasedTag, customTag },
            (await scope.SuggestionsAsync()).Select(item => item.TagId).Order().ToArray());
    }

    [DatabaseFact]
    public async Task NonEnglishAndEmptyRecognitionHaveNoProposals()
    {
        await using var scope = await Scope.CreateAsync();
        await scope.TagAsync("blue_hair", "general", "danbooru");

        var nonEnglish = await scope.PrepareAsync("uk", false,
            [new SpeechSegment(0, 1, "blue hair")]);
        var empty = await scope.PrepareAsync(null, null, []);

        Assert.Empty(nonEnglish.Proposals ?? []);
        Assert.Empty(empty.Proposals ?? []);
    }

    [DatabaseFact]
    public async Task PublicationPreservesVisualPendingReviewedAndManualTags()
    {
        await using var scope = await Scope.CreateAsync();
        var speechTag = await scope.TagAsync("blue_hair", "general", "danbooru");
        var visualTag = await scope.TagAsync("green_eyes", "general", "danbooru");
        var acceptedTag = await scope.TagAsync("red_dress", "general", "danbooru");
        var rejectedTag = await scope.TagAsync("white_gloves", "general", "danbooru");
        var manualTag = await scope.TagAsync("black_shoes", "general", "danbooru");
        foreach (var (tagId, state) in new[]
                 { (visualTag, "pending"), (acceptedTag, "accepted"), (rejectedTag, "rejected") })
            await scope.Connection.ExecuteAsync("""
                INSERT INTO card_tag_suggestions (card_id, tag_id, state, confidence, model_key)
                VALUES (@CardId, @TagId, @State, 0.9, 'visual-fixture');
                """, new { scope.CardId, TagId = tagId, State = state });
        await scope.Connection.ExecuteAsync(
            "INSERT INTO card_tags (card_id, tag_id) VALUES (@CardId, @TagId);",
            new { scope.CardId, TagId = manualTag });
        await scope.QueueAsync();
        var lease = await scope.Queue.ClaimAsync();
        Assert.NotNull(lease);
        var prepared = await scope.PrepareAsync("en", true,
            [new SpeechSegment(0, 2, "Blue hair. Green eyes. Red dress. White gloves. Black shoes.")]);

        Assert.True(await scope.Queue.CompleteAsync(lease, prepared));
        var rows = await scope.SuggestionsAsync();
        Assert.Contains(rows, row => row.TagId == speechTag && row.State == "pending" && row.Source == "speech");
        Assert.Contains(rows, row => row.TagId == visualTag && row.State == "pending" && row.Source == "visual" && row.ModelKey == "visual-fixture");
        Assert.Contains(rows, row => row.TagId == acceptedTag && row.State == "accepted" && row.Source == "visual");
        Assert.Contains(rows, row => row.TagId == rejectedTag && row.State == "rejected" && row.Source == "visual");
        Assert.DoesNotContain(rows, row => row.TagId == manualTag);
        Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_tags WHERE card_id = @CardId AND tag_id = @TagId;",
            new { scope.CardId, TagId = manualTag }));
    }

    [DatabaseFact]
    public async Task ReplacementAndStaleLeaseCannotPublishPreparedProposals()
    {
        await using var scope = await Scope.CreateAsync();
        await scope.TagAsync("blue_hair", "general", "danbooru");
        await scope.QueueAsync();
        var oldLease = await scope.Queue.ClaimAsync();
        Assert.NotNull(oldLease);
        var prepared = await scope.PrepareAsync("en", true,
            [new SpeechSegment(0, 1, "blue hair")]);
        Assert.Single(prepared.Proposals!);

        await scope.QueueAsync();
        Assert.False(await scope.Queue.CompleteAsync(oldLease, prepared));
        Assert.Empty(await scope.SuggestionsAsync());

        var newLease = await scope.Queue.ClaimAsync();
        Assert.NotNull(newLease);
        await scope.Connection.ExecuteAsync("""
            UPDATE cards SET metadata = JSON_SET(metadata, '$.replacement', TRUE)
            WHERE id = @CardId;
            """, new { scope.CardId });
        Assert.False(await scope.Queue.CompleteAsync(newLease, prepared));
        Assert.Empty(await scope.SuggestionsAsync());
    }

    [DatabaseFact]
    public async Task VisualUpsertTakesOwnershipOfPendingSpeechSuggestion()
    {
        await using var scope = await Scope.CreateAsync();
        var tagId = await scope.TagAsync("blue_hair", "general", "danbooru");
        await scope.QueueAsync();
        var lease = await scope.Queue.ClaimAsync();
        Assert.NotNull(lease);
        var prepared = await scope.PrepareAsync("en", true,
            [new SpeechSegment(0, 1, "blue hair")]);
        Assert.True(await scope.Queue.CompleteAsync(lease, prepared));
        Assert.Equal("speech", Assert.Single(await scope.SuggestionsAsync()).Source);

        await new CardCatalogRepository(scope.Configuration).UpsertSuggestionsAsync(scope.CardId,
            [new TagSuggestionProposal(tagId, 0.95m, "visual-fixture")]);

        var suggestion = Assert.Single(await scope.SuggestionsAsync());
        Assert.Equal("visual", suggestion.Source);
        Assert.Equal(0.95m, suggestion.Confidence);
        Assert.Equal("visual-fixture", suggestion.ModelKey);
    }

    private sealed record Suggestion(long TagId, string State, string Source,
        decimal? Confidence, string? ModelKey);

    private sealed class FakeRecognizer(SpeechRecognitionResult result) : ISpeechRecognizer
    {
        public Task<SpeechRecognitionResult> RecognizeAsync(string originalPath,
            double durationSeconds, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class Scope : IAsyncDisposable
    {
        private readonly string _root;
        private readonly List<long> _tagIds = [];
        private readonly List<long> _extraCardIds = [];

        private Scope(MySqlConnection connection, IConfiguration configuration, long cardId, string root)
        {
            Connection = connection;
            Configuration = configuration;
            CardId = cardId;
            _root = root;
            Queue = new SpeechAnalysisQueueRepository(configuration);
        }

        public MySqlConnection Connection { get; }
        public IConfiguration Configuration { get; }
        public long CardId { get; }
        public SpeechAnalysisQueueRepository Queue { get; }

        public static async Task<Scope> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
            var root = Path.Combine(Path.GetTempPath(), "nodeck-speech-tag-tests", Guid.NewGuid().ToString("N"));
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
                foreach (var statement in sql.Split(';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    await connection.ExecuteAsync(statement);
            }
            var cardId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO cards (type, title, metadata)
                VALUES ('media', @Title, JSON_OBJECT('media_type', 'video', 'has_audio', TRUE,
                    'duration', 8, 'content_path', 'pending'));
                SELECT LAST_INSERT_ID();
                """, new { Title = $"speech-tag-{Guid.NewGuid():N}" });
            await connection.ExecuteAsync("""
                UPDATE cards SET metadata = JSON_SET(metadata, '$.content_path', @ContentPath)
                WHERE id = @CardId;
                """, new { ContentPath = $"cards/{cardId}/original.mp4", CardId = cardId });
            var path = Path.Combine(root, "cards", cardId.ToString(), "original.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            return new Scope(connection, configuration, cardId, root);
        }

        public async Task<long> TagAsync(string name, string category, string origin)
        {
            var id = await Connection.ExecuteScalarAsync<long>("""
                INSERT INTO tags (name, canonical_key, category, origin)
                VALUES (@Name, @Key, @Category, @Origin);
                SELECT LAST_INSERT_ID();
                """, new { Name = name, Key = $"{name}_{Guid.NewGuid():N}", Category = category,
                Origin = origin });
            _tagIds.Add(id);
            return id;
        }

        public async Task QueueAsync()
        {
            await using var transaction = await Connection.BeginTransactionAsync();
            await SpeechAnalysisQueueRepository.RefreshAsync(Connection, transaction, CardId, true);
            await transaction.CommitAsync();
        }

        public async Task SeedPriorManualUseAsync(long tagId)
        {
            var cardId = await Connection.ExecuteScalarAsync<long>("""
                INSERT INTO cards (type, title, metadata)
                VALUES ('media', @Title, JSON_OBJECT('media_type', 'image'));
                SELECT LAST_INSERT_ID();
                """, new { Title = $"speech-tag-prior-{Guid.NewGuid():N}" });
            _extraCardIds.Add(cardId);
            await Connection.ExecuteAsync("""
                INSERT INTO card_tags (card_id, tag_id, assignment_source)
                VALUES (@CardId, @TagId, 'manual');
                """, new { CardId = cardId, TagId = tagId });
        }

        public Task<PreparedCardSpeechAnalysis> PrepareAsync(string? language, bool? english,
            IReadOnlyList<SpeechSegment> segments) =>
            new CardSpeechAnalysisService(Configuration, new FakeRecognizer(
                new SpeechRecognitionResult(english, english, language, 2m, false,
                    "fixture:v1", segments))).PrepareAsync(CardId);

        public async Task<Suggestion[]> SuggestionsAsync() =>
            (await Connection.QueryAsync<Suggestion>("""
                SELECT tag_id AS TagId, state AS State, suggestion_source AS Source,
                    confidence AS Confidence, model_key AS ModelKey
                FROM card_tag_suggestions WHERE card_id = @CardId ORDER BY tag_id;
                """, new { CardId })).ToArray();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Connection.ExecuteAsync("DELETE FROM cards WHERE id = @CardId;", new { CardId });
                if (_extraCardIds.Count > 0)
                    await Connection.ExecuteAsync("DELETE FROM cards WHERE id IN @Ids;", new { Ids = _extraCardIds });
                if (_tagIds.Count > 0)
                    await Connection.ExecuteAsync("DELETE FROM tags WHERE id IN @Ids;", new { Ids = _tagIds });
            }
            finally
            {
                await Connection.DisposeAsync();
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
        }
    }
}

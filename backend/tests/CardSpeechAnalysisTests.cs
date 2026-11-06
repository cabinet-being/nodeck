using Dapper;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Analysis;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class CardSpeechAnalysisTests
{
    [DatabaseFact]
    public async Task AudioVideoPreparesFlagsWithoutPersistingTranscriptOrEvidence()
    {
        await using var scope = await TestScope.CreateAsync();
        await scope.QueueAsync();
        var recognizer = new FakeRecognizer(new SpeechRecognitionResult(
            true, true, "en", 3.5m, false, "fake-speech:v1",
            [new SpeechSegment(0.5m, 1.5m, "private spoken words")]
        ));

        var prepared = await scope.Service(recognizer).PrepareAsync(scope.CardId, CancellationToken.None);

        Assert.Equal(scope.OriginalPath, recognizer.SeenPath);
        Assert.Equal(8d, recognizer.SeenDuration);
        Assert.Equal(await scope.MetadataAsync(), prepared.Metadata);
        Assert.True(prepared.SpeechDetected);
        Assert.True(prepared.EnglishSpeechDetected);
        Assert.Equal("en", prepared.Language);
        Assert.Equal(3.5m, prepared.ProcessedSeconds);
        Assert.False(prepared.Truncated);
        Assert.Equal("fake-speech:v1", prepared.ModelKey);
        Assert.DoesNotContain("private spoken words", prepared.EvidenceJson ?? "", StringComparison.Ordinal);
        var stored = await scope.Connection.QuerySingleAsync<(string State, string? EvidenceJson)>(
            "SELECT state AS State, evidence_json AS EvidenceJson FROM card_speech_analysis_jobs WHERE card_id = @CardId;",
            new { scope.CardId });
        Assert.Equal("pending", stored.State);
        Assert.Null(stored.EvidenceJson);
    }

    [DatabaseFact]
    public async Task SilentVideoIsRejectedBeforeRecognition()
    {
        await using var scope = await TestScope.CreateAsync(hasAudio: false);
        var recognizer = FakeRecognizer.Default();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Service(recognizer).PrepareAsync(scope.CardId, CancellationToken.None));

        Assert.Null(recognizer.SeenPath);
    }

    [DatabaseFact]
    public async Task NonVideoIsRejectedBeforeRecognition()
    {
        await using var scope = await TestScope.CreateAsync(mediaType: "image");
        var recognizer = FakeRecognizer.Default();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Service(recognizer).PrepareAsync(scope.CardId, CancellationToken.None));

        Assert.Null(recognizer.SeenPath);
    }

    [DatabaseFact]
    public async Task ChangedOriginalBytesDuringRecognitionAreRejected()
    {
        await using var scope = await TestScope.CreateAsync();
        var recognizer = FakeRecognizer.Default(_ =>
            File.WriteAllBytes(scope.OriginalPath, [9, 8, 7, 6]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Service(recognizer).PrepareAsync(scope.CardId, CancellationToken.None));

        Assert.Equal(scope.OriginalPath, recognizer.SeenPath);
    }

    [DatabaseFact]
    public async Task ChangedMetadataDuringRecognitionIsRejected()
    {
        await using var scope = await TestScope.CreateAsync();
        var recognizer = FakeRecognizer.Default(_ => scope.Connection.Execute(
            "UPDATE cards SET metadata = JSON_SET(metadata, '$.duration', 9) WHERE id = @CardId;",
            new { scope.CardId }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Service(recognizer).PrepareAsync(scope.CardId, CancellationToken.None));

        Assert.Equal(scope.OriginalPath, recognizer.SeenPath);
    }

    [DatabaseFact]
    public async Task UnsafeOriginalPathIsRejectedBeforeRecognition()
    {
        await using var scope = await TestScope.CreateAsync();
        await scope.Connection.ExecuteAsync(
            "UPDATE cards SET metadata = JSON_SET(metadata, '$.content_path', '../outside.mp4') WHERE id = @CardId;",
            new { scope.CardId });
        var recognizer = FakeRecognizer.Default();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Service(recognizer).PrepareAsync(scope.CardId, CancellationToken.None));

        Assert.Null(recognizer.SeenPath);
    }

    private sealed class FakeRecognizer(
        SpeechRecognitionResult result, Action<string>? onRecognize = null) : ISpeechRecognizer
    {
        public string? SeenPath { get; private set; }
        public double? SeenDuration { get; private set; }

        public static FakeRecognizer Default(Action<string>? onRecognize = null) => new(
            new SpeechRecognitionResult(true, true, "en", 2m, false, "fake-speech:v1", []),
            onRecognize);

        public Task<SpeechRecognitionResult> RecognizeAsync(
            string originalPath, double durationSeconds, CancellationToken cancellationToken)
        {
            SeenPath = originalPath;
            SeenDuration = durationSeconds;
            onRecognize?.Invoke(originalPath);
            return Task.FromResult(result);
        }
    }

    private sealed class TestScope : IAsyncDisposable
    {
        private TestScope(MySqlConnection connection, IConfiguration configuration, long cardId, string root)
        {
            Connection = connection;
            Configuration = configuration;
            CardId = cardId;
            Root = root;
            OriginalPath = Path.Combine(root, "cards", cardId.ToString(), "original.mp4");
        }

        public MySqlConnection Connection { get; }
        public IConfiguration Configuration { get; }
        public long CardId { get; }
        public string Root { get; }
        public string OriginalPath { get; }

        public static async Task<TestScope> CreateAsync(string mediaType = "video", bool hasAudio = true)
        {
            var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
            var root = Path.Combine(Path.GetTempPath(), "nodeck-speech-analysis-tests", Guid.NewGuid().ToString("N"));
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
                VALUES ('media', @Title, JSON_OBJECT('media_type', @MediaType,
                    'has_audio', JSON_EXTRACT(@AudioJson, '$'),
                    'duration', 8, 'content_path', 'pending'));
                SELECT LAST_INSERT_ID();
                """, new { Title = $"speech-{Guid.NewGuid():N}", MediaType = mediaType,
                    AudioJson = hasAudio ? "true" : "false" });
            var scope = new TestScope(connection, configuration, cardId, root);
            await connection.ExecuteAsync("""
                UPDATE cards SET metadata = JSON_SET(metadata, '$.content_path', @ContentPath)
                WHERE id = @CardId;
                """, new { ContentPath = $"cards/{cardId}/original.mp4", CardId = cardId });
            Directory.CreateDirectory(Path.GetDirectoryName(scope.OriginalPath)!);
            await File.WriteAllBytesAsync(scope.OriginalPath, [1, 2, 3, 4]);
            return scope;
        }

        public CardSpeechAnalysisService Service(ISpeechRecognizer recognizer) =>
            new(Configuration, recognizer);

        public async Task<string> MetadataAsync() =>
            await Connection.ExecuteScalarAsync<string>(
                "SELECT metadata FROM cards WHERE id = @CardId;", new { CardId })
            ?? throw new InvalidOperationException("Speech analysis test card is missing.");

        public async Task QueueAsync()
        {
            await using var transaction = await Connection.BeginTransactionAsync();
            await SpeechAnalysisQueueRepository.RefreshAsync(Connection, transaction, CardId, true);
            await transaction.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try { await Connection.ExecuteAsync("DELETE FROM cards WHERE id = @CardId;", new { CardId }); }
            finally
            {
                await Connection.DisposeAsync();
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
        }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Analysis;
using MyApp.Api.Cards;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class SpeechAnalysisQueueTests
{
    [DatabaseFact]
    public async Task CardUploadsQueueOnlyVideoWithAudioAndSilentReplacementRemovesJob()
    {
        await using var scope = await QueueScope.CreateAsync();
        var audioVideo = await scope.CreateMediaAsync("video", hasAudio: true);
        Assert.Equal("pending", (await scope.Queue.GetStatusAsync(audioVideo))?.State);
        var lease = await scope.Queue.ClaimAsync();
        Assert.NotNull(lease);
        Assert.Equal(audioVideo, lease.CardId);

        var silentVideo = await scope.CreateMediaAsync("video", hasAudio: false);
        var gif = await scope.CreateMediaAsync("gif", hasAudio: false);
        Assert.Null(await scope.Queue.GetStatusAsync(silentVideo));
        Assert.Null(await scope.Queue.GetStatusAsync(gif));

        Assert.NotNull(await scope.Cards.UpdateAsync(audioVideo,
            new UpdateCardData("silent replacement", null, [], QueueScope.Assets(audioVideo, "video", false))));
        Assert.Null(await scope.Queue.GetStatusAsync(audioVideo));
        Assert.False(await scope.Queue.RenewAsync(lease));
        Assert.Null(await scope.Queue.ClaimAsync());
    }

    [DatabaseFact]
    public async Task RefreshClaimRenewAndCompleteStoreSpeechResultWithoutExposingLease()
    {
        await using var scope = await QueueScope.CreateAsync();
        var cardId = await scope.InsertCardAsync();
        await scope.RefreshAsync(cardId, analyzeAudio: true);

        var pending = await scope.Queue.GetStatusAsync(cardId);
        Assert.NotNull(pending);
        Assert.Equal("pending", pending.State);
        Assert.Equal(0, pending.Attempts);
        Assert.Null(pending.SpeechDetected);
        Assert.Null(pending.EnglishSpeechDetected);

        var lease = await scope.Queue.ClaimAsync();
        Assert.NotNull(lease);
        Assert.Equal(cardId, lease.CardId);
        Assert.Equal(1, lease.Attempts);
        Assert.Null(await scope.Queue.ClaimAsync());
        Assert.True(await scope.Queue.RenewAsync(lease));
        Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM card_speech_analysis_jobs
            WHERE card_id = @CardId AND lease_until_utc > UTC_TIMESTAMP(6);
            """, new { CardId = cardId }));

        var prepared = new PreparedCardSpeechAnalysis(await scope.MetadataAsync(cardId),
            true, true, "en", 12.5m, false, "fixture:v1", "[]");
        Assert.True(await scope.Queue.CompleteAsync(lease, prepared));
        Assert.False(await scope.Queue.RenewAsync(lease));
        var done = await scope.Queue.GetStatusAsync(cardId);
        Assert.NotNull(done);
        Assert.Equal("done", done.State);
        Assert.True(done.SpeechDetected);
        Assert.True(done.EnglishSpeechDetected);
        Assert.Equal("en", done.Language);
        Assert.Equal(12.5m, done.ProcessedSeconds);
        Assert.False(done.Truncated);
        Assert.Equal("fixture:v1", done.ModelKey);
        var serialized = JsonSerializer.Serialize(done);
        Assert.DoesNotContain("LeaseToken", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Revision", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("EvidenceJson", serialized, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task ReplacementInvalidatesOldLeaseAndResetsResultsToUnknown()
    {
        await using var scope = await QueueScope.CreateAsync();
        var cardId = await scope.InsertCardAsync();
        await scope.RefreshAsync(cardId, analyzeAudio: true);
        var oldLease = await scope.Queue.ClaimAsync();
        Assert.NotNull(oldLease);
        var oldMetadata = await scope.MetadataAsync(cardId);

        await scope.Connection.ExecuteAsync("""
            UPDATE cards SET metadata = JSON_SET(metadata, '$.replacement', TRUE)
            WHERE id = @CardId;
            """, new { CardId = cardId });
        await scope.RefreshAsync(cardId, analyzeAudio: true);

        Assert.False(await scope.Queue.RenewAsync(oldLease));
        Assert.False(await scope.Queue.FailAsync(oldLease, new Exception("stale")));
        Assert.False(await scope.Queue.CompleteAsync(oldLease,
            new PreparedCardSpeechAnalysis(oldMetadata, true, true, "en", 1m, false, "fixture:v1", "[]")));
        var replacement = await scope.Queue.GetStatusAsync(cardId);
        Assert.NotNull(replacement);
        Assert.Equal("pending", replacement.State);
        Assert.Equal(0, replacement.Attempts);
        Assert.Null(replacement.SpeechDetected);
        Assert.Null(replacement.EnglishSpeechDetected);
        Assert.Null(replacement.Language);
        Assert.Null(replacement.ModelKey);

        var newLease = await scope.Queue.ClaimAsync();
        Assert.NotNull(newLease);
        Assert.NotEqual(oldLease.Revision, newLease.Revision);
        Assert.True(await scope.Queue.CompleteAsync(newLease,
            new PreparedCardSpeechAnalysis(await scope.MetadataAsync(cardId),
                null, null, null, 0m, false, "fixture:v1", null)));
        var unknown = await scope.Queue.GetStatusAsync(cardId);
        Assert.NotNull(unknown);
        Assert.Equal("done", unknown.State);
        Assert.Null(unknown.SpeechDetected);
        Assert.Null(unknown.EnglishSpeechDetected);
    }

    [DatabaseFact]
    public async Task NoAudioRefreshRemovesJobAndInvalidatesItsLease()
    {
        await using var scope = await QueueScope.CreateAsync();
        var cardId = await scope.InsertCardAsync();
        await scope.RefreshAsync(cardId, analyzeAudio: true);
        var lease = await scope.Queue.ClaimAsync();
        Assert.NotNull(lease);

        await scope.RefreshAsync(cardId, analyzeAudio: false);

        Assert.Null(await scope.Queue.GetStatusAsync(cardId));
        Assert.False(await scope.Queue.RenewAsync(lease));
        Assert.False(await scope.Queue.FailAsync(lease, new Exception("stale")));
        Assert.Null(await scope.Queue.ClaimAsync());
    }

    [DatabaseFact]
    public async Task FailureRetriesWithBackoffAndStopsAfterFiveClaims()
    {
        await using var scope = await QueueScope.CreateAsync();
        var cardId = await scope.InsertCardAsync();
        await scope.RefreshAsync(cardId, analyzeAudio: true);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var lease = await scope.Queue.ClaimAsync();
            Assert.NotNull(lease);
            Assert.Equal(attempt, lease.Attempts);
            Assert.True(await scope.Queue.FailAsync(lease, new Exception("recognizer unavailable")));
            var status = await scope.Queue.GetStatusAsync(cardId);
            Assert.NotNull(status);
            Assert.Equal(attempt == 5 ? "failed" : "pending", status.State);
            Assert.Equal(attempt, status.Attempts);
            Assert.Null(await scope.Queue.ClaimAsync());
            if (attempt < 5)
                await scope.Connection.ExecuteAsync("""
                    UPDATE card_speech_analysis_jobs
                    SET available_at_utc = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND
                    WHERE card_id = @CardId;
                    """, new { CardId = cardId });
        }
    }

    private sealed class QueueScope : IAsyncDisposable
    {
        private readonly List<long> _cardIds = [];

        private QueueScope(MySqlConnection connection, IConfiguration configuration)
        {
            Connection = connection;
            Queue = new SpeechAnalysisQueueRepository(configuration);
            Cards = new CardRepository(configuration);
        }

        public MySqlConnection Connection { get; }
        public SpeechAnalysisQueueRepository Queue { get; }
        public CardRepository Cards { get; }

        public static async Task<QueueScope> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString }).Build();
            var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null &&
                !File.Exists(Path.Combine(directory.FullName, "database/init/001_cards.sql")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            foreach (var file in new[] { "001_cards.sql", "002_catalog.sql" })
            {
                var sql = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "database/init", file));
                foreach (var statement in sql.Split(';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    await connection.ExecuteAsync(statement);
            }
            return new QueueScope(connection, configuration);
        }

        public async Task<long> CreateMediaAsync(string mediaType, bool hasAudio)
        {
            var result = await Cards.CreateAsync(
                new CreateCardData("media", "speech-queue-test", null, new JsonObject(), []),
                id => Task.FromResult<CardFileAssets?>(Assets(id, mediaType, hasAudio)), _ => { });
            _cardIds.Add(result.Id);
            return result.Id;
        }

        public static CardFileAssets Assets(long cardId, string mediaType, bool hasAudio) => new(
            $"cards/{cardId}/original.{(mediaType == "gif" ? "gif" : "mp4")}",
            $"cache/cards/{cardId}/preview.gif", mediaType,
            mediaType == "gif" ? "image/gif" : "video/mp4", "fixture", 4, 2, 2,
            Duration: 2, FrameCount: mediaType == "gif" ? 2 : null, HasAudio: hasAudio);

        public async Task<long> InsertCardAsync()
        {
            var cardId = await Connection.ExecuteScalarAsync<long>("""
                INSERT INTO cards (type, title, metadata)
                VALUES ('media', 'speech-queue-test', JSON_OBJECT('media_type', 'video', 'has_audio', TRUE));
                SELECT LAST_INSERT_ID();
                """);
            _cardIds.Add(cardId);
            return cardId;
        }

        public async Task RefreshAsync(long cardId, bool analyzeAudio)
        {
            await using var transaction = await Connection.BeginTransactionAsync();
            await SpeechAnalysisQueueRepository.RefreshAsync(Connection, transaction, cardId, analyzeAudio);
            await transaction.CommitAsync();
        }

        public async Task<string> MetadataAsync(long cardId) =>
            await Connection.ExecuteScalarAsync<string>(
                "SELECT metadata FROM cards WHERE id = @CardId;", new { CardId = cardId })
            ?? throw new InvalidOperationException("Speech queue test card is missing.");

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_cardIds.Count > 0)
                    await Connection.ExecuteAsync("DELETE FROM cards WHERE id IN @Ids;", new { Ids = _cardIds });
            }
            finally { await Connection.DisposeAsync(); }
        }
    }
}

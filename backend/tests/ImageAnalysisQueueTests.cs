using System.Text.Json.Nodes;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Analysis;
using MyApp.Api.Cards;
using MyApp.Api.Catalog;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class ImageAnalysisQueueTests
{
    [DatabaseFact]
    public async Task ImageCreationIsClaimedOnceAndFailureRetriesThenStopsAfterFiveAttempts()
    {
        await using var scope = await QueueScope.CreateAsync();
        var cardId = await scope.CreateAsync("image");
        var first = await scope.Queue.ClaimAsync(default);
        Assert.NotNull(first);
        Assert.Equal(cardId, first.CardId);
        Assert.Equal(1, first.Attempts);
        var publicStatus = await scope.Queue.GetStatusAsync(cardId, default);
        Assert.NotNull(publicStatus);
        Assert.Equal("leased", publicStatus.State);
        Assert.Equal(1, publicStatus.Attempts);
        var serializedStatus = JsonSerializer.Serialize(publicStatus);
        Assert.DoesNotContain("LeaseToken", serializedStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("Revision", serializedStatus, StringComparison.Ordinal);
        Assert.Null(await scope.Queue.ClaimAsync(default));

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var lease = attempt == 1 ? first : await scope.Queue.ClaimAsync(default);
            Assert.NotNull(lease);
            Assert.Equal(attempt, lease.Attempts);
            Assert.True(await scope.Queue.FailAsync(lease, new InvalidOperationException("tagger unavailable"), default));
            var state = await scope.Connection.ExecuteScalarAsync<string>(
                "SELECT state FROM card_image_analysis_jobs WHERE card_id = @CardId;", new { CardId = cardId });
            Assert.Equal(attempt == 5 ? "failed" : "pending", state);
            if (attempt < 5)
            {
                Assert.Null(await scope.Queue.ClaimAsync(default));
                await scope.Connection.ExecuteAsync("""
                    UPDATE card_image_analysis_jobs SET available_at_utc = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND
                    WHERE card_id = @CardId;
                    """, new { CardId = cardId });
            }
        }
        Assert.Null(await scope.Queue.ClaimAsync(default));
        Assert.Equal(5, await scope.Connection.ExecuteScalarAsync<int>(
            "SELECT attempts FROM card_image_analysis_jobs WHERE card_id = @CardId;", new { CardId = cardId }));
    }

    [DatabaseFact]
    public async Task ReplacementInvalidatesOldLeaseAndPendingSuggestionBeforeNewResultCanPublish()
    {
        await using var scope = await QueueScope.CreateAsync();
        var cardId = await scope.CreateAsync("image");
        var tagId = await scope.InsertTagAsync();
        var first = await scope.Queue.ClaimAsync(default);
        Assert.NotNull(first);
        await scope.Connection.ExecuteAsync("""
            INSERT INTO card_tag_suggestions (card_id, tag_id, state)
            VALUES (@CardId, @TagId, 'pending');
            """, new { CardId = cardId, TagId = tagId });
        var oldMetadata = await scope.MetadataAsync(cardId);
        var proposal = new TagSuggestionProposal(tagId, 0.9m, "fake:v1");
        var oldResult = new PreparedCardImageAnalysis(
            new CardImageAnalysisResult(cardId, 1, 1, 1), [proposal], oldMetadata);

        var updated = await scope.Cards.UpdateAsync(cardId,
            new UpdateCardData("replacement", null, [], QueueScope.Assets(cardId, "image")));
        Assert.NotNull(updated);
        Assert.Equal(0, await scope.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_tag_suggestions WHERE card_id = @CardId AND state = 'pending';",
            new { CardId = cardId }));
        Assert.False(await scope.Queue.CompleteAsync(first, oldResult, default));
        Assert.False(await scope.Queue.FailAsync(first, new Exception("stale"), default));

        var second = await scope.Queue.ClaimAsync(default);
        Assert.NotNull(second);
        Assert.Equal(cardId, second.CardId);
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.Equal(1, second.Attempts);
        Assert.True(await scope.Queue.CompleteAsync(second, new PreparedCardImageAnalysis(
            new CardImageAnalysisResult(cardId, 1, 1, 1), [proposal], await scope.MetadataAsync(cardId)), default));
        Assert.Equal("done", await scope.Connection.ExecuteScalarAsync<string>(
            "SELECT state FROM card_image_analysis_jobs WHERE card_id = @CardId;", new { CardId = cardId }));
        Assert.Equal(1, await scope.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_tag_suggestions WHERE card_id = @CardId AND tag_id = @TagId AND state = 'pending';",
            new { CardId = cardId, TagId = tagId }));
    }

    [DatabaseFact]
    public async Task GifAndVideoCreationEachQueueAnAnalysisJob()
    {
        await using var scope = await QueueScope.CreateAsync();

        foreach (var mediaType in new[] { "gif", "video" })
        {
            var cardId = await scope.CreateAsync(mediaType);
            var status = await scope.Queue.GetStatusAsync(cardId, default);
            Assert.NotNull(status);
            Assert.Equal("pending", status.State);
            Assert.Equal(0, status.Attempts);
            var lease = await scope.Queue.ClaimAsync(default);
            Assert.NotNull(lease);
            Assert.Equal(cardId, lease.CardId);
            Assert.True(await scope.Queue.FailAsync(lease,
                new InvalidOperationException("test failure"), default));
        }
    }

    [DatabaseFact]
    public async Task ReplacingImageWithGifInvalidatesLeaseAndQueuesGifWithoutOldSuggestions()
    {
        await using var scope = await QueueScope.CreateAsync();
        var cardId = await scope.CreateAsync("image");
        var tagId = await scope.InsertTagAsync();
        var oldLease = await scope.Queue.ClaimAsync(default);
        Assert.NotNull(oldLease);
        await scope.Connection.ExecuteAsync("""
            INSERT INTO card_tag_suggestions (card_id, tag_id, state)
            VALUES (@CardId, @TagId, 'pending');
            """, new { CardId = cardId, TagId = tagId });

        Assert.NotNull(await scope.Cards.UpdateAsync(cardId,
            new UpdateCardData("animation", null, [], QueueScope.Assets(cardId, "gif"))));

        Assert.False(await scope.Queue.FailAsync(oldLease, new Exception("stale"), default));
        var replacement = await scope.Queue.ClaimAsync(default);
        Assert.NotNull(replacement);
        Assert.Equal(cardId, replacement.CardId);
        Assert.NotEqual(oldLease.Revision, replacement.Revision);
        Assert.Equal(1, replacement.Attempts);
        Assert.Equal(0, await scope.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_tag_suggestions WHERE card_id = @CardId AND state = 'pending';",
            new { CardId = cardId }));
    }

    private sealed class QueueScope : IAsyncDisposable
    {
        private readonly List<long> _cards = [];
        private readonly List<long> _tags = [];

        private QueueScope(MySqlConnection connection, IConfiguration configuration)
        {
            Connection = connection;
            Cards = new CardRepository(configuration);
            Queue = new AnalysisQueueRepository(configuration);
        }

        public MySqlConnection Connection { get; }
        public CardRepository Cards { get; }
        public AnalysisQueueRepository Queue { get; }

        public static async Task<QueueScope> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString }).Build();
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
            return new QueueScope(connection, configuration);
        }

        public async Task<long> CreateAsync(string mediaType)
        {
            var result = await Cards.CreateAsync(new CreateCardData("media", "queue-test", null,
                new JsonObject(), []), id => Task.FromResult<CardFileAssets?>(Assets(id, mediaType)), _ => { });
            _cards.Add(result.Id);
            return result.Id;
        }

        public static CardFileAssets Assets(long id, string mediaType) => new(
            $"cards/{id}/original.{mediaType switch { "image" => "png", "video" => "mp4", _ => "gif" }}",
            $"cache/cards/{id}/preview.webp", mediaType,
            mediaType switch { "image" => "image/png", "video" => "video/mp4", _ => "image/gif" },
            "fixture", 4, 2, 2);

        public async Task<string> MetadataAsync(long cardId) =>
            await Connection.ExecuteScalarAsync<string>(
                "SELECT metadata FROM cards WHERE id = @CardId;", new { CardId = cardId })
            ?? throw new InvalidOperationException("Queue test card is missing.");

        public async Task<long> InsertTagAsync()
        {
            var key = $"queue_{Guid.NewGuid():N}";
            var id = await Connection.ExecuteScalarAsync<long>("""
                INSERT INTO tags (name, canonical_key, category, origin)
                VALUES (@Key, @Key, 'general', 'user');
                SELECT LAST_INSERT_ID();
                """, new { Key = key });
            _tags.Add(id);
            return id;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_cards.Count > 0)
                    await Connection.ExecuteAsync("DELETE FROM cards WHERE id IN @Ids;", new { Ids = _cards });
                if (_tags.Count > 0)
                    await Connection.ExecuteAsync("DELETE FROM tags WHERE id IN @Ids;", new { Ids = _tags });
            }
            finally { await Connection.DisposeAsync(); }
        }
    }
}

using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Cards;
using MyApp.Api.Decks;
using MyApp.Api.Storage;
using MySqlConnector;

namespace MyApp.Api.Tests;

[CollectionDefinition("Collection database", DisableParallelization = true)]
public sealed class CollectionDatabaseDefinition { }

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Skip = "Set NODECK_TEST_CONNECTION_STRING to a dedicated MySQL test database.";
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (!builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
            Skip = "NODECK_TEST_CONNECTION_STRING must name a dedicated test database.";
    }
}

[Collection("Collection database")]
public sealed class CollectionOwnershipTests
{
    [DatabaseFact]
    public async Task PreviewOverridePersistsManualMetadataAndRejectsOutOfRangeWithoutChangingFiles()
    {
        await using var scope = await TestScope.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "nodeck-preview-repository-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var card = await scope.Cards.CreateAsync(
                new CreateCardData("media", "GIF", null,
                    new JsonObject { ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") }, []),
                id => Task.FromResult<CardFileAssets?>(new CardFileAssets(
                    $"cards/{id}/original.gif", $"cache/cards/{id}/preview.gif", "gif", "image/gif",
                    "fixture.gif", 10, 32, 24, 6, 60,
                    PosterPath: $"cache/cards/{id}/poster.webp", PreviewStartSeconds: 2)),
                _ => { });
            scope.TrackCard(card.Id);
            var finalDirectory = Path.Combine(root, "cache", "cards", card.Id.ToString());
            Directory.CreateDirectory(finalDirectory);
            var previewPath = Path.Combine(finalDirectory, "preview.gif");
            var posterPath = Path.Combine(finalDirectory, "poster.webp");
            await File.WriteAllTextAsync(previewPath, "old preview");
            await File.WriteAllTextAsync(posterPath, "old poster");

            var prepared = false;
            var updated = await scope.Cards.UpdatePreviewStartAsync(card.Id, 0, async (id, contentPath, duration, start) =>
            {
                prepared = true;
                Assert.Equal(card.Id, id);
                Assert.Equal($"cards/{card.Id}/original.gif", contentPath);
                Assert.Equal(6, duration);
                Assert.Equal(0, start);
                var stagingRoot = Path.Combine(root, "tmp", "preview-overrides", Guid.NewGuid().ToString("N"));
                var stagingDirectory = Path.Combine(stagingRoot, "preview");
                Directory.CreateDirectory(stagingDirectory);
                await File.WriteAllTextAsync(Path.Combine(stagingDirectory, "preview.gif"), "new preview");
                await File.WriteAllTextAsync(Path.Combine(stagingDirectory, "poster.webp"), "new poster");
                return new PendingPreviewReplacement(stagingRoot, stagingDirectory, finalDirectory,
                    Path.Combine(root, "tmp", "preview-override-backups", Guid.NewGuid().ToString("N")), 0);
            });

            Assert.True(prepared);
            Assert.NotNull(updated);
            Assert.Equal(0, updated.Metadata["preview_start_seconds"]!.GetValue<double>());
            Assert.True(updated.Metadata["preview_start_manual"]!.GetValue<bool>());
            Assert.Equal("new preview", await File.ReadAllTextAsync(previewPath));
            Assert.Equal("new poster", await File.ReadAllTextAsync(posterPath));
            var storedMetadata = await scope.GetStoredMetadataAsync(card.Id);
            Assert.Equal(0, storedMetadata["preview_start_seconds"]!.GetValue<double>());
            Assert.True(storedMetadata["preview_start_manual"]!.GetValue<bool>());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scope.Cards.UpdatePreviewStartAsync(card.Id, 4, (_, _, _, _) =>
                    throw new Xunit.Sdk.XunitException("Invalid start must not prepare files.")));

            Assert.Equal(storedMetadata.ToJsonString(), (await scope.GetStoredMetadataAsync(card.Id)).ToJsonString());
            Assert.Equal("new preview", await File.ReadAllTextAsync(previewPath));
            Assert.Equal("new poster", await File.ReadAllTextAsync(posterPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [DatabaseFact]
    public async Task ExistingMediaCanBeGroupedAndReorderedWithoutDuplication()
    {
        await using var scope = await TestScope.CreateAsync();
        var first = await scope.CreateMediaAsync("first");
        var second = await scope.CreateMediaAsync("second");
        await scope.SetPreviewAsync(first, "cache/cards/first.webp");
        await scope.SetPreviewAsync(second, "cache/cards/second.webp");

        var comic = await scope.Cards.CreateCollectionFromExistingAsync(
            new CreateExistingCollectionRequest("comic", "Pages", [first, second]));
        scope.TrackCard(comic.Id);

        Assert.Equal(new[] { first, second }, comic.ContainedCards.Select(card => card.Id).ToArray());
        Assert.Equal("/api/cards/" + comic.Id + "/preview", comic.PreviewUrl);
        Assert.Equal(3, await scope.CountCardsAsync([first, second, comic.Id]));

        var reordered = await scope.Cards.UpdateCollectionChildrenAsync(comic.Id, [second, first]);
        Assert.NotNull(reordered);
        Assert.Equal(new[] { second, first }, reordered.ContainedCards.Select(card => card.Id).ToArray());
        Assert.Equal("cache/cards/second.webp", await scope.GetStoredPreviewAsync(comic.Id));
        Assert.Null(await scope.GetNextInSequenceAsync(first));
        Assert.Equal(first, await scope.GetNextInSequenceAsync(second));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Cards.CreateCollectionFromExistingAsync(
                new CreateExistingCollectionRequest("set", "Conflict", [first, second])));
        Assert.Equal(3, await scope.CountCardsAsync([first, second, comic.Id]));
    }

    [DatabaseFact]
    public async Task DeletingOnlyParentDetachesChildrenAndKeepsDeckLinks()
    {
        await using var scope = await TestScope.CreateAsync();
        var first = await scope.CreateMediaAsync("first");
        var second = await scope.CreateMediaAsync("second");
        var set = await scope.Cards.CreateCollectionFromExistingAsync(
            new CreateExistingCollectionRequest("set", "Group", [first, second]));
        scope.TrackCard(set.Id);
        var deck = await scope.Decks.CreateAsync(new DeckRequest("Test deck", null,
            [new DeckCardInput(first, null), new DeckCardInput(set.Id, null)]));
        scope.TrackDeck(deck.Id);

        Assert.Equal(new[] { set.Id }, (await scope.Cards.DeleteAsync(set.Id)).ToArray());
        Assert.NotNull(await scope.Cards.GetByIdAsync(first));
        Assert.NotNull(await scope.Cards.GetByIdAsync(second));
        Assert.Null(await scope.GetParentAsync(first));
        var remainingDeck = await scope.Decks.GetByIdAsync(deck.Id);
        Assert.NotNull(remainingDeck);
        Assert.Equal(new[] { first }, remainingDeck.Cards.Select(card => card.Id).ToArray());
    }

    [DatabaseFact]
    public async Task FullDeletionRemovesChildrenAndTheirDeckLinks()
    {
        await using var scope = await TestScope.CreateAsync();
        var first = await scope.CreateMediaAsync("first");
        var second = await scope.CreateMediaAsync("second");
        var comic = await scope.Cards.CreateCollectionFromExistingAsync(
            new CreateExistingCollectionRequest("comic", "Pages", [first, second]));
        scope.TrackCard(comic.Id);
        var deck = await scope.Decks.CreateAsync(new DeckRequest("Test deck", null,
            [new DeckCardInput(first, null), new DeckCardInput(comic.Id, null)]));
        scope.TrackDeck(deck.Id);

        var deleted = await scope.Cards.DeleteAsync(comic.Id, deleteContents: true);

        Assert.Equal(3, deleted.Count);
        Assert.Contains(first, deleted);
        Assert.Contains(second, deleted);
        Assert.Contains(comic.Id, deleted);
        Assert.Null(await scope.Cards.GetByIdAsync(first));
        Assert.Null(await scope.Cards.GetByIdAsync(second));
        var remainingDeck = await scope.Decks.GetByIdAsync(deck.Id);
        Assert.NotNull(remainingDeck);
        Assert.Empty(remainingDeck.Cards);
    }

    [DatabaseFact]
    public async Task DeletingOneChildRefreshesParentPreviewAndOrder()
    {
        await using var scope = await TestScope.CreateAsync();
        var first = await scope.CreateMediaAsync("first");
        var second = await scope.CreateMediaAsync("second");
        await scope.SetPreviewAsync(first, "cache/cards/first.webp");
        await scope.SetPreviewAsync(second, "cache/cards/second.webp");
        var comic = await scope.Cards.CreateCollectionFromExistingAsync(
            new CreateExistingCollectionRequest("comic", "Pages", [first, second]));
        scope.TrackCard(comic.Id);

        Assert.Equal(new[] { first }, (await scope.Cards.DeleteAsync(first)).ToArray());

        var updated = await scope.Cards.GetByIdAsync(comic.Id);
        Assert.NotNull(updated);
        Assert.Equal(new[] { second }, updated.ContainedCards.Select(card => card.Id).ToArray());
        Assert.Equal(0, updated.ContainedCards[0].Position);
        Assert.Equal("cache/cards/second.webp", await scope.GetStoredPreviewAsync(comic.Id));
        Assert.Null(await scope.GetNextInSequenceAsync(second));
    }

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly List<long> _cardIds = [];
        private readonly List<long> _deckIds = [];

        public CardRepository Cards { get; }
        public DeckRepository Decks { get; }

        private TestScope(string connectionString, IConfiguration configuration)
        {
            _connectionString = connectionString;
            Cards = new CardRepository(configuration);
            Decks = new DeckRepository(configuration);
        }

        public static async Task<TestScope> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")
                ?? throw new InvalidOperationException("NODECK_TEST_CONNECTION_STRING is required.");
            var builder = new MySqlConnectionStringBuilder(connectionString);
            if (!builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only a dedicated test database is allowed.");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString })
                .Build();
            await EnsureSchemaAsync(connectionString, configuration);
            return new TestScope(connectionString, configuration);
        }

        private static async Task EnsureSchemaAsync(string connectionString, IConfiguration configuration)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "database/init/001_cards.sql")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            var sql = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "database/init/001_cards.sql"));
            foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await connection.ExecuteAsync(statement);
            await new CardDatabaseInitializer(configuration).InitializeAsync();
            await new DeckDatabaseInitializer(configuration).InitializeAsync();
        }

        public async Task<long> CreateMediaAsync(string title)
        {
            var created = await Cards.CreateAsync(
                new CreateCardData("media", title, null,
                    new JsonObject { ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") }, []),
                _ => Task.FromResult<CardFileAssets?>(null), _ => { });
            TrackCard(created.Id);
            return created.Id;
        }

        public void TrackCard(long id) => _cardIds.Add(id);
        public void TrackDeck(long id) => _deckIds.Add(id);

        public async Task SetPreviewAsync(long id, string path)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.ExecuteAsync("UPDATE cards SET preview = @Path WHERE id = @Id;", new { Id = id, Path = path });
        }

        public async Task<string?> GetStoredPreviewAsync(long id)
        {
            await using var connection = new MySqlConnection(_connectionString);
            return await connection.QuerySingleOrDefaultAsync<string>("SELECT preview FROM cards WHERE id = @Id;", new { Id = id });
        }

        public async Task<JsonNode> GetStoredMetadataAsync(long id)
        {
            await using var connection = new MySqlConnection(_connectionString);
            var json = await connection.QuerySingleAsync<string>(
                "SELECT metadata FROM cards WHERE id = @Id;", new { Id = id });
            return JsonNode.Parse(json)!;
        }

        public async Task<long?> GetParentAsync(long childId)
        {
            await using var connection = new MySqlConnection(_connectionString);
            return await connection.QuerySingleOrDefaultAsync<long?>(
                "SELECT from_card_id FROM card_relations WHERE to_card_id = @ChildId AND relation_type = 'contains';",
                new { ChildId = childId });
        }

        public async Task<long?> GetNextInSequenceAsync(long id)
        {
            await using var connection = new MySqlConnection(_connectionString);
            return await connection.QuerySingleOrDefaultAsync<long?>(
                "SELECT to_card_id FROM card_relations WHERE from_card_id = @Id AND relation_type = 'next_in_sequence';",
                new { Id = id });
        }

        public async Task<int> CountCardsAsync(long[] ids)
        {
            await using var connection = new MySqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cards WHERE id IN @Ids;", new { Ids = ids });
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            if (_deckIds.Count > 0)
                await connection.ExecuteAsync("DELETE FROM decks WHERE id IN @Ids;", new { Ids = _deckIds });
            if (_cardIds.Count > 0)
                await connection.ExecuteAsync("DELETE FROM cards WHERE id IN @Ids;", new { Ids = _cardIds });
        }
    }
}

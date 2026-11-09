using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Decks;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class DeckMembershipTests
{
    [DatabaseFact]
    public async Task AddIsIdempotentAndAppendsInOrderWithoutReplacingMembershipProperties()
    {
        await using var scope = await DeckScope.CreateAsync();
        var first = await scope.InsertCardAsync("first");
        var second = await scope.InsertCardAsync("second");
        var third = await scope.InsertCardAsync("third");
        var properties = new JsonObject { ["caption"] = "keep me" };
        var deck = await scope.Repository.CreateAsync(new DeckRequest("Ordered", new JsonObject { ["theme"] = "blue" },
            [new DeckCardInput(first, properties)]));
        var firstMembershipMetadata = await scope.GetMembershipMetadataAsync(deck.Id, first);
        var deckMetadata = deck.Metadata.ToJsonString();

        var withSecond = await scope.Repository.AddCardAsync(deck.Id, second);
        Assert.NotNull(withSecond);
        Assert.Equal(new[] { first, second }, withSecond.Cards.Select(card => card.Id));
        Assert.Equal(new[] { 0, 1 }, withSecond.Cards.Select(card => card.Position));

        var duplicate = await scope.Repository.AddCardAsync(deck.Id, first);
        Assert.NotNull(duplicate);
        Assert.Equal(new[] { first, second }, duplicate.Cards.Select(card => card.Id));
        Assert.Equal(new[] { 0, 1 }, duplicate.Cards.Select(card => card.Position));
        Assert.Equal("keep me", duplicate.Cards[0].MembershipProperties?["caption"]?.GetValue<string>());
        Assert.Equal(firstMembershipMetadata, await scope.GetMembershipMetadataAsync(deck.Id, first));
        Assert.Equal(deckMetadata, duplicate.Metadata.ToJsonString());
        Assert.Equal("blue", duplicate.Properties?["theme"]?.GetValue<string>());

        var appended = await scope.Repository.AddCardAsync(deck.Id, third);
        Assert.NotNull(appended);
        Assert.Equal(new[] { first, second, third }, appended.Cards.Select(card => card.Id));
        Assert.Equal(new[] { 0, 1, 2 }, appended.Cards.Select(card => card.Position));
        Assert.NotNull(await scope.GetMembershipMetadataAsync(deck.Id, third));
    }

    [DatabaseFact]
    public async Task RemoveIsIdempotentAndCompactsPositionsWithoutDeletingCardOrOtherMembership()
    {
        await using var scope = await DeckScope.CreateAsync();
        var first = await scope.InsertCardAsync("first");
        var middle = await scope.InsertCardAsync("middle");
        var last = await scope.InsertCardAsync("last");
        var deck = await scope.Repository.CreateAsync(new DeckRequest("Primary", null,
            [new DeckCardInput(first, null), new DeckCardInput(middle, null),
                new DeckCardInput(last, new JsonObject { ["role"] = "last" })]));
        var other = await scope.Repository.CreateAsync(new DeckRequest("Other", null,
            [new DeckCardInput(middle, null)]));
        var lastMetadata = await scope.GetMembershipMetadataAsync(deck.Id, last);

        var removed = await scope.Repository.RemoveCardAsync(deck.Id, middle);
        Assert.NotNull(removed);
        Assert.Equal(new[] { first, last }, removed.Cards.Select(card => card.Id));
        Assert.Equal(new[] { 0, 1 }, removed.Cards.Select(card => card.Position));
        Assert.Equal("last", removed.Cards[1].MembershipProperties?["role"]?.GetValue<string>());
        Assert.Equal(lastMetadata, await scope.GetMembershipMetadataAsync(deck.Id, last));
        Assert.True(await scope.CardExistsAsync(middle));
        Assert.Equal(new[] { middle }, (await scope.Repository.GetByIdAsync(other.Id))!.Cards.Select(card => card.Id));

        var repeated = await scope.Repository.RemoveCardAsync(deck.Id, middle);
        Assert.NotNull(repeated);
        Assert.Equal(new[] { first, last }, repeated.Cards.Select(card => card.Id));
        Assert.Equal(new[] { 0, 1 }, repeated.Cards.Select(card => card.Position));
        Assert.Equal(lastMetadata, await scope.GetMembershipMetadataAsync(deck.Id, last));
    }

    [DatabaseFact]
    public async Task MissingDeckReturnsNullAndMissingCardCannotBeAdded()
    {
        await using var scope = await DeckScope.CreateAsync();
        var card = await scope.InsertCardAsync("exists");
        var deck = await scope.Repository.CreateAsync(new DeckRequest("Exists", null, []));

        Assert.Null(await scope.Repository.AddCardAsync(long.MaxValue, card));
        Assert.Null(await scope.Repository.RemoveCardAsync(long.MaxValue, card));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Repository.AddCardAsync(deck.Id, long.MaxValue));
        Assert.Empty((await scope.Repository.GetByIdAsync(deck.Id))!.Cards);
    }

    [DatabaseFact]
    public async Task FavoritesCannotBeEditedThroughOrdinaryMembershipMethods()
    {
        await using var scope = await DeckScope.CreateAsync();
        var card = await scope.InsertCardAsync("favorite");
        var favorites = await scope.Repository.GetFavoritesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Repository.AddCardAsync(favorites.Id, card));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Repository.RemoveCardAsync(favorites.Id, card));
        Assert.Empty((await scope.Repository.GetFavoritesAsync()).Cards);
    }

    [DatabaseFact]
    public async Task CardDeckListIncludesOnlyItsUserDecksAndTracksRemoval()
    {
        await using var scope = await DeckScope.CreateAsync();
        var card = await scope.InsertCardAsync("target");
        var anotherCard = await scope.InsertCardAsync("other");
        var first = await scope.Repository.CreateAsync(new DeckRequest("First", null,
            [new DeckCardInput(card, null)]));
        var second = await scope.Repository.CreateAsync(new DeckRequest("Second", null,
            [new DeckCardInput(anotherCard, null)]));
        var third = await scope.Repository.CreateAsync(new DeckRequest("Third", null, []));
        await scope.Repository.AddCardAsync(third.Id, card);
        await scope.Repository.AddFavoriteAsync(card);

        var memberships = await scope.Repository.GetCardDecksAsync(card);
        Assert.Equal(new[] { first.Id, third.Id }, memberships.Select(deck => deck.Id).OrderBy(id => id));
        Assert.DoesNotContain(memberships, deck => deck.SystemKey == DeckRepository.FavoritesSystemKey);
        Assert.DoesNotContain(memberships, deck => deck.Id == second.Id);

        await scope.Repository.RemoveCardAsync(first.Id, card);
        var remaining = await scope.Repository.GetCardDecksAsync(card);
        Assert.Equal(new[] { third.Id }, remaining.Select(deck => deck.Id));
        Assert.Equal("Third", remaining[0].Title);
        Assert.Equal(1L, remaining[0].CardCount);
        Assert.Empty(await scope.Repository.GetCardDecksAsync(long.MaxValue));
    }

    private sealed class DeckScope : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _name;
        public MySqlConnection Connection { get; }
        public DeckRepository Repository { get; }

        private DeckScope(string adminConnectionString, string name, MySqlConnection connection,
            IConfiguration configuration)
        {
            _adminConnectionString = adminConnectionString;
            _name = name;
            Connection = connection;
            Repository = new DeckRepository(configuration);
        }

        public static async Task<DeckScope> CreateAsync()
        {
            var builder = new MySqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!);
            if (!builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Deck membership tests require a dedicated test database.");
            var name = $"nodeck_deck_membership_{Guid.NewGuid():N}_test";
            builder.Database = "";
            var adminConnectionString = builder.ConnectionString;
            await using (var admin = new MySqlConnection(adminConnectionString))
            {
                await admin.OpenAsync();
                await admin.ExecuteAsync($"CREATE DATABASE `{name}`;");
            }

            builder.Database = name;
            var connection = new MySqlConnection(builder.ConnectionString);
            try
            {
                await connection.OpenAsync();
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "database/init/001_cards.sql")))
                    directory = directory.Parent;
                Assert.NotNull(directory);
                var sql = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "database/init/001_cards.sql"));
                foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    await connection.ExecuteAsync(statement);
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:Default"] = builder.ConnectionString }).Build();
                await new DeckDatabaseInitializer(configuration).InitializeAsync();
                return new DeckScope(adminConnectionString, name, connection, configuration);
            }
            catch
            {
                await connection.DisposeAsync();
                await using var admin = new MySqlConnection(adminConnectionString);
                await admin.OpenAsync();
                await admin.ExecuteAsync($"DROP DATABASE `{name}`;");
                throw;
            }
        }

        public Task<long> InsertCardAsync(string title) => Connection.ExecuteScalarAsync<long>("""
            INSERT INTO cards (type, title, metadata)
            VALUES ('media', @Title, JSON_OBJECT('created_at', '2026-01-01T00:00:00Z'));
            SELECT LAST_INSERT_ID();
            """, new { Title = title });

        public Task<string> GetMembershipMetadataAsync(long deckId, long cardId) =>
            Connection.QuerySingleAsync<string>("""
                SELECT metadata FROM deck_cards WHERE deck_id = @DeckId AND card_id = @CardId;
                """, new { DeckId = deckId, CardId = cardId });

        public Task<bool> CardExistsAsync(long cardId) => Connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM cards WHERE id = @CardId);", new { CardId = cardId });

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await using var admin = new MySqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE `{_name}`;");
        }
    }
}

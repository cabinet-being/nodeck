using Dapper;
using Microsoft.Extensions.Configuration;
using MyApp.Api.Decks;
using MyApp.Api.Interest;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class InterestSignalsTests
{
    [DatabaseFact]
    public async Task ViewEventIsIdempotentAndRejectsMissingOrInvalidCards()
    {
        await using var scope = await InterestScope.CreateAsync();
        var cardId = await scope.InsertCardAsync("media");
        var tagId = await scope.InsertCardAsync("tag");
        var eventId = Guid.NewGuid();

        Assert.Equal(InterestViewResult.Recorded,
            await scope.Repository.RecordViewAsync(cardId, eventId, 2000));
        Assert.Equal(InterestViewResult.Duplicate,
            await scope.Repository.RecordViewAsync(cardId, eventId, 600000));
        Assert.Equal(InterestViewResult.NotFound,
            await scope.Repository.RecordViewAsync(tagId, Guid.NewGuid(), 2000));
        Assert.Equal(InterestViewResult.NotFound,
            await scope.Repository.RecordViewAsync(long.MaxValue, Guid.NewGuid(), 2000));
        Assert.Equal(1, await scope.CountAsync("card_view_events"));
        Assert.Equal(2000, await scope.Connection.ExecuteScalarAsync<int>(
            "SELECT duration_ms FROM card_view_events WHERE event_id = @EventId;",
            new { EventId = eventId.ToString("D") }));
    }

    [DatabaseFact]
    public async Task ViewsValidateDurationAndKeepOnlyNewestFiveHundredEvents()
    {
        await using var scope = await InterestScope.CreateAsync();
        var cardId = await scope.InsertCardAsync("media");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            scope.Repository.RecordViewAsync(cardId, Guid.NewGuid(), 1999));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            scope.Repository.RecordViewAsync(cardId, Guid.NewGuid(), 600001));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            scope.Repository.RecordViewAsync(cardId, Guid.Empty, 2000));
        Assert.Equal(0, await scope.CountAsync("card_view_events"));

        var ids = Enumerable.Range(0, 502).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
            Assert.Equal(InterestViewResult.Recorded,
                await scope.Repository.RecordViewAsync(cardId, id, 2000));

        Assert.Equal(500, await scope.CountAsync("card_view_events"));
        Assert.Equal(0, await scope.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_view_events WHERE event_id IN @Ids;",
            new { Ids = ids.Take(2).Select(id => id.ToString("D")).ToArray() }));
        Assert.Equal(500, await scope.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_view_events WHERE event_id IN @Ids;",
            new { Ids = ids.Skip(2).Select(id => id.ToString("D")).ToArray() }));
    }

    [DatabaseFact]
    public async Task VoteUpdatesNeutralizesAndResetsOnNextLocalDay()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 7, 21, 59, 0, TimeSpan.Zero));
        await using var scope = await InterestScope.CreateAsync(clock);
        var cardId = await scope.InsertCardAsync("media");
        const string zone = "Europe/Kyiv";

        var empty = await scope.Repository.GetVoteAsync(cardId, zone);
        Assert.NotNull(empty);
        Assert.Equal("2026-11-07", empty.LocalDate);
        Assert.Null(empty.Value);
        Assert.Equal(zone, empty.TimeZone);

        Assert.Equal((sbyte)1, (await scope.Repository.PutVoteAsync(cardId, 1, zone))!.Value);
        Assert.Equal((sbyte)-1, (await scope.Repository.PutVoteAsync(cardId, -1, zone))!.Value);
        Assert.Equal((sbyte)0, (await scope.Repository.PutVoteAsync(cardId, 0, zone))!.Value);
        Assert.Equal((sbyte)0, (await scope.Repository.GetVoteAsync(cardId, zone))!.Value);
        Assert.Equal(1, await scope.CountAsync("card_daily_votes"));

        clock.UtcNow = new DateTimeOffset(2026, 11, 7, 22, 1, 0, TimeSpan.Zero);
        var nextDay = await scope.Repository.GetVoteAsync(cardId, zone);
        Assert.NotNull(nextDay);
        Assert.Equal("2026-11-08", nextDay.LocalDate);
        Assert.Null(nextDay.Value);
        Assert.Equal("2026-11-08", (await scope.Repository.PutVoteAsync(cardId, 1, zone))!.LocalDate);
        Assert.Equal(2, await scope.CountAsync("card_daily_votes"));
    }

    [DatabaseFact]
    public async Task VoteUsesZoneLocalDateAcrossDaylightSavingTransition()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero));
        await using var scope = await InterestScope.CreateAsync(clock);
        var cardId = await scope.InsertCardAsync("media");
        const string zone = "Europe/Kyiv";

        Assert.Equal("2026-03-29", (await scope.Repository.PutVoteAsync(cardId, 1, zone))!.LocalDate);
        clock.UtcNow = new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero);
        Assert.Equal((sbyte)1, (await scope.Repository.GetVoteAsync(cardId, zone))!.Value);
        Assert.Equal(1, await scope.CountAsync("card_daily_votes"));
        clock.UtcNow = new DateTimeOffset(2026, 3, 29, 21, 1, 0, TimeSpan.Zero);
        Assert.Equal("2026-03-30", (await scope.Repository.GetVoteAsync(cardId, zone))!.LocalDate);
        Assert.Null((await scope.Repository.GetVoteAsync(cardId, zone))!.Value);
    }

    [DatabaseFact]
    public async Task InvalidVotesAndZonesNeverPersistAndMissingCardHasNoStatus()
    {
        await using var scope = await InterestScope.CreateAsync();
        var cardId = await scope.InsertCardAsync("media");
        var tagId = await scope.InsertCardAsync("tag");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            scope.Repository.PutVoteAsync(cardId, 2, "UTC"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            scope.Repository.PutVoteAsync(cardId, -2, "UTC"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            scope.Repository.GetVoteAsync(cardId, "not/a-real-zone"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            scope.Repository.PutVoteAsync(cardId, 1, ""));
        Assert.Null(await scope.Repository.GetVoteAsync(tagId, "UTC"));
        Assert.Null(await scope.Repository.PutVoteAsync(long.MaxValue, 1, "UTC"));
        Assert.Equal(0, await scope.CountAsync("card_daily_votes"));
    }

    [DatabaseFact]
    public async Task InterestRowsCascadeOnCardDeleteAndDoNotChangeFavorites()
    {
        await using var scope = await InterestScope.CreateAsync();
        var cardId = await scope.InsertCardAsync("media");
        var otherId = await scope.InsertCardAsync("media");
        var favorites = new DeckRepository(scope.Configuration);
        await favorites.AddFavoriteAsync(cardId);

        await scope.Repository.RecordViewAsync(cardId, Guid.NewGuid(), 2000);
        await scope.Repository.PutVoteAsync(cardId, -1, "UTC");
        await scope.Repository.RecordViewAsync(otherId, Guid.NewGuid(), 2000);
        await scope.Repository.PutVoteAsync(otherId, 1, "UTC");
        Assert.Contains((await favorites.GetFavoritesAsync()).Cards, card => card.Id == cardId);

        await scope.Connection.ExecuteAsync("DELETE FROM cards WHERE id = @CardId;", new { CardId = cardId });

        Assert.Equal(0, await scope.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_view_events WHERE card_id = @CardId;", new { CardId = cardId }));
        Assert.Equal(0, await scope.Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_daily_votes WHERE card_id = @CardId;", new { CardId = cardId }));
        Assert.Equal(1, await scope.CountAsync("card_view_events"));
        Assert.Equal(1, await scope.CountAsync("card_daily_votes"));
        Assert.DoesNotContain((await favorites.GetFavoritesAsync()).Cards, card => card.Id == cardId);
        Assert.DoesNotContain((await favorites.GetFavoritesAsync()).Cards, card => card.Id == otherId);
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class InterestScope : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _name;
        public MySqlConnection Connection { get; }
        public IConfiguration Configuration { get; }
        public InterestRepository Repository { get; }

        private InterestScope(string adminConnectionString, string name, MySqlConnection connection,
            IConfiguration configuration, TimeProvider? clock)
        {
            _adminConnectionString = adminConnectionString;
            _name = name;
            Connection = connection;
            Configuration = configuration;
            Repository = new InterestRepository(configuration, clock);
        }

        public static async Task<InterestScope> CreateAsync(TimeProvider? clock = null)
        {
            var builder = new MySqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!);
            if (!builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Interest tests require a dedicated test database.");
            var name = $"nodeck_interest_{Guid.NewGuid():N}_test";
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
                await new InterestDatabaseInitializer(configuration).InitializeAsync();
                return new InterestScope(adminConnectionString, name, connection, configuration, clock);
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

        public Task<long> InsertCardAsync(string type) => Connection.ExecuteScalarAsync<long>("""
            INSERT INTO cards (type, title, metadata) VALUES (@Type, 'interest-test', JSON_OBJECT());
            SELECT LAST_INSERT_ID();
            """, new { Type = type });

        public Task<int> CountAsync(string table) => Connection.ExecuteScalarAsync<int>(
            table switch
            {
                "card_view_events" => "SELECT COUNT(*) FROM card_view_events;",
                "card_daily_votes" => "SELECT COUNT(*) FROM card_daily_votes;",
                _ => throw new ArgumentOutOfRangeException(nameof(table)),
            });

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await using var admin = new MySqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE `{_name}`;");
        }
    }
}

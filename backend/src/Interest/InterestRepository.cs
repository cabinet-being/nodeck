using System.Globalization;
using Dapper;
using MySqlConnector;

namespace MyApp.Api.Interest;

public enum InterestViewResult { Recorded, Duplicate, NotFound }

public sealed record DailyVoteStatus(long CardId, string LocalDate, sbyte? Value, string TimeZone);

/// <summary>Interest signals for the single local library. Favorites remain a separate deck.</summary>
public sealed class InterestRepository
{
    private const int ViewHistoryLimit = 500;
    private readonly string _connectionString;
    private readonly TimeProvider _clock;

    public InterestRepository(IConfiguration configuration, TimeProvider? clock = null)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<InterestViewResult> RecordViewAsync(long cardId, Guid eventId,
        int durationMs, CancellationToken cancellationToken = default)
    {
        if (cardId <= 0) throw new ArgumentOutOfRangeException(nameof(cardId));
        if (eventId == Guid.Empty) throw new ArgumentException("A view event ID is required.", nameof(eventId));
        if (durationMs is < 2000 or > 600000)
            throw new ArgumentOutOfRangeException(nameof(durationMs), "View duration must be between 2 seconds and 10 minutes.");

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var acquired = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT GET_LOCK('nodeck-interest-view-events', 10);",
            cancellationToken: cancellationToken));
        if (acquired != 1) throw new InvalidOperationException("Could not acquire the view event lock.");
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var type = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT type FROM cards WHERE id = @CardId FOR UPDATE;",
                new { CardId = cardId }, transaction, cancellationToken: cancellationToken));
            if (type is not ("media" or "comic" or "set")) return InterestViewResult.NotFound;

            var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT COUNT(*) FROM card_view_events WHERE event_id = @EventId;",
                new { EventId = eventId.ToString("D") }, transaction,
                cancellationToken: cancellationToken));
            if (exists) return InterestViewResult.Duplicate;

            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO card_view_events (event_id, card_id, viewed_at_utc, duration_ms)
                VALUES (@EventId, @CardId, @ViewedAtUtc, @DurationMs);
                """, new { EventId = eventId.ToString("D"), CardId = cardId,
                    ViewedAtUtc = _clock.GetUtcNow().UtcDateTime, DurationMs = durationMs },
                transaction, cancellationToken: cancellationToken));
            var cutoff = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("""
                SELECT id FROM card_view_events ORDER BY id DESC LIMIT 1 OFFSET @Offset;
                """, new { Offset = ViewHistoryLimit - 1 }, transaction,
                cancellationToken: cancellationToken));
            if (cutoff is long keepFromId)
                await connection.ExecuteAsync(new CommandDefinition("""
                    DELETE FROM card_view_events WHERE id < @KeepFromId;
                    """, new { KeepFromId = keepFromId }, transaction,
                    cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return InterestViewResult.Recorded;
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT RELEASE_LOCK('nodeck-interest-view-events');"));
        }
    }

    public async Task<DailyVoteStatus?> GetVoteAsync(long cardId, string timeZone,
        CancellationToken cancellationToken = default)
    {
        if (cardId <= 0) throw new ArgumentOutOfRangeException(nameof(cardId));
        var (zone, localDate) = ResolveLocalDate(timeZone);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        if (!await CardExistsAsync(connection, cardId, null, false, cancellationToken)) return null;
        var value = await connection.ExecuteScalarAsync<sbyte?>(new CommandDefinition("""
            SELECT vote FROM card_daily_votes WHERE card_id = @CardId AND vote_date = @VoteDate;
            """, new { CardId = cardId, VoteDate = localDate.ToDateTime(TimeOnly.MinValue) },
            cancellationToken: cancellationToken));
        return new DailyVoteStatus(cardId, localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            value, zone.Id);
    }

    public async Task<DailyVoteStatus?> PutVoteAsync(long cardId, sbyte value, string timeZone,
        CancellationToken cancellationToken = default)
    {
        if (cardId <= 0) throw new ArgumentOutOfRangeException(nameof(cardId));
        if (value is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(value), "Vote must be -1, 0, or 1.");
        var (zone, localDate) = ResolveLocalDate(timeZone);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await CardExistsAsync(connection, cardId, transaction, true, cancellationToken)) return null;
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO card_daily_votes (card_id, vote_date, vote, zone_id, updated_at_utc)
            VALUES (@CardId, @VoteDate, @Vote, @ZoneId, @UpdatedAtUtc)
            ON DUPLICATE KEY UPDATE vote = VALUES(vote), zone_id = VALUES(zone_id),
                updated_at_utc = VALUES(updated_at_utc);
            """, new { CardId = cardId, VoteDate = localDate.ToDateTime(TimeOnly.MinValue), Vote = value,
                ZoneId = zone.Id, UpdatedAtUtc = _clock.GetUtcNow().UtcDateTime },
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new DailyVoteStatus(cardId, localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            value, zone.Id);
    }

    private (TimeZoneInfo Zone, DateOnly LocalDate) ResolveLocalDate(string timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone) || timeZone.Length > 64)
            throw new ArgumentException("A valid time zone is required.", nameof(timeZone));
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
        catch (TimeZoneNotFoundException)
        { throw new ArgumentException("Unknown time zone.", nameof(timeZone)); }
        catch (InvalidTimeZoneException)
        { throw new ArgumentException("Invalid time zone.", nameof(timeZone)); }
        var local = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), zone);
        return (zone, DateOnly.FromDateTime(local.DateTime));
    }

    private static async Task<bool> CardExistsAsync(MySqlConnection connection, long cardId,
        MySqlTransaction? transaction, bool lockCard, CancellationToken cancellationToken)
    {
        var sql = lockCard
            ? "SELECT type FROM cards WHERE id = @CardId FOR UPDATE;"
            : "SELECT type FROM cards WHERE id = @CardId;";
        var type = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(sql,
            new { CardId = cardId }, transaction, cancellationToken: cancellationToken));
        return type is "media" or "comic" or "set";
    }
}

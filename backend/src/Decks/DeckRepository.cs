using System.Data;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;

namespace MyApp.Api.Decks;

public sealed class DeckRepository
{
    public const string FavoritesSystemKey = "favorites";

    private readonly string _connectionString;

    public DeckRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
    }

    public async Task<DeckDetails> CreateAsync(DeckRequest request)
    {
        var title = NormalizeTitle(request.Title);
        var cards = NormalizeCards(request.Cards);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ValidateCardsExistAsync(connection, transaction, cards);

        var metadata = CreateDeckMetadata();
        var deckId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO decks (title, properties, metadata, system_key)
            VALUES (@Title, @Properties, @Metadata, NULL);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                Title = title,
                Properties = request.Properties?.ToJsonString(),
                Metadata = metadata.ToJsonString(),
            },
            transaction);

        await InsertMembershipsAsync(connection, transaction, deckId, cards);
        await transaction.CommitAsync();

        return await GetByIdAsync(deckId) ?? throw new InvalidOperationException("Created deck could not be read.");
    }

    public async Task<IReadOnlyList<DeckSummary>> ListAsync(string? search, string sort, string order)
    {
        await using var connection = new MySqlConnection(_connectionString);
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("(CAST(d.id AS CHAR) = @Search OR d.title LIKE @SearchLike)");
            parameters.Add("Search", search);
            parameters.Add("SearchLike", $"%{search}%");
        }

        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
        var orderDirection = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)
            ? "ASC"
            : "DESC";
        var orderColumn = string.Equals(sort, "created_at", StringComparison.OrdinalIgnoreCase)
            ? "d.created_at"
            : "d.id";

        var rows = await connection.QueryAsync<DbDeckSummary>(
            $"""
            SELECT
                d.id AS {nameof(DbDeckSummary.Id)},
                d.title AS {nameof(DbDeckSummary.Title)},
                COUNT(dc.card_id) AS {nameof(DbDeckSummary.CardCount)},
                d.properties AS {nameof(DbDeckSummary.Properties)},
                d.metadata AS {nameof(DbDeckSummary.Metadata)},
                d.system_key AS {nameof(DbDeckSummary.SystemKey)}
            FROM decks d
            LEFT JOIN deck_cards dc ON dc.deck_id = d.id
            {where}
            GROUP BY d.id, d.title, d.properties, d.metadata, d.system_key, d.created_at
            ORDER BY {orderColumn} {orderDirection}, d.id {orderDirection}
            LIMIT 200;
            """,
            parameters);

        return rows.Select(ToSummary).ToList();
    }

    public async Task<DeckDetails?> GetByIdAsync(long id)
    {
        await using var connection = new MySqlConnection(_connectionString);

        var deck = await connection.QuerySingleOrDefaultAsync<DbDeck>(
            """
            SELECT
                id AS Id,
                title AS Title,
                properties AS Properties,
                metadata AS Metadata,
                system_key AS SystemKey
            FROM decks
            WHERE id = @Id;
            """,
            new { Id = id });

        if (deck is null)
        {
            return null;
        }

        var cards = await GetDeckCardsAsync(connection, id);

        return ToDetails(deck, cards);
    }

    public async Task<DeckDetails?> UpdateAsync(long id, DeckRequest request)
    {
        var title = NormalizeTitle(request.Title);
        var cards = NormalizeCards(request.Cards);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var deck = await connection.QuerySingleOrDefaultAsync<DbDeck>(
            """
            SELECT
                id AS Id,
                title AS Title,
                properties AS Properties,
                metadata AS Metadata,
                system_key AS SystemKey
            FROM decks
            WHERE id = @Id
            FOR UPDATE;
            """,
            new { Id = id },
            transaction);

        if (deck is null)
        {
            await transaction.RollbackAsync();
            return null;
        }

        if (string.Equals(deck.SystemKey, FavoritesSystemKey, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync();
            throw new InvalidOperationException("Favorites deck cannot be edited.");
        }

        await ValidateCardsExistAsync(connection, transaction, cards);

        var metadata = ParseRequiredJsonObject(deck.Metadata);
        metadata["updated_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        await connection.ExecuteAsync(
            """
            UPDATE decks
            SET title = @Title,
                properties = @Properties,
                metadata = @Metadata
            WHERE id = @Id;
            """,
            new
            {
                Id = id,
                Title = title,
                Properties = request.Properties?.ToJsonString(),
                Metadata = metadata.ToJsonString(),
            },
            transaction);

        await connection.ExecuteAsync(
            """
            DELETE FROM deck_cards
            WHERE deck_id = @Id;
            """,
            new { Id = id },
            transaction);

        await InsertMembershipsAsync(connection, transaction, id, cards);
        await transaction.CommitAsync();

        return await GetByIdAsync(id);
    }

    public async Task<bool> DeleteAsync(long id)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var deck = await connection.QuerySingleOrDefaultAsync<DbDeck>(
            """
            SELECT
                id AS Id,
                title AS Title,
                properties AS Properties,
                metadata AS Metadata,
                system_key AS SystemKey
            FROM decks
            WHERE id = @Id
            FOR UPDATE;
            """,
            new { Id = id },
            transaction);

        if (deck is null)
        {
            await transaction.RollbackAsync();
            return false;
        }

        if (string.Equals(deck.SystemKey, FavoritesSystemKey, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync();
            throw new InvalidOperationException("Favorites deck cannot be deleted.");
        }

        var affectedRows = await connection.ExecuteAsync(
            """
            DELETE FROM decks
            WHERE id = @Id;
            """,
            new { Id = id },
            transaction);

        await transaction.CommitAsync();

        return affectedRows > 0;
    }

    public async Task<DeckDetails> GetFavoritesAsync()
    {
        var deckId = await EnsureFavoritesDeckAsync();

        return await GetByIdAsync(deckId) ?? throw new InvalidOperationException("Favorites deck could not be read.");
    }

    public async Task<DeckDetails> AddFavoriteAsync(long cardId)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var deckId = await EnsureFavoritesDeckAsync(connection, transaction);
        await ValidateCardsExistAsync(
            connection,
            transaction,
            new[] { new DeckCardInput(cardId, null) });

        var existing = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM deck_cards
            WHERE deck_id = @DeckId
              AND card_id = @CardId;
            """,
            new { DeckId = deckId, CardId = cardId },
            transaction);

        if (existing == 0)
        {
            var position = await connection.ExecuteScalarAsync<long>(
                """
                SELECT COALESCE(MAX(position) + 1, 0)
                FROM deck_cards
                WHERE deck_id = @DeckId;
                """,
                new { DeckId = deckId },
                transaction);

            await connection.ExecuteAsync(
                """
                INSERT INTO deck_cards (deck_id, card_id, position, properties, metadata)
                VALUES (@DeckId, @CardId, @Position, NULL, @Metadata);
                """,
                new
                {
                    DeckId = deckId,
                    CardId = cardId,
                    Position = (int)position,
                    Metadata = CreateMembershipMetadata().ToJsonString(),
                },
                transaction);
        }

        await transaction.CommitAsync();

        return await GetFavoritesAsync();
    }

    public async Task<DeckDetails> RemoveFavoriteAsync(long cardId)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var deckId = await EnsureFavoritesDeckAsync(connection, transaction);

        await connection.ExecuteAsync(
            """
            DELETE FROM deck_cards
            WHERE deck_id = @DeckId
              AND card_id = @CardId;
            """,
            new { DeckId = deckId, CardId = cardId },
            transaction);

        await NormalizeDeckPositionsAsync(connection, transaction, deckId);
        await transaction.CommitAsync();

        return await GetFavoritesAsync();
    }

    private static async Task<IReadOnlyList<DbDeckCard>> GetDeckCardsAsync(IDbConnection connection, long deckId)
    {
        var rows = await connection.QueryAsync<DbDeckCard>(
            """
            SELECT
                c.id AS Id,
                c.type AS Type,
                c.title AS Title,
                c.preview AS Preview,
                c.properties AS CardProperties,
                c.metadata AS CardMetadata,
                dc.position AS Position,
                dc.properties AS MembershipProperties
            FROM deck_cards dc
            JOIN cards c ON c.id = dc.card_id
            WHERE dc.deck_id = @DeckId
            ORDER BY dc.position ASC, c.id ASC;
            """,
            new { DeckId = deckId });

        return rows.ToList();
    }

    private static async Task InsertMembershipsAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        long deckId,
        IReadOnlyList<DeckCardInput> cards)
    {
        for (var index = 0; index < cards.Count; index++)
        {
            var card = cards[index];

            await connection.ExecuteAsync(
                """
                INSERT INTO deck_cards (deck_id, card_id, position, properties, metadata)
                VALUES (@DeckId, @CardId, @Position, @Properties, @Metadata);
                """,
                new
                {
                    DeckId = deckId,
                    card.CardId,
                    Position = index,
                    Properties = card.Properties?.ToJsonString(),
                    Metadata = CreateMembershipMetadata().ToJsonString(),
                },
                transaction);
        }
    }

    private async Task<long> EnsureFavoritesDeckAsync()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var deckId = await EnsureFavoritesDeckAsync(connection, transaction);
        await transaction.CommitAsync();

        return deckId;
    }

    private static async Task<long> EnsureFavoritesDeckAsync(
        IDbConnection connection,
        IDbTransaction transaction)
    {
        var existingDeckId = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT id
            FROM decks
            WHERE system_key = 'favorites'
            FOR UPDATE;
            """,
            transaction: transaction);

        if (existingDeckId is not null)
        {
            return existingDeckId.Value;
        }

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO decks (title, properties, metadata, system_key)
            VALUES ('Favorites', NULL, @Metadata, 'favorites');
            SELECT LAST_INSERT_ID();
            """,
            new { Metadata = CreateDeckMetadata().ToJsonString() },
            transaction);
    }

    private static async Task NormalizeDeckPositionsAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        long deckId)
    {
        var cardIds = (await connection.QueryAsync<long>(
            """
            SELECT card_id
            FROM deck_cards
            WHERE deck_id = @DeckId
            ORDER BY position ASC, card_id ASC;
            """,
            new { DeckId = deckId },
            transaction)).ToList();

        for (var index = 0; index < cardIds.Count; index++)
        {
            await connection.ExecuteAsync(
                """
                UPDATE deck_cards
                SET position = @Position
                WHERE deck_id = @DeckId
                  AND card_id = @CardId;
                """,
                new { DeckId = deckId, CardId = cardIds[index], Position = index },
                transaction);
        }
    }

    private static async Task ValidateCardsExistAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        IReadOnlyList<DeckCardInput> cards)
    {
        if (cards.Count == 0)
        {
            return;
        }

        var cardIds = cards.Select(card => card.CardId).ToList();
        var duplicateCardId = cardIds
            .GroupBy(cardId => cardId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .FirstOrDefault();

        if (duplicateCardId > 0)
        {
            throw new InvalidOperationException($"Card #{duplicateCardId} is already in this deck.");
        }

        if (cardIds.Any(cardId => cardId <= 0))
        {
            throw new InvalidOperationException("Deck cards must reference existing cards.");
        }

        var existingCardIds = await connection.QueryAsync<long>(
            """
            SELECT id
            FROM cards
            WHERE id IN @CardIds;
            """,
            new { CardIds = cardIds },
            transaction);

        var existing = existingCardIds.ToHashSet();
        var missingCardId = cardIds.FirstOrDefault(cardId => !existing.Contains(cardId));

        if (missingCardId > 0)
        {
            throw new InvalidOperationException($"Card #{missingCardId} does not exist.");
        }
    }

    private static string NormalizeTitle(string? title)
    {
        var normalized = title?.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Deck title is required.");
        }

        return normalized;
    }

    private static IReadOnlyList<DeckCardInput> NormalizeCards(IReadOnlyList<DeckCardInput>? cards) =>
        cards ?? Array.Empty<DeckCardInput>();

    private static JsonObject CreateDeckMetadata()
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        return new JsonObject
        {
            ["created_at"] = now,
            ["updated_at"] = now,
        };
    }

    private static JsonObject CreateMembershipMetadata() =>
        new()
        {
            ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };

    private static DeckSummary ToSummary(DbDeckSummary deck) =>
        new(
            deck.Id,
            deck.Title,
            deck.CardCount,
            ParseOptionalJson(deck.Properties),
            ParseRequiredJson(deck.Metadata),
            deck.SystemKey);

    private static DeckDetails ToDetails(DbDeck deck, IReadOnlyList<DbDeckCard> cards) =>
        new(
            deck.Id,
            deck.Title,
            ParseOptionalJson(deck.Properties),
            ParseRequiredJson(deck.Metadata),
            deck.SystemKey,
            cards.Select(ToDeckCardResponse).ToList());

    private static DeckCardResponse ToDeckCardResponse(DbDeckCard card)
    {
        var metadata = ParseRequiredJson(card.CardMetadata);
        var contentPath = metadata["content_path"]?.GetValue<string>();

        return new DeckCardResponse(
            card.Id,
            card.Type,
            card.Title,
            card.Preview is null ? null : $"/api/cards/{card.Id}/preview",
            contentPath is null ? null : $"/api/cards/{card.Id}/content",
            card.Position,
            ParseOptionalJson(card.CardProperties),
            metadata,
            ParseOptionalJson(card.MembershipProperties));
    }

    private static JsonNode ParseRequiredJson(string json) =>
        JsonNode.Parse(json) ?? new JsonObject();

    private static JsonObject ParseRequiredJsonObject(string json) =>
        JsonNode.Parse(json) as JsonObject ?? new JsonObject();

    private static JsonNode? ParseOptionalJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
}

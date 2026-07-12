using Dapper;
using MySqlConnector;

namespace MyApp.Api.Decks;

public sealed class DeckDatabaseInitializer
{
    private readonly string _connectionString;

    public DeckDatabaseInitializer(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
    }

    public async Task InitializeAsync()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS decks (
                id BIGINT NOT NULL AUTO_INCREMENT,
                title VARCHAR(255) NOT NULL,
                properties JSON NULL,
                metadata JSON NOT NULL,
                system_key VARCHAR(128) NULL,
                created_at DATETIME(6)
                    GENERATED ALWAYS AS (
                        STR_TO_DATE(JSON_UNQUOTE(JSON_EXTRACT(metadata, '$.created_at')), '%Y-%m-%dT%H:%i:%sZ')
                    ) STORED,
                PRIMARY KEY (id),
                CONSTRAINT chk_decks_properties_json CHECK (properties IS NULL OR JSON_VALID(properties)),
                CONSTRAINT chk_decks_metadata_json CHECK (JSON_VALID(metadata)),
                UNIQUE INDEX uq_decks_system_key (system_key),
                INDEX idx_decks_created_at (created_at)
            );
            """);

        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS deck_cards (
                deck_id BIGINT NOT NULL,
                card_id BIGINT NOT NULL,
                position INT NOT NULL,
                properties JSON NULL,
                metadata JSON NOT NULL,
                PRIMARY KEY (deck_id, card_id),
                CONSTRAINT fk_deck_cards_deck
                    FOREIGN KEY (deck_id) REFERENCES decks(id)
                    ON DELETE CASCADE,
                CONSTRAINT fk_deck_cards_card
                    FOREIGN KEY (card_id) REFERENCES cards(id)
                    ON DELETE CASCADE,
                CONSTRAINT chk_deck_cards_position CHECK (position >= 0),
                CONSTRAINT chk_deck_cards_properties_json CHECK (properties IS NULL OR JSON_VALID(properties)),
                CONSTRAINT chk_deck_cards_metadata_json CHECK (JSON_VALID(metadata)),
                UNIQUE INDEX uq_deck_cards_position (deck_id, position),
                INDEX idx_deck_cards_deck_id (deck_id),
                INDEX idx_deck_cards_card_id (card_id)
            );
            """);
    }
}

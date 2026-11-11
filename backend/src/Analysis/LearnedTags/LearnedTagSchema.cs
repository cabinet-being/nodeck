using Dapper;
using MySqlConnector;

namespace MyApp.Api.Analysis.LearnedTags;

/// <summary>
/// Schema for visual suggestions of custom tags (migration 13). Every step is idempotent, so it also brings
/// a database created from the current init SQL, or a long-lived test database, to the same shape.
/// The new tables hold derived data only (features, trained models), rebuilt from cards and tag assignments.
/// </summary>
public static class LearnedTagSchema
{
    private const string SuggestionSources = "'visual', 'speech', 'custom', 'learned'";

    public static async Task ApplyAsync(MySqlConnection connection)
    {
        if (!await ColumnExistsAsync(connection, "card_image_analysis_jobs", "priority"))
            await connection.ExecuteAsync(
                "ALTER TABLE card_image_analysis_jobs ADD COLUMN priority TINYINT NOT NULL DEFAULT 0;");

        await ReplaceCheckAsync(connection, "card_tag_suggestions", "chk_card_tag_suggestions_source",
            $"suggestion_source IN ({SuggestionSources})");
        await ReplaceCheckAsync(connection, "card_tag_decisions", "chk_card_tag_decisions_source",
            $"suggestion_source IS NULL OR suggestion_source IN ({SuggestionSources})");

        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS card_visual_features (
                card_id BIGINT NOT NULL PRIMARY KEY,
                revision VARCHAR(36) NOT NULL,
                model_key VARCHAR(128) NOT NULL,
                dimension INT NOT NULL,
                features MEDIUMBLOB NOT NULL,
                scored_generation BIGINT NULL,
                updated_at_utc DATETIME(6) NOT NULL,
                KEY idx_card_visual_features_model_scored (model_key, scored_generation),
                CONSTRAINT chk_card_visual_features_dimension CHECK (dimension > 0),
                CONSTRAINT fk_card_visual_features_card FOREIGN KEY (card_id) REFERENCES cards(id) ON DELETE CASCADE
            );
            """);
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS learned_tag_models (
                tag_id BIGINT NOT NULL PRIMARY KEY,
                feature_model_key VARCHAR(128) NOT NULL,
                dimension INT NOT NULL,
                weights MEDIUMBLOB NOT NULL,
                bias DOUBLE NOT NULL,
                threshold DECIMAL(6,5) NULL,
                positives INT NOT NULL,
                negatives INT NOT NULL,
                hard_negatives INT NOT NULL,
                cv_precision DECIMAL(6,5) NULL,
                cv_recall DECIMAL(6,5) NULL,
                generation BIGINT NOT NULL,
                trained_at_utc DATETIME(6) NOT NULL,
                CONSTRAINT chk_learned_tag_models_threshold CHECK (threshold IS NULL OR threshold BETWEEN 0 AND 1),
                CONSTRAINT fk_learned_tag_models_tag FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
            );
            """);
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS learned_tag_state (
                id TINYINT NOT NULL PRIMARY KEY,
                generation BIGINT NOT NULL,
                feature_model_key VARCHAR(128) NOT NULL,
                fingerprint CHAR(64) NOT NULL,
                trained_at_utc DATETIME(6) NOT NULL,
                CONSTRAINT chk_learned_tag_state_singleton CHECK (id = 1)
            );
            """);
    }

    private static Task<bool> ColumnExistsAsync(MySqlConnection connection, string table, string column) =>
        connection.ExecuteScalarAsync<bool>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @Table AND COLUMN_NAME = @Column;
            """, new { Table = table, Column = column });

    private static async Task ReplaceCheckAsync(MySqlConnection connection, string table, string name,
        string clause)
    {
        var existing = await connection.ExecuteScalarAsync<string?>("""
            SELECT cc.CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS cc
            JOIN information_schema.TABLE_CONSTRAINTS tc
              ON tc.CONSTRAINT_SCHEMA = cc.CONSTRAINT_SCHEMA AND tc.CONSTRAINT_NAME = cc.CONSTRAINT_NAME
            WHERE cc.CONSTRAINT_SCHEMA = DATABASE() AND tc.TABLE_NAME = @Table AND cc.CONSTRAINT_NAME = @Name;
            """, new { Table = table, Name = name });
        if (existing is not null && existing.Contains("'learned'", StringComparison.Ordinal)) return;
        if (existing is not null)
            await connection.ExecuteAsync($"ALTER TABLE `{table}` DROP CHECK `{name}`;");
        await connection.ExecuteAsync($"ALTER TABLE `{table}` ADD CONSTRAINT `{name}` CHECK ({clause});");
    }
}

using System.Runtime.CompilerServices;
using Dapper;
using MySqlConnector;

namespace MyApp.Api.Tests;

/// <summary>
/// Fixtures create tables with CREATE TABLE IF NOT EXISTS, which leaves a long-lived shared test database on an
/// older cards schema. Bring an existing cards table up to migration 12 once per test run (no-op on a fresh one).
/// </summary>
internal static class SharedTestDatabaseUpgrade
{
    [ModuleInitializer]
    internal static void Run()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (!builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase)) return;

        using var connection = new MySqlConnection(connectionString);
        connection.Open();
        var hasCards = connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'cards';
            """) > 0;
        if (!hasCards) return;
        if (connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_schema = DATABASE() AND table_name = 'cards' AND column_name = 'content_sha256';
            """) == 0)
            connection.Execute("ALTER TABLE cards ADD COLUMN content_sha256 CHAR(64) NULL;");
        if (connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM information_schema.statistics
            WHERE table_schema = DATABASE() AND table_name = 'cards' AND index_name = 'idx_cards_content_sha256';
            """) == 0)
            connection.Execute("ALTER TABLE cards ADD INDEX idx_cards_content_sha256 (content_sha256);");
    }
}

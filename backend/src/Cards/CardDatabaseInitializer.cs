using Dapper;
using MySqlConnector;

namespace MyApp.Api.Cards;

public sealed class CardDatabaseInitializer
{
    private readonly string _connectionString;

    public CardDatabaseInitializer(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
    }

    public async Task InitializeAsync()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var relationTypeConstraint = await connection.QuerySingleOrDefaultAsync<string>(
            """
            SELECT cc.CHECK_CLAUSE
            FROM information_schema.CHECK_CONSTRAINTS cc
            JOIN information_schema.TABLE_CONSTRAINTS tc
              ON tc.CONSTRAINT_SCHEMA = cc.CONSTRAINT_SCHEMA
             AND tc.CONSTRAINT_NAME = cc.CONSTRAINT_NAME
            WHERE tc.TABLE_SCHEMA = DATABASE()
              AND tc.TABLE_NAME = 'card_relations'
              AND tc.CONSTRAINT_NAME = 'chk_card_relations_type';
            """);

        if (relationTypeConstraint?.Contains("next_in_sequence", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        if (relationTypeConstraint is not null)
        {
            await connection.ExecuteAsync(
                """
                ALTER TABLE card_relations
                DROP CHECK chk_card_relations_type;
                """);
        }

        await connection.ExecuteAsync(
            """
            ALTER TABLE card_relations
            ADD CONSTRAINT chk_card_relations_type CHECK (
                relation_type IN ('tagged_with', 'sourced_from', 'contains', 'related_to', 'preview_for', 'next_in_sequence')
            );
            """);
    }
}

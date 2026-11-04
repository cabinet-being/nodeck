using Dapper;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyApp.Api.Cards;
using MyApp.Api.Catalog;
using MyApp.Api.Decks;
using MyApp.Api.Migrations;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class SchemaMigrationTests
{
    [DatabaseFact]
    public async Task PopulatedUnversionedDatabaseRefusesMigrationBeforeAnySchemaWrite()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await schema.ApplySqlAsync("001_cards.sql");
        await using var connection = await schema.OpenAsync();
        var cardId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO cards (type, title, metadata)
            VALUES ('media', 'migration-preflight', JSON_OBJECT());
            SELECT LAST_INSERT_ID();
            """);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.Configuration).InitializeAsync());
        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cards WHERE id = @Id;", new { Id = cardId }));
        Assert.False(await schema.TableExistsAsync("schema_migrations"));
        Assert.False(await schema.TableExistsAsync("tags"));
    }

    [DatabaseFact]
    public async Task FreshSchemaRecordsVersionsOnceAndKeepsCountsOnRerun()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await schema.ApplySqlAsync("001_cards.sql", "002_catalog.sql");
        var runner = CreateRunner(schema.Configuration);
        await runner.InitializeAsync();
        Assert.True(await schema.TableExistsAsync("schema_migrations"));
        await using var connection = await schema.OpenAsync();
        var firstVersions = (await connection.QueryAsync<int>(
            "SELECT version FROM schema_migrations ORDER BY version;")).ToArray();
        Assert.NotEmpty(firstVersions);

        await runner.InitializeAsync();
        var secondVersions = (await connection.QueryAsync<int>(
            "SELECT version FROM schema_migrations ORDER BY version;")).ToArray();
        Assert.Equal(firstVersions, secondVersions);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cards;"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_relations;"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM deck_cards;"));
    }

    [DatabaseFact]
    public async Task MatchingBackupAllowsLegacyMigrationWithoutLosingCardsOrRelations()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await schema.ApplySqlAsync("001_cards.sql");
        await using var connection = await schema.OpenAsync();
        var firstId = await InsertCardAsync(connection, "media");
        var secondId = await InsertCardAsync(connection, "tag");
        var relationId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO card_relations (from_card_id, to_card_id, relation_type, metadata)
            VALUES (@FromId, @ToId, 'tagged_with', JSON_OBJECT());
            SELECT LAST_INSERT_ID();
            """, new { FromId = firstId, ToId = secondId });
        var manifestPath = await schema.WriteMatchingBackupManifestAsync();

        var runner = CreateRunner(schema.ConfigurationWithManifest(manifestPath));
        await runner.InitializeAsync();
        Assert.True(await schema.TableExistsAsync("schema_migrations"));
        Assert.True(await schema.TableExistsAsync("tags"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cards WHERE id IN @Ids;", new { Ids = new[] { firstId, secondId } }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_relations WHERE id = @Id;", new { Id = relationId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_tags WHERE legacy_relation_id = @Id;", new { Id = relationId }));
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM schema_migrations;"));

        await runner.InitializeAsync();
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM schema_migrations;"));
        await connection.ExecuteAsync("DELETE FROM card_relations WHERE id = @Id;", new { Id = relationId });
        await runner.InitializeAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_tags WHERE legacy_relation_id = @Id;", new { Id = relationId }));
    }

    [DatabaseFact]
    public async Task TamperedBackupArtifactBlocksMigrationBeforeSchemaWrite()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await schema.ApplySqlAsync("001_cards.sql");
        await using var connection = await schema.OpenAsync();
        var cardId = await InsertCardAsync(connection, "media");
        var manifestPath = await schema.WriteMatchingBackupManifestAsync();
        await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(manifestPath)!, "database.sql"),
            "-- modified after backup\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.ConfigurationWithManifest(manifestPath)).InitializeAsync());
        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await schema.TableExistsAsync("schema_migrations"));
        Assert.False(await schema.TableExistsAsync("tags"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cards WHERE id = @Id;", new { Id = cardId }));
    }

    [DatabaseFact]
    public async Task InvalidOwnershipFailsBeforeLedgerOrCatalogTablesAreWritten()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await schema.ApplySqlAsync("001_cards.sql");
        await using var connection = await schema.OpenAsync();
        var parentId = await InsertCardAsync(connection, "media");
        var childId = await InsertCardAsync(connection, "media");
        var relationId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO card_relations (from_card_id, to_card_id, relation_type, properties, metadata)
            VALUES (@ParentId, @ChildId, 'contains', JSON_OBJECT('position', 0), JSON_OBJECT());
            SELECT LAST_INSERT_ID();
            """, new { ParentId = parentId, ChildId = childId });
        var manifestPath = await schema.WriteMatchingBackupManifestAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.ConfigurationWithManifest(manifestPath)).InitializeAsync());
        Assert.Contains("ownership", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await schema.TableExistsAsync("schema_migrations"));
        Assert.False(await schema.TableExistsAsync("tags"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cards WHERE id IN @Ids;", new { Ids = new[] { parentId, childId } }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_relations WHERE id = @Id;", new { Id = relationId }));
    }

    private static Task<long> InsertCardAsync(MySqlConnection connection, string type) =>
        connection.ExecuteScalarAsync<long>("""
            INSERT INTO cards (type, title, metadata) VALUES (@Type, 'migration-test', JSON_OBJECT());
            SELECT LAST_INSERT_ID();
            """, new { Type = type });

    private static SchemaMigrationRunner CreateRunner(IConfiguration configuration) =>
        new(configuration, NullLogger<SchemaMigrationRunner>.Instance,
            new CardDatabaseInitializer(configuration),
            new DeckDatabaseInitializer(configuration),
            new CatalogDatabaseInitializer(configuration, NullLogger<CatalogDatabaseInitializer>.Instance));

    private sealed class IsolatedSchema : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _name;
        private readonly string _connectionString;
        private readonly string _temporaryRoot;

        private IsolatedSchema(string adminConnectionString, string name, string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _name = name;
            _connectionString = connectionString;
            _temporaryRoot = Path.Combine(Path.GetTempPath(), $"nodeck-migration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(_temporaryRoot, "app_data"));
            Configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connectionString,
                    ["NODECK_APP_DATA"] = Path.Combine(_temporaryRoot, "app_data"),
                }).Build();
        }

        public IConfiguration Configuration { get; }

        public IConfiguration ConfigurationWithManifest(string path) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _connectionString,
                ["NODECK_APP_DATA"] = Path.Combine(_temporaryRoot, "app_data"),
                ["NODECK_MIGRATION_BACKUP_MANIFEST"] = path,
            }).Build();

        public static async Task<IsolatedSchema> CreateAsync()
        {
            var baseConnection = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
            var builder = new MySqlConnectionStringBuilder(baseConnection);
            if (!builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Schema migration tests require a dedicated test database.");
            var name = $"nodeck_migration_{Guid.NewGuid():N}_test";
            builder.Database = "";
            var adminConnectionString = builder.ConnectionString;
            await using var admin = new MySqlConnection(adminConnectionString);
            try
            {
                await admin.OpenAsync();
                await admin.ExecuteAsync($"CREATE DATABASE `{name}`;");
            }
            catch (MySqlException exception) when (exception.Number is 1044 or 1045)
            {
                throw new InvalidOperationException(
                    "Schema migration tests need CREATE DATABASE permission on disposable test MySQL.", exception);
            }
            builder.Database = name;
            return new IsolatedSchema(adminConnectionString, name, builder.ConnectionString);
        }

        public async Task<MySqlConnection> OpenAsync()
        {
            var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            return connection;
        }

        public async Task ApplySqlAsync(params string[] files)
        {
            await using var connection = await OpenAsync();
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "database/init/001_cards.sql")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            foreach (var file in files)
            {
                var sql = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "database/init", file));
                foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    await connection.ExecuteAsync(statement);
            }
        }

        public async Task<bool> TableExistsAsync(string table)
        {
            await using var connection = await OpenAsync();
            return await connection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @Table;
                """, new { Table = table }) != 0;
        }

        public async Task<string> WriteMatchingBackupManifestAsync()
        {
            var backupRoot = Path.Combine(_temporaryRoot, "backup");
            Directory.CreateDirectory(backupRoot);
            var databasePath = Path.Combine(backupRoot, "database.sql");
            var dataPath = Path.Combine(backupRoot, "app_data.tar");
            var manifestPath = Path.Combine(backupRoot, "manifest.txt");
            await File.WriteAllTextAsync(databasePath, "-- disposable test database snapshot\n");
            await File.WriteAllBytesAsync(dataPath, new byte[1024]);
            await using var connection = await OpenAsync();
            var cards = await CountIfTableExistsAsync(connection, "cards");
            var relations = await CountIfTableExistsAsync(connection, "card_relations");
            var decks = await CountIfTableExistsAsync(connection, "decks");
            var deckCards = await CountIfTableExistsAsync(connection, "deck_cards");
            var tags = await CountIfTableExistsAsync(connection, "tags");
            var sources = await CountIfTableExistsAsync(connection, "sources");
            await File.WriteAllLinesAsync(manifestPath,
            [
                "format=1",
                $"database={_name}",
                "startingVersion=0",
                $"database_sha256={await Sha256Async(databasePath)}",
                $"app_data_sha256={await Sha256Async(dataPath)}",
                "app_file_count=0",
                $"count.cards={cards}",
                $"count.card_relations={relations}",
                $"count.decks={decks}",
                $"count.deck_cards={deckCards}",
                $"count.tags={tags}",
                $"count.sources={sources}",
            ]);
            return manifestPath;
        }

        private static async Task<long> CountIfTableExistsAsync(MySqlConnection connection, string table)
        {
            var exists = await connection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @Table;
                """, new { Table = table });
            if (exists == 0) return 0;
            // Only fixed table names above are passed to this helper.
            return await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM `{table}`;");
        }

        private static async Task<string> Sha256Async(string path)
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new MySqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE `{_name}`;");
            Directory.Delete(_temporaryRoot, recursive: true);
        }
    }
}

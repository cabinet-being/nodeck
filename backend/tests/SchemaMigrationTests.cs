using Dapper;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyApp.Api.Cards;
using MyApp.Api.Catalog;
using MyApp.Api.Decks;
using MyApp.Api.Migrations;
using MyApp.Api.Interest;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class SchemaMigrationTests
{
    [DatabaseFact]
    public async Task EmptyDatabaseGetsTheBaseSchemaAndAllMigrations()
    {
        await using var schema = await IsolatedSchema.CreateAsync();

        await CreateRunner(schema.Configuration).InitializeAsync();

        foreach (var table in new[] { "cards", "card_relations", "tags", "decks", "schema_migrations" })
            Assert.True(await schema.TableExistsAsync(table), table);
        await using var connection = await schema.OpenAsync();
        var cardId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO cards (type, title, metadata)
            VALUES ('media', 'bootstrap', JSON_OBJECT('created_at', '2026-11-11T00:00:00Z'));
            SELECT LAST_INSERT_ID();
            """);
        Assert.True(cardId > 0);

        // A second start sees a current schema and changes nothing.
        await CreateRunner(schema.Configuration).InitializeAsync();
    }

    [DatabaseFact]
    public async Task PartiallyCreatedDatabaseIsNotBootstrapped()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await using var connection = await schema.OpenAsync();
        await connection.ExecuteAsync("CREATE TABLE unrelated (id INT PRIMARY KEY);");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.Configuration).InitializeAsync());
        Assert.Contains("Base card tables are missing", error.Message);
        Assert.False(await schema.TableExistsAsync("cards"));
    }

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
        Assert.Equal(Enumerable.Range(1, 12), firstVersions);
        Assert.True(await schema.TableExistsAsync("card_speech_analysis_jobs"));
        Assert.True(await schema.TableExistsAsync("card_view_events"));
        Assert.True(await schema.TableExistsAsync("card_daily_votes"));
        Assert.True(await schema.TableExistsAsync("card_creation_requests"));

        await runner.InitializeAsync();
        var secondVersions = (await connection.QueryAsync<int>(
            "SELECT version FROM schema_migrations ORDER BY version;")).ToArray();
        Assert.Equal(firstVersions, secondVersions);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cards;"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_relations;"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM deck_cards;"));
    }

    [DatabaseFact]
    public async Task FreshSchemaWithDirectoryPlaceholdersDoesNotRequireBackup()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await schema.ApplySqlAsync("001_cards.sql", "002_catalog.sql");
        var appDataRoot = schema.Configuration["NODECK_APP_DATA"]!;
        foreach (var directory in new[] { "cache", "cards", "config", "uploads", "user-data" })
        {
            var path = Path.Combine(appDataRoot, directory);
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, ".gitkeep"), "");
        }

        await CreateRunner(schema.Configuration).InitializeAsync();

        await using var connection = await schema.OpenAsync();
        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
    }

    [DatabaseFact]
    public async Task UnversionedSchemaWithActualAppFileStillRequiresBackup()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await schema.ApplySqlAsync("001_cards.sql", "002_catalog.sql");
        var appDataRoot = schema.Configuration["NODECK_APP_DATA"]!;
        await File.WriteAllTextAsync(Path.Combine(appDataRoot, "existing-upload.png"), "content");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.Configuration).InitializeAsync());

        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await schema.TableExistsAsync("schema_migrations"));
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
        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM schema_migrations;"));

        await runner.InitializeAsync();
        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM schema_migrations;"));
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

    [DatabaseFact]
    public async Task VersionFourUpgradePreservesExistingCatalogAndAssignments()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await SeedVersionThreeAsync(schema);
        await using var connection = await schema.OpenAsync();
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 3);

        await CreateRunner(schema.ConfigurationWithManifest(manifest)).InitializeAsync();

        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.True(await schema.TableExistsAsync("danbooru_import_state"));
        Assert.True(await schema.TableExistsAsync("card_tag_suggestions"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases WHERE alias = 'legacy_alias' AND origin = 'user' AND danbooru_id IS NULL;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_tags;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_sources;"));
        await CreateRunner(schema.Configuration).InitializeAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases;"));
    }

    [DatabaseFact]
    public async Task VersionFourUpgradeRejectsStaleAliasBackupBeforeSchemaWrite()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await SeedVersionThreeAsync(schema);
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 3);
        await File.WriteAllLinesAsync(manifest, (await File.ReadAllLinesAsync(manifest))
            .Select(line => line.StartsWith("count.tag_aliases=", StringComparison.Ordinal)
                ? "count.tag_aliases=0" : line));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.ConfigurationWithManifest(manifest)).InitializeAsync());
        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await schema.TableExistsAsync("danbooru_import_state"));
        await using var connection = await schema.OpenAsync();
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases;"));
    }

    [DatabaseFact]
    public async Task VersionFiveUpgradePreservesExistingAssignmentsAndCreatesSuggestionTable()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await SeedVersionFourAsync(schema);
        await using var connection = await schema.OpenAsync();
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 4);

        await CreateRunner(schema.ConfigurationWithManifest(manifest)).InitializeAsync();

        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.True(await schema.TableExistsAsync("card_tag_suggestions"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_tags WHERE assignment_source = 'manual';"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases WHERE origin = 'user';"));
        await CreateRunner(schema.Configuration).InitializeAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_tags;"));
    }

    [DatabaseFact]
    public async Task VersionFiveUpgradeRejectsStaleAssignmentBackupBeforeSchemaWrite()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await SeedVersionFourAsync(schema);
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 4);
        await File.WriteAllLinesAsync(manifest, (await File.ReadAllLinesAsync(manifest))
            .Select(line => line.StartsWith("count.card_tags=", StringComparison.Ordinal)
                ? "count.card_tags=0" : line));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.ConfigurationWithManifest(manifest)).InitializeAsync());
        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await schema.TableExistsAsync("card_tag_suggestions"));
        await using var connection = await schema.OpenAsync();
        Assert.Equal(4, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_tags;"));
    }

    [DatabaseFact]
    public async Task VersionSixUpgradePreservesSuggestionsAndCreatesJobTable()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await SeedVersionFiveAsync(schema);
        await using var connection = await schema.OpenAsync();
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 5);

        await CreateRunner(schema.ConfigurationWithManifest(manifest)).InitializeAsync();

        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.True(await schema.TableExistsAsync("card_image_analysis_jobs"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_tag_suggestions WHERE state = 'pending';"));
        await CreateRunner(schema.Configuration).InitializeAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_tag_suggestions WHERE state = 'pending';"));
    }

    [DatabaseFact]
    public async Task VersionSixUpgradeRejectsStaleSuggestionBackupBeforeSchemaWrite()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        await SeedVersionFiveAsync(schema);
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 5);
        await File.WriteAllLinesAsync(manifest, (await File.ReadAllLinesAsync(manifest))
            .Select(line => line.StartsWith("count.card_tag_suggestions=", StringComparison.Ordinal)
                ? "count.card_tag_suggestions=0" : line));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.ConfigurationWithManifest(manifest)).InitializeAsync());
        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await schema.TableExistsAsync("card_image_analysis_jobs"));
        await using var connection = await schema.OpenAsync();
        Assert.Equal(5, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_tag_suggestions;"));
    }

    [DatabaseFact]
    public async Task VersionSevenUpgradePreservesCardsAndImageJobsAndAllowsUnknownSpeechState()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var cardId = await SeedVersionSixAsync(schema);
        await using var connection = await schema.OpenAsync();
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 6);

        var runner = CreateRunner(schema.ConfigurationWithManifest(manifest));
        await runner.InitializeAsync();

        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.True(await schema.TableExistsAsync("card_speech_analysis_jobs"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cards WHERE id = @CardId;", new { CardId = cardId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM card_image_analysis_jobs
            WHERE card_id = @CardId AND revision = 'existing-image-revision' AND state = 'pending';
            """, new { CardId = cardId }));
        await connection.ExecuteAsync("""
            INSERT INTO card_speech_analysis_jobs
                (card_id, revision, state, available_at_utc)
            VALUES (@CardId, 'speech-revision', 'pending', UTC_TIMESTAMP(6));
            """, new { CardId = cardId });
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM card_speech_analysis_jobs
            WHERE card_id = @CardId AND speech_detected IS NULL
              AND english_speech_detected IS NULL AND truncated = 0;
            """, new { CardId = cardId }));
        await runner.InitializeAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_speech_analysis_jobs WHERE card_id = @CardId;",
            new { CardId = cardId }));
    }

    [DatabaseFact]
    public async Task VersionSevenUpgradeRejectsStaleSpeechJobBackupBeforeSchemaWrite()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var cardId = await SeedVersionSixAsync(schema);
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 6);
        await File.WriteAllLinesAsync(manifest, (await File.ReadAllLinesAsync(manifest))
            .Select(line => line.StartsWith("count.card_speech_analysis_jobs=", StringComparison.Ordinal)
                ? "count.card_speech_analysis_jobs=1" : line));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.ConfigurationWithManifest(manifest)).InitializeAsync());
        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await schema.TableExistsAsync("card_speech_analysis_jobs"));
        await using var connection = await schema.OpenAsync();
        Assert.Equal(6, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_image_analysis_jobs WHERE card_id = @CardId;",
            new { CardId = cardId }));
    }

    [DatabaseFact]
    public async Task VersionEightUpgradePreservesSuggestionStatesCardsAndAnalysisJobs()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var cardId = await SeedVersionSevenAsync(schema);
        await using var connection = await schema.OpenAsync();
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 7);
        var runner = CreateRunner(schema.ConfigurationWithManifest(manifest));

        await runner.InitializeAsync();

        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal("tag-suggestion-source-v1", await connection.ExecuteScalarAsync<string>(
            "SELECT name FROM schema_migrations WHERE version = 8;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cards WHERE id = @CardId;", new { CardId = cardId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_image_analysis_jobs WHERE card_id = @CardId AND revision = 'existing-image-revision' AND state = 'pending';",
            new { CardId = cardId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_speech_analysis_jobs WHERE card_id = @CardId AND revision = 'existing-speech-revision' AND state = 'pending';",
            new { CardId = cardId }));
        Assert.Equal(new[] { "pending:visual", "accepted:visual", "rejected:visual" },
            (await connection.QueryAsync<string>("""
                SELECT CONCAT(state, ':', suggestion_source)
                FROM card_tag_suggestions WHERE card_id = @CardId ORDER BY tag_id;
                """, new { CardId = cardId })).ToArray());
        var newTagId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO tags (name, canonical_key, category, origin)
            VALUES ('new_default_suggestion', 'new_default_suggestion', 'general', 'user');
            SELECT LAST_INSERT_ID();
            """);
        await connection.ExecuteAsync("""
            INSERT INTO card_tag_suggestions (card_id, tag_id, state)
            VALUES (@CardId, @TagId, 'pending');
            """, new { CardId = cardId, TagId = newTagId });
        Assert.Equal("visual", await connection.ExecuteScalarAsync<string>("""
            SELECT suggestion_source FROM card_tag_suggestions
            WHERE card_id = @CardId AND tag_id = @TagId;
            """, new { CardId = cardId, TagId = newTagId }));

        await runner.InitializeAsync();
        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(4, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_tag_suggestions WHERE card_id = @CardId AND suggestion_source = 'visual';",
            new { CardId = cardId }));
    }

    [DatabaseFact]
    public async Task VersionEightUpgradeRejectsStaleSuggestionBackupBeforeSchemaWrite()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var cardId = await SeedVersionSevenAsync(schema);
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 7);
        await File.WriteAllLinesAsync(manifest, (await File.ReadAllLinesAsync(manifest))
            .Select(line => line.StartsWith("count.card_tag_suggestions=", StringComparison.Ordinal)
                ? "count.card_tag_suggestions=0" : line));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.ConfigurationWithManifest(manifest)).InitializeAsync());
        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var connection = await schema.OpenAsync();
        Assert.Equal(7, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'card_tag_suggestions'
              AND COLUMN_NAME = 'suggestion_source';
            """));
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_tag_suggestions WHERE card_id = @CardId;", new { CardId = cardId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_image_analysis_jobs WHERE card_id = @CardId;", new { CardId = cardId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_speech_analysis_jobs WHERE card_id = @CardId;", new { CardId = cardId }));
    }

    [DatabaseFact]
    public async Task VersionNineUpgradePreservesPopulatedVersionEightDataAndCreatesInterestTables()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var cardId = await SeedVersionEightAsync(schema);
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 8);
        await using var connection = await schema.OpenAsync();

        var runner = CreateRunner(schema.ConfigurationWithManifest(manifest));
        await runner.InitializeAsync();

        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal("interest-signals-v1", await connection.ExecuteScalarAsync<string>(
            "SELECT name FROM schema_migrations WHERE version = 9;"));
        Assert.True(await schema.TableExistsAsync("card_view_events"));
        Assert.True(await schema.TableExistsAsync("card_daily_votes"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cards WHERE id = @CardId;", new { CardId = cardId }));
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_tag_suggestions WHERE card_id = @CardId;", new { CardId = cardId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_image_analysis_jobs WHERE card_id = @CardId;", new { CardId = cardId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM card_speech_analysis_jobs WHERE card_id = @CardId;", new { CardId = cardId }));
        await runner.InitializeAsync();
        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM schema_migrations;"));
    }

    [DatabaseFact]
    public async Task VersionNineUpgradeRejectsStaleInterestCountBeforeSchemaWrite()
        => await AssertVersionNineRejectsStaleInterestCountAsync("card_view_events");

    [DatabaseFact]
    public async Task VersionNineUpgradeRejectsStaleDailyVoteCountBeforeSchemaWrite()
        => await AssertVersionNineRejectsStaleInterestCountAsync("card_daily_votes");

    private static async Task AssertVersionNineRejectsStaleInterestCountAsync(string table)
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var cardId = await SeedVersionEightAsync(schema);
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 8);
        await File.WriteAllLinesAsync(manifest, (await File.ReadAllLinesAsync(manifest))
            .Select(line => line.StartsWith($"count.{table}=", StringComparison.Ordinal)
                ? $"count.{table}=1" : line));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.ConfigurationWithManifest(manifest)).InitializeAsync());

        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await schema.TableExistsAsync("card_view_events"));
        Assert.False(await schema.TableExistsAsync("card_daily_votes"));
        await using var connection = await schema.OpenAsync();
        Assert.Equal(8, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cards WHERE id = @CardId;", new { CardId = cardId }));
    }

    [DatabaseFact]
    public async Task VersionTenUpgradeCreatesCreationRequestsAndPreservesDeletedCardTombstone()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var cardId = await SeedVersionNineAsync(schema);
        Assert.False(await schema.TableExistsAsync("card_creation_requests"));
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 9);
        await using var connection = await schema.OpenAsync();

        var runner = CreateRunner(schema.ConfigurationWithManifest(manifest));
        await runner.InitializeAsync();

        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal("interest-signals-v1", await connection.ExecuteScalarAsync<string>(
            "SELECT name FROM schema_migrations WHERE version = 9;"));
        Assert.Equal("card-creation-requests-v1", await connection.ExecuteScalarAsync<string>(
            "SELECT name FROM schema_migrations WHERE version = 10;"));
        Assert.True(await schema.TableExistsAsync("card_creation_requests"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cards WHERE id = @CardId;", new { CardId = cardId }));

        var key = Guid.NewGuid().ToString("D");
        await connection.ExecuteAsync("""
            INSERT INTO card_creation_requests (scope, request_key, request_hash, card_id, created_at_utc)
            VALUES ('media', @Key, @Hash, @CardId, UTC_TIMESTAMP(6));
            """, new { Key = key, Hash = new byte[32], CardId = cardId });
        await connection.ExecuteAsync("DELETE FROM cards WHERE id = @CardId;", new { CardId = cardId });
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM card_creation_requests
            WHERE scope = 'media' AND request_key = @Key AND card_id IS NULL;
            """, new { Key = key }));
        await runner.InitializeAsync();
        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM schema_migrations;"));
    }

    [DatabaseFact]
    public async Task VersionTenUpgradeRejectsStaleCreationRequestCountBeforeSchemaWrite()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var cardId = await SeedVersionNineAsync(schema);
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 9);
        await File.WriteAllLinesAsync(manifest, (await File.ReadAllLinesAsync(manifest))
            .Select(line => line.StartsWith("count.card_creation_requests=", StringComparison.Ordinal)
                ? "count.card_creation_requests=1" : line));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.ConfigurationWithManifest(manifest)).InitializeAsync());

        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await schema.TableExistsAsync("card_creation_requests"));
        await using var connection = await schema.OpenAsync();
        Assert.Equal(9, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cards WHERE id = @CardId;", new { CardId = cardId }));
    }

    [DatabaseFact]
    public async Task VersionElevenUpgradeCreatesTagDecisionLogAndPreservesCounts()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var cardId = await SeedVersionTenAsync(schema);
        Assert.False(await schema.TableExistsAsync("card_tag_decisions"));
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 10);
        await using var connection = await schema.OpenAsync();
        var cardsBefore = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cards;");
        var suggestionsBefore = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_tag_suggestions;");

        var runner = CreateRunner(schema.ConfigurationWithManifest(manifest));
        await runner.InitializeAsync();

        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal("tag-decision-log-v1", await connection.ExecuteScalarAsync<string>(
            "SELECT name FROM schema_migrations WHERE version = 11;"));
        Assert.True(await schema.TableExistsAsync("card_tag_decisions"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_tag_decisions;"));
        Assert.Equal(cardsBefore, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cards;"));
        Assert.Equal(suggestionsBefore, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM card_tag_suggestions;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cards WHERE id = @CardId;", new { CardId = cardId }));
        await runner.InitializeAsync();
        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM schema_migrations;"));
    }

    [DatabaseFact]
    public async Task VersionElevenUpgradeRejectsStaleTagDecisionCountBeforeSchemaWrite()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var cardId = await SeedVersionTenAsync(schema);
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 10);
        await File.WriteAllLinesAsync(manifest, (await File.ReadAllLinesAsync(manifest))
            .Select(line => line.StartsWith("count.card_tag_decisions=", StringComparison.Ordinal)
                ? "count.card_tag_decisions=1" : line));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(schema.ConfigurationWithManifest(manifest)).InitializeAsync());

        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await schema.TableExistsAsync("card_tag_decisions"));
        await using var connection = await schema.OpenAsync();
        Assert.Equal(10, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cards WHERE id = @CardId;", new { CardId = cardId }));
    }

    [DatabaseFact]
    public async Task VersionElevenUpgradeAcceptsPreVersionElevenManifestWithoutTagDecisionCount()
    {
        // Backups taken before version 11 do not contain the key; it is optional below version 11.
        await using var schema = await IsolatedSchema.CreateAsync();
        await SeedVersionTenAsync(schema);
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 10);
        await File.WriteAllLinesAsync(manifest, (await File.ReadAllLinesAsync(manifest))
            .Where(line => !line.StartsWith("count.card_tag_decisions=", StringComparison.Ordinal)));

        await CreateRunner(schema.ConfigurationWithManifest(manifest)).InitializeAsync();

        await using var connection = await schema.OpenAsync();
        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
    }

    [DatabaseFact]
    public async Task BackupVerificationExcludesModelsAndAcceptsLegacyManifestsThatCountedThem()
    {
        foreach (var (appFileCount, accepted) in new[] { (1L, true), (2L, true), (3L, false) })
        {
            await using var schema = await IsolatedSchema.CreateAsync();
            var cardId = await SeedVersionElevenAsync(schema);
            Assert.True(cardId > 0);
            // One real app file plus one downloaded model file that backups exclude.
            Directory.CreateDirectory(Path.Combine(schema.AppDataPath, "cards"));
            await File.WriteAllTextAsync(Path.Combine(schema.AppDataPath, "cards", "a.bin"), "x");
            Directory.CreateDirectory(Path.Combine(schema.AppDataPath, "models", "wd"));
            await File.WriteAllTextAsync(Path.Combine(schema.AppDataPath, "models", "wd", "model.onnx"), "m");
            var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 11, appFileCount: appFileCount);

            var runner = CreateRunner(schema.ConfigurationWithManifest(manifest));
            if (accepted)
            {
                await runner.InitializeAsync();
                await using var connection = await schema.OpenAsync();
                Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => runner.InitializeAsync());
            }
        }
    }

    [DatabaseFact]
    public async Task VersionTwelveUpgradeAddsContentHashColumnAndIndexAndPreservesCounts()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var cardId = await SeedVersionElevenAsync(schema);
        await using var connection = await schema.OpenAsync();
        Assert.Equal(0, await ContentHashColumnCountAsync(connection));
        Assert.Equal(0, await ContentHashIndexCountAsync(connection));
        var manifest = await schema.WriteMatchingBackupManifestAsync(startingVersion: 11);
        var cardsBefore = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cards;");
        var tagsBefore = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tags;");

        var runner = CreateRunner(schema.ConfigurationWithManifest(manifest));
        await runner.InitializeAsync();

        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal("card-content-hash-v1", await connection.ExecuteScalarAsync<string>(
            "SELECT name FROM schema_migrations WHERE version = 12;"));
        Assert.Equal(1, await ContentHashColumnCountAsync(connection));
        Assert.Equal(1, await ContentHashIndexCountAsync(connection));
        Assert.Equal("char", await connection.ExecuteScalarAsync<string>("""
            SELECT DATA_TYPE FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'cards' AND COLUMN_NAME = 'content_sha256';
            """));
        Assert.Equal(64, await connection.ExecuteScalarAsync<int>("""
            SELECT CHARACTER_MAXIMUM_LENGTH FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'cards' AND COLUMN_NAME = 'content_sha256';
            """));
        Assert.Equal("YES", await connection.ExecuteScalarAsync<string>("""
            SELECT IS_NULLABLE FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'cards' AND COLUMN_NAME = 'content_sha256';
            """));
        Assert.Equal(cardsBefore, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cards;"));
        Assert.Equal(tagsBefore, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tags;"));
        Assert.Null(await connection.ExecuteScalarAsync<string?>(
            "SELECT content_sha256 FROM cards WHERE id = @CardId;", new { CardId = cardId }));
        await runner.InitializeAsync();
        Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(1, await ContentHashIndexCountAsync(connection));
    }

    private static Task<int> ContentHashColumnCountAsync(MySqlConnection connection) =>
        connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'cards' AND COLUMN_NAME = 'content_sha256';
            """);

    private static Task<int> ContentHashIndexCountAsync(MySqlConnection connection) =>
        connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'cards' AND INDEX_NAME = 'idx_cards_content_sha256';
            """);

    private static async Task<long> SeedVersionElevenAsync(IsolatedSchema schema)
    {
        var cardId = await SeedVersionTenAsync(schema);
        await new CatalogDatabaseInitializer(schema.Configuration,
            NullLogger<CatalogDatabaseInitializer>.Instance).InitializeTagDecisionLogAsync();
        await using var connection = await schema.OpenAsync();
        if (await ContentHashIndexCountAsync(connection) > 0)
            await connection.ExecuteAsync("ALTER TABLE cards DROP INDEX idx_cards_content_sha256;");
        if (await ContentHashColumnCountAsync(connection) > 0)
            await connection.ExecuteAsync("ALTER TABLE cards DROP COLUMN content_sha256;");
        const string name = "tag-decision-log-v1";
        await connection.ExecuteAsync("""
            INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
            VALUES (11, @Name, @Checksum, UTC_TIMESTAMP(6));
            """, new { Name = name, Checksum = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant() });
        return cardId;
    }

    private static async Task<long> SeedVersionTenAsync(IsolatedSchema schema)
    {
        var cardId = await SeedVersionNineAsync(schema);
        await new CardDatabaseInitializer(schema.Configuration).InitializeCreationRequestsAsync();
        await using var connection = await schema.OpenAsync();
        const string name = "card-creation-requests-v1";
        await connection.ExecuteAsync("""
            INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
            VALUES (10, @Name, @Checksum, UTC_TIMESTAMP(6));
            """, new { Name = name, Checksum = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant() });
        return cardId;
    }

    private static async Task<long> SeedVersionNineAsync(IsolatedSchema schema)
    {
        var cardId = await SeedVersionEightAsync(schema);
        await using var connection = await schema.OpenAsync();
        await connection.ExecuteAsync("DROP TABLE card_creation_requests;");
        await connection.ExecuteAsync("DROP TABLE IF EXISTS card_tag_decisions;");
        await new InterestDatabaseInitializer(schema.Configuration).InitializeAsync();
        const string name = "interest-signals-v1";
        await connection.ExecuteAsync("""
            INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
            VALUES (9, @Name, @Checksum, UTC_TIMESTAMP(6));
            """, new { Name = name, Checksum = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant() });
        return cardId;
    }

    private static async Task<long> SeedVersionEightAsync(IsolatedSchema schema)
    {
        var cardId = await SeedVersionSevenAsync(schema);
        var catalog = new CatalogDatabaseInitializer(schema.Configuration,
            NullLogger<CatalogDatabaseInitializer>.Instance);
        await catalog.InitializeTagSuggestionSourceAsync();
        await using var connection = await schema.OpenAsync();
        const string name = "tag-suggestion-source-v1";
        await connection.ExecuteAsync("""
            INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
            VALUES (8, @Name, @Checksum, UTC_TIMESTAMP(6));
            """, new { Name = name, Checksum = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant() });
        return cardId;
    }

    private static async Task<long> SeedVersionSevenAsync(IsolatedSchema schema)
    {
        await schema.ApplySqlAsync("001_cards.sql", "002_catalog.sql");
        await using var connection = await schema.OpenAsync();
        if (await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'card_tag_suggestions'
              AND COLUMN_NAME = 'suggestion_source';
            """) != 0)
        {
            await connection.ExecuteAsync("ALTER TABLE card_tag_suggestions DROP CHECK chk_card_tag_suggestions_source;");
            await connection.ExecuteAsync("ALTER TABLE card_tag_suggestions DROP COLUMN suggestion_source;");
        }
        await connection.ExecuteAsync("""
            CREATE TABLE schema_migrations (
                version INT NOT NULL PRIMARY KEY, name VARCHAR(128) NOT NULL,
                checksum CHAR(64) NOT NULL, applied_at_utc DATETIME(6) NOT NULL);
            """);
        foreach (var (version, name) in new[] {
            (1, "card-ownership-v1"), (2, "decks-v1"), (3, "catalog-v1"),
            (4, "danbooru-vocabulary-v1"), (5, "tag-suggestions-v1"),
            (6, "image-analysis-jobs-v1"), (7, "speech-analysis-jobs-v1") })
            await connection.ExecuteAsync("""
                INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
                VALUES (@Version, @Name, @Checksum, UTC_TIMESTAMP(6));
                """, new { Version = version, Name = name,
                    Checksum = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant() });
        var cardId = await InsertCardAsync(connection, "media");
        foreach (var state in new[] { "pending", "accepted", "rejected" })
        {
            var tagId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO tags (name, canonical_key, category, origin)
                VALUES (@Name, @Name, 'general', 'user');
                SELECT LAST_INSERT_ID();
                """, new { Name = $"existing_{state}_suggestion" });
            await connection.ExecuteAsync("""
                INSERT INTO card_tag_suggestions (card_id, tag_id, state)
                VALUES (@CardId, @TagId, @State);
                """, new { CardId = cardId, TagId = tagId, State = state });
        }
        await connection.ExecuteAsync("""
            INSERT INTO card_image_analysis_jobs (card_id, revision, state, available_at_utc)
            VALUES (@CardId, 'existing-image-revision', 'pending', UTC_TIMESTAMP(6));
            INSERT INTO card_speech_analysis_jobs (card_id, revision, state, available_at_utc)
            VALUES (@CardId, 'existing-speech-revision', 'pending', UTC_TIMESTAMP(6));
            """, new { CardId = cardId });
        return cardId;
    }

    private static async Task<long> SeedVersionSixAsync(IsolatedSchema schema)
    {
        await schema.ApplySqlAsync("001_cards.sql", "002_catalog.sql");
        await using var connection = await schema.OpenAsync();
        await connection.ExecuteAsync("DROP TABLE IF EXISTS card_speech_analysis_jobs;");
        await connection.ExecuteAsync("""
            CREATE TABLE schema_migrations (
                version INT NOT NULL PRIMARY KEY, name VARCHAR(128) NOT NULL,
                checksum CHAR(64) NOT NULL, applied_at_utc DATETIME(6) NOT NULL);
            """);
        foreach (var (version, name) in new[] {
            (1, "card-ownership-v1"), (2, "decks-v1"), (3, "catalog-v1"),
            (4, "danbooru-vocabulary-v1"), (5, "tag-suggestions-v1"),
            (6, "image-analysis-jobs-v1") })
            await connection.ExecuteAsync("""
                INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
                VALUES (@Version, @Name, @Checksum, UTC_TIMESTAMP(6));
                """, new { Version = version, Name = name,
                    Checksum = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant() });
        var cardId = await InsertCardAsync(connection, "media");
        await connection.ExecuteAsync("""
            INSERT INTO card_image_analysis_jobs
                (card_id, revision, state, available_at_utc)
            VALUES (@CardId, 'existing-image-revision', 'pending', UTC_TIMESTAMP(6));
            """, new { CardId = cardId });
        return cardId;
    }

    private static async Task SeedVersionFiveAsync(IsolatedSchema schema)
    {
        await schema.ApplySqlAsync("001_cards.sql", "002_catalog.sql");
        await using var connection = await schema.OpenAsync();
        await connection.ExecuteAsync("DROP TABLE IF EXISTS card_image_analysis_jobs;");
        await connection.ExecuteAsync("""
            CREATE TABLE schema_migrations (
                version INT NOT NULL PRIMARY KEY, name VARCHAR(128) NOT NULL,
                checksum CHAR(64) NOT NULL, applied_at_utc DATETIME(6) NOT NULL);
            """);
        foreach (var (version, name) in new[] {
            (1, "card-ownership-v1"), (2, "decks-v1"), (3, "catalog-v1"),
            (4, "danbooru-vocabulary-v1"), (5, "tag-suggestions-v1") })
            await connection.ExecuteAsync("""
                INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
                VALUES (@Version, @Name, @Checksum, UTC_TIMESTAMP(6));
                """, new { Version = version, Name = name,
                    Checksum = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant() });
        var cardId = await InsertCardAsync(connection, "media");
        var tagId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO tags (name, canonical_key, category, origin)
            VALUES ('existing_suggestion', 'existing_suggestion', 'general', 'user');
            SELECT LAST_INSERT_ID();
            """);
        await connection.ExecuteAsync("""
            INSERT INTO card_tag_suggestions (card_id, tag_id, state)
            VALUES (@CardId, @TagId, 'pending');
            """, new { CardId = cardId, TagId = tagId });
    }

    private static async Task SeedVersionFourAsync(IsolatedSchema schema)
    {
        await schema.ApplySqlAsync("001_cards.sql", "002_catalog.sql");
        await using var connection = await schema.OpenAsync();
        await connection.ExecuteAsync("DROP TABLE IF EXISTS card_tag_suggestions;");
        await connection.ExecuteAsync("""
            CREATE TABLE schema_migrations (
                version INT NOT NULL PRIMARY KEY, name VARCHAR(128) NOT NULL,
                checksum CHAR(64) NOT NULL, applied_at_utc DATETIME(6) NOT NULL);
            """);
        foreach (var (version, name) in new[] {
            (1, "card-ownership-v1"), (2, "decks-v1"), (3, "catalog-v1"),
            (4, "danbooru-vocabulary-v1") })
            await connection.ExecuteAsync("""
                INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
                VALUES (@Version, @Name, @Checksum, UTC_TIMESTAMP(6));
                """, new { Version = version, Name = name,
                    Checksum = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant() });
        var cardId = await InsertCardAsync(connection, "media");
        var tagId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO tags (name, canonical_key, category, origin)
            VALUES ('existing_tag', 'existing_tag', 'general', 'user');
            SELECT LAST_INSERT_ID();
            """);
        await connection.ExecuteAsync("INSERT INTO tag_aliases (tag_id, alias) VALUES (@TagId, 'existing_alias');", new { TagId = tagId });
        await connection.ExecuteAsync("INSERT INTO card_tags (card_id, tag_id, assignment_source) VALUES (@CardId, @TagId, 'manual');",
            new { CardId = cardId, TagId = tagId });
    }

    private static async Task SeedVersionThreeAsync(IsolatedSchema schema)
    {
        await schema.ApplySqlAsync("001_cards.sql", "002_catalog.sql");
        await using var connection = await schema.OpenAsync();
        // The checked-in bootstrap schema may already include v4 additions; reconstruct v3 explicitly.
        await connection.ExecuteAsync("DROP TABLE IF EXISTS card_tag_suggestions;");
        await connection.ExecuteAsync("DROP TABLE IF EXISTS danbooru_import_state;");
        if (await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'tag_aliases'
              AND INDEX_NAME = 'uq_tag_alias_danbooru';
            """) != 0)
            await connection.ExecuteAsync("ALTER TABLE tag_aliases DROP INDEX uq_tag_alias_danbooru;");
        if (await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'tag_aliases' AND COLUMN_NAME = 'danbooru_id';
            """) != 0)
            await connection.ExecuteAsync("ALTER TABLE tag_aliases DROP COLUMN danbooru_id;");
        if (await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'tag_aliases' AND COLUMN_NAME = 'origin';
            """) != 0)
            await connection.ExecuteAsync("ALTER TABLE tag_aliases DROP COLUMN origin;");
        await connection.ExecuteAsync("""
            CREATE TABLE schema_migrations (
                version INT NOT NULL PRIMARY KEY, name VARCHAR(128) NOT NULL,
                checksum CHAR(64) NOT NULL, applied_at_utc DATETIME(6) NOT NULL);
            """);
        foreach (var (version, name) in new[] { (1, "card-ownership-v1"), (2, "decks-v1"), (3, "catalog-v1") })
            await connection.ExecuteAsync("""
                INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
                VALUES (@Version, @Name, @Checksum, UTC_TIMESTAMP(6));
                """, new { Version = version, Name = name,
                    Checksum = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant() });
        var cardId = await InsertCardAsync(connection, "media");
        var tagId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO tags (name, canonical_key, category, origin)
            VALUES ('legacy_tag', 'legacy_tag', 'general', 'user');
            SELECT LAST_INSERT_ID();
            """);
        var sourceId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO sources (name, canonical_key) VALUES ('legacy_source', 'legacy_source');
            SELECT LAST_INSERT_ID();
            """);
        await connection.ExecuteAsync("INSERT INTO tag_aliases (tag_id, alias) VALUES (@TagId, 'legacy_alias');", new { TagId = tagId });
        await connection.ExecuteAsync("INSERT INTO card_tags (card_id, tag_id) VALUES (@CardId, @TagId);", new { CardId = cardId, TagId = tagId });
        await connection.ExecuteAsync("INSERT INTO card_sources (card_id, source_id) VALUES (@CardId, @SourceId);", new { CardId = cardId, SourceId = sourceId });
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
            new CatalogDatabaseInitializer(configuration, NullLogger<CatalogDatabaseInitializer>.Instance),
            new InterestDatabaseInitializer(configuration));

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

        public string AppDataPath => Path.Combine(_temporaryRoot, "app_data");

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

        public async Task<string> WriteMatchingBackupManifestAsync(int startingVersion = 0, long appFileCount = 0)
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
            var tagAliases = await CountIfTableExistsAsync(connection, "tag_aliases");
            var cardTags = await CountIfTableExistsAsync(connection, "card_tags");
            var cardSources = await CountIfTableExistsAsync(connection, "card_sources");
            var suggestions = await CountIfTableExistsAsync(connection, "card_tag_suggestions");
            var imageAnalysisJobs = await CountIfTableExistsAsync(connection, "card_image_analysis_jobs");
            var speechAnalysisJobs = await CountIfTableExistsAsync(connection, "card_speech_analysis_jobs");
            var viewEvents = await CountIfTableExistsAsync(connection, "card_view_events");
            var dailyVotes = await CountIfTableExistsAsync(connection, "card_daily_votes");
            var creationRequests = await CountIfTableExistsAsync(connection, "card_creation_requests");
            var tagDecisions = await CountIfTableExistsAsync(connection, "card_tag_decisions");
            await File.WriteAllLinesAsync(manifestPath,
            [
                "format=1",
                $"database={_name}",
                $"startingVersion={startingVersion}",
                $"database_sha256={await Sha256Async(databasePath)}",
                $"app_data_sha256={await Sha256Async(dataPath)}",
                $"app_file_count={appFileCount}",
                $"count.cards={cards}",
                $"count.card_relations={relations}",
                $"count.decks={decks}",
                $"count.deck_cards={deckCards}",
                $"count.tags={tags}",
                $"count.sources={sources}",
                $"count.tag_aliases={tagAliases}",
                $"count.card_tags={cardTags}",
                $"count.card_sources={cardSources}",
                $"count.card_tag_suggestions={suggestions}",
                $"count.card_image_analysis_jobs={imageAnalysisJobs}",
                $"count.card_speech_analysis_jobs={speechAnalysisJobs}",
                $"count.card_view_events={viewEvents}",
                $"count.card_daily_votes={dailyVotes}",
                $"count.card_creation_requests={creationRequests}",
                $"count.card_tag_decisions={tagDecisions}",
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

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using MyApp.Api.Cards;
using MyApp.Api.Catalog;
using MyApp.Api.Decks;
using MyApp.Api.Interest;
using MySqlConnector;

namespace MyApp.Api.Migrations;

public sealed class SchemaMigrationRunner
{
    private static readonly (int Version, string Name)[] Versions =
    [
        (1, "card-ownership-v1"),
        (2, "decks-v1"),
        (3, "catalog-v1"),
        (4, "danbooru-vocabulary-v1"),
        (5, "tag-suggestions-v1"),
        (6, "image-analysis-jobs-v1"),
        (7, "speech-analysis-jobs-v1"),
        (8, "tag-suggestion-source-v1"),
        (9, "interest-signals-v1"),
        (10, "card-creation-requests-v1"),
        (11, "tag-decision-log-v1"),
        (12, "card-content-hash-v1"),
    ];

    private readonly string _connectionString;
    private readonly string _appDataRoot;
    private readonly string? _backupManifest;
    private readonly ILogger<SchemaMigrationRunner> _logger;
    private readonly CardDatabaseInitializer _cards;
    private readonly DeckDatabaseInitializer _decks;
    private readonly CatalogDatabaseInitializer _catalog;
    private readonly InterestDatabaseInitializer _interest;

    public SchemaMigrationRunner(
        IConfiguration configuration,
        ILogger<SchemaMigrationRunner> logger,
        CardDatabaseInitializer cards,
        DeckDatabaseInitializer decks,
        CatalogDatabaseInitializer catalog,
        InterestDatabaseInitializer interest)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing database connection string.");
        _appDataRoot = configuration["NODECK_APP_DATA"]
            ?? Environment.GetEnvironmentVariable("NODECK_APP_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _backupManifest = configuration["NODECK_MIGRATION_BACKUP_MANIFEST"];
        _logger = logger;
        _cards = cards;
        _decks = decks;
        _catalog = catalog;
        _interest = interest;
    }

    public async Task InitializeAsync()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        var database = connection.Database;
        var lockName = "nodeck-schema-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(database)))[..24];
        var acquired = await connection.ExecuteScalarAsync<int?>(
            "SELECT GET_LOCK(@LockName, 30);", new { LockName = lockName });
        if (acquired != 1)
            throw new InvalidOperationException("Could not acquire the database migration lock.");

        try
        {
            if (!await TableExistsAsync(connection, "cards") && await IsEmptyDatabaseAsync(connection))
                await CreateBaseSchemaAsync(connection);
            if (!await TableExistsAsync(connection, "cards") ||
                !await TableExistsAsync(connection, "card_relations"))
                throw new InvalidOperationException("Base card tables are missing. Initialize the database before starting the API.");

            var applied = await ReadLedgerAsync(connection);
            ValidateLedger(applied);
            var currentVersion = applied.Count == 0 ? 0 : applied.Max(row => row.Version);
            if (currentVersion == Versions[^1].Version)
            {
                // Legacy card endpoints remain available during the transition; keep their catalog copies current.
                await _catalog.ReconcileLegacyAsync();
                await VerifyReferencesAsync(connection);
                await VerifyCollectionOwnershipAsync(connection);
                return;
            }

            var before = await SnapshotAsync(connection);
            await VerifyReferencesAsync(connection);
            await VerifyCollectionOwnershipAsync(connection);
            if (before.HasUserData)
                VerifyBackup(database, currentVersion, before);

            await connection.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INT NOT NULL PRIMARY KEY,
                    name VARCHAR(128) NOT NULL,
                    checksum CHAR(64) NOT NULL,
                    applied_at_utc DATETIME(6) NOT NULL
                );
                """);

            var previous = before;
            foreach (var (version, name) in Versions.Where(item => item.Version > currentVersion))
            {
                _logger.LogInformation("Applying schema migration {Version}: {Name}", version, name);
                switch (version)
                {
                    case 1: await _cards.InitializeAsync(); break;
                    case 2: await _decks.InitializeAsync(); break;
                    case 3: await _catalog.InitializeAsync(); break;
                    case 4: await _catalog.InitializeDanbooruVocabularyAsync(); break;
                    case 5: await _catalog.InitializeTagSuggestionsAsync(); break;
                    case 6: await _catalog.InitializeImageAnalysisJobsAsync(); break;
                    case 7: await _catalog.InitializeSpeechAnalysisJobsAsync(); break;
                    case 8: await _catalog.InitializeTagSuggestionSourceAsync(); break;
                    case 9: await _interest.InitializeAsync(); break;
                    case 10: await _cards.InitializeCreationRequestsAsync(); break;
                    case 11: await _catalog.InitializeTagDecisionLogAsync(); break;
                    case 12: await _cards.InitializeContentHashAsync(); break;
                }

                var after = await SnapshotAsync(connection);
                if (after.Cards != before.Cards || after.CardRelations != before.CardRelations ||
                    after.DeckCards != before.DeckCards || after.AppFiles != before.AppFiles)
                    throw new InvalidOperationException(
                        $"Migration {version} changed existing card, relation, deck membership, or file counts. " +
                        "Stop the API and restore the pre-migration backup before accepting writes.");
                if (version == 4 && (after.TagAliases != previous.TagAliases ||
                    after.CardTags != previous.CardTags || after.CardSources != previous.CardSources))
                    throw new InvalidOperationException(
                        "Migration 4 changed existing alias, tag assignment, or source assignment counts. " +
                        "Stop the API and restore the pre-migration backup before accepting writes.");
                if (version == 5 && (after.Tags != previous.Tags || after.Sources != previous.Sources ||
                    after.TagAliases != previous.TagAliases || after.CardTags != previous.CardTags ||
                    after.CardSources != previous.CardSources ||
                    after.CardTagSuggestions != previous.CardTagSuggestions))
                    throw new InvalidOperationException(
                        "Migration 5 changed existing catalog or suggestion counts. " +
                        "Stop the API and restore the pre-migration backup before accepting writes.");
                if (version == 6 && after != previous)
                    throw new InvalidOperationException(
                        "Migration 6 changed existing row or file counts. " +
                        "Stop the API and restore the pre-migration backup before accepting writes.");
                if (version == 7 && after != previous)
                    throw new InvalidOperationException(
                        "Migration 7 changed existing row or file counts. " +
                        "Stop the API and restore the pre-migration backup before accepting writes.");
                if (version == 8 && after != previous)
                    throw new InvalidOperationException(
                        "Migration 8 changed existing row or file counts. " +
                        "Stop the API and restore the pre-migration backup before accepting writes.");
                if (version == 9 && after != previous)
                    throw new InvalidOperationException(
                        "Migration 9 changed existing row or file counts. " +
                        "Stop the API and restore the pre-migration backup before accepting writes.");
                if (version == 10 && after != previous)
                    throw new InvalidOperationException(
                        "Migration 10 changed existing row or file counts. " +
                        "Stop the API and restore the pre-migration backup before accepting writes.");
                if (version == 11 && after != previous)
                    throw new InvalidOperationException(
                        "Migration 11 changed existing row or file counts. " +
                        "Stop the API and restore the pre-migration backup before accepting writes.");
                if (version == 12 && after != previous)
                    throw new InvalidOperationException(
                        "Migration 12 changed existing row or file counts. " +
                        "Stop the API and restore the pre-migration backup before accepting writes.");
                await VerifyReferencesAsync(connection);
                await VerifyCollectionOwnershipAsync(connection);
                await connection.ExecuteAsync("""
                    INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
                    VALUES (@Version, @Name, @Checksum, UTC_TIMESTAMP(6));
                    """, new { Version = version, Name = name, Checksum = Checksum(name) });
                previous = after;
            }
        }
        finally
        {
            await connection.ExecuteAsync("SELECT RELEASE_LOCK(@LockName);", new { LockName = lockName });
        }
    }

    private static string Checksum(string name) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();

    private static void ValidateLedger(IReadOnlyList<MigrationRow> applied)
    {
        if (applied.Any(row => row.Version > Versions[^1].Version))
            throw new InvalidOperationException("Database schema is newer than this API version.");
        foreach (var row in applied)
        {
            var expected = Versions.SingleOrDefault(item => item.Version == row.Version);
            if (expected == default || row.Name != expected.Name || row.Checksum != Checksum(expected.Name))
                throw new InvalidOperationException($"Unknown or modified schema migration {row.Version}.");
        }
        for (var version = 1; version <= applied.Count; version++)
            if (!applied.Any(row => row.Version == version))
                throw new InvalidOperationException("Schema migration ledger has a gap.");
    }

    private async Task<IReadOnlyList<MigrationRow>> ReadLedgerAsync(MySqlConnection connection)
    {
        if (!await TableExistsAsync(connection, "schema_migrations")) return [];
        return (await connection.QueryAsync<MigrationRow>(
            "SELECT version, name, checksum FROM schema_migrations ORDER BY version;")).ToList();
    }

    private static Task<bool> IsEmptyDatabaseAsync(MySqlConnection connection) =>
        connection.ExecuteScalarAsync<bool>(
            "SELECT COUNT(*) = 0 FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE();");

    /// <summary>
    /// Creates the base tables from the SQL files in database/init, embedded in the assembly. MySQL's
    /// own init directory is not available when the stack is installed from a single compose file
    /// (for example through the ZimaOS import dialog), so an empty database is set up here instead.
    /// </summary>
    private async Task CreateBaseSchemaAsync(MySqlConnection connection)
    {
        var assembly = typeof(SchemaMigrationRunner).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(BaseSchemaResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0)
            throw new InvalidOperationException("The base schema SQL is missing from the build.");
        _logger.LogInformation("Empty database: creating the base schema from {Count} files.", resources.Length);
        foreach (var resource in resources)
        {
            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = await reader.ReadToEndAsync();
            foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await connection.ExecuteAsync(statement);
        }
    }

    internal const string BaseSchemaResourcePrefix = "nodeck.schema.";

    private static Task<bool> TableExistsAsync(MySqlConnection connection, string table) =>
        connection.ExecuteScalarAsync<bool>("""
            SELECT COUNT(*) FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @Table;
            """, new { Table = table });

    private async Task<DatabaseSnapshot> SnapshotAsync(MySqlConnection connection)
    {
        async Task<long> Count(string table) => await TableExistsAsync(connection, table)
            ? await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM `{table}`;") : 0;
        long appFiles = 0;
        long userAppFiles = 0;
        long modelFiles = 0;
        if (Directory.Exists(_appDataRoot))
        {
            var modelsRoot = Path.Combine(Path.GetFullPath(_appDataRoot), "models") + Path.DirectorySeparatorChar;
            foreach (var path in Directory.EnumerateFiles(_appDataRoot, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                // Downloaded models are re-installable and excluded from backups (top-level models/ only).
                if (Path.GetFullPath(path).StartsWith(modelsRoot, StringComparison.Ordinal))
                {
                    modelFiles++;
                    continue;
                }
                appFiles++;
                if (!string.Equals(Path.GetFileName(path), ".gitkeep", StringComparison.Ordinal))
                    userAppFiles++;
            }
        }
        var userDecks = await TableExistsAsync(connection, "decks")
            ? await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM decks WHERE system_key IS NULL OR system_key <> 'favorites';") : 0;
        return new DatabaseSnapshot(
            await Count("cards"),
            await Count("card_relations"),
            await Count("decks"),
            await Count("deck_cards"),
            await Count("tags"),
            await Count("sources"),
            await Count("tag_aliases"),
            await Count("card_tags"),
            await Count("card_sources"),
            await Count("card_tag_suggestions"),
            await Count("card_image_analysis_jobs"),
            await Count("card_speech_analysis_jobs"),
            await Count("card_view_events"),
            await Count("card_daily_votes"),
            await Count("card_creation_requests"),
            await Count("card_tag_decisions"),
            appFiles,
            userAppFiles,
            userDecks,
            modelFiles);
    }

    private void VerifyBackup(string database, int currentVersion, DatabaseSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(_backupManifest) || !File.Exists(_backupManifest))
            throw new InvalidOperationException(
                "A populated database needs a verified backup before schema migration. " +
                "Run the backup script, then set NODECK_MIGRATION_BACKUP_MANIFEST to its manifest path.");
        var entries = File.ReadAllLines(_backupManifest)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

        string Require(string key) => entries.TryGetValue(key, out var value) ? value
            : throw new InvalidOperationException($"Backup manifest is missing {key}.");
        long Number(string key) => long.TryParse(Require(key), NumberStyles.None,
            CultureInfo.InvariantCulture, out var value) && value >= 0 ? value
            : throw new InvalidOperationException($"Backup manifest has invalid {key}.");
        long CreationRequestsCount() => currentVersion < 10 &&
            !entries.ContainsKey("count.card_creation_requests") ? 0 : Number("count.card_creation_requests");

        long TagDecisionsCount() => currentVersion < 11 &&
            !entries.ContainsKey("count.card_tag_decisions") ? 0 : Number("count.card_tag_decisions");

        if (Require("format") != "1" || Require("database") != database ||
            Number("startingVersion") != currentVersion ||
            Number("count.cards") != snapshot.Cards ||
            Number("count.card_relations") != snapshot.CardRelations ||
            Number("count.decks") != snapshot.Decks ||
            Number("count.deck_cards") != snapshot.DeckCards ||
            Number("count.tags") != snapshot.Tags ||
            Number("count.sources") != snapshot.Sources ||
            Number("count.tag_aliases") != snapshot.TagAliases ||
            Number("count.card_tags") != snapshot.CardTags ||
            Number("count.card_sources") != snapshot.CardSources ||
            Number("count.card_tag_suggestions") != snapshot.CardTagSuggestions ||
            Number("count.card_image_analysis_jobs") != snapshot.CardImageAnalysisJobs ||
            Number("count.card_speech_analysis_jobs") != snapshot.CardSpeechAnalysisJobs ||
            Number("count.card_view_events") != snapshot.CardViewEvents ||
            Number("count.card_daily_votes") != snapshot.CardDailyVotes ||
            CreationRequestsCount() != snapshot.CardCreationRequests ||
            TagDecisionsCount() != snapshot.CardTagDecisions ||
            // Current backups exclude models/; manifests from the older script counted them too.
            (Number("app_file_count") != snapshot.AppFiles &&
                Number("app_file_count") != snapshot.AppFiles + snapshot.ModelFiles))
            throw new InvalidOperationException("Backup manifest does not match the current database and files. Create a new backup.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(_backupManifest))!;
        VerifyHash(Path.Combine(directory, "database.sql"), Require("database_sha256"));
        VerifyHash(Path.Combine(directory, "app_data.tar"), Require("app_data_sha256"));
    }

    private static void VerifyHash(string path, string expected)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Backup artifact is missing: {Path.GetFileName(path)}.");
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Backup artifact checksum mismatch: {Path.GetFileName(path)}.");
    }

    private static async Task VerifyReferencesAsync(MySqlConnection connection)
    {
        var missingCards = await connection.ExecuteScalarAsync<long>("""
            SELECT COUNT(*) FROM card_relations r
            LEFT JOIN cards a ON a.id = r.from_card_id
            LEFT JOIN cards b ON b.id = r.to_card_id
            WHERE a.id IS NULL OR b.id IS NULL;
            """);
        if (missingCards != 0) throw new InvalidOperationException("Card relations contain missing cards.");

        if (await TableExistsAsync(connection, "deck_cards"))
        {
            var missingDecks = await connection.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM deck_cards dc
                LEFT JOIN cards c ON c.id = dc.card_id
                LEFT JOIN decks d ON d.id = dc.deck_id
                WHERE c.id IS NULL OR d.id IS NULL;
                """);
            if (missingDecks != 0) throw new InvalidOperationException("Deck memberships contain missing cards or decks.");
        }
        if (await TableExistsAsync(connection, "card_tags"))
        {
            var missingTags = await connection.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM card_tags ct LEFT JOIN cards c ON c.id = ct.card_id
                LEFT JOIN tags t ON t.id = ct.tag_id WHERE c.id IS NULL OR t.id IS NULL;
                """);
            if (missingTags != 0) throw new InvalidOperationException("Tag assignments contain missing cards or tags.");
        }
        if (await TableExistsAsync(connection, "card_sources"))
        {
            var missingSources = await connection.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM card_sources cs LEFT JOIN cards c ON c.id = cs.card_id
                LEFT JOIN sources s ON s.id = cs.source_id WHERE c.id IS NULL OR s.id IS NULL;
                """);
            if (missingSources != 0) throw new InvalidOperationException("Source assignments contain missing cards or sources.");
        }
    }

    private static async Task VerifyCollectionOwnershipAsync(MySqlConnection connection)
    {
        var invalid = await connection.ExecuteScalarAsync<long>("""
            SELECT COUNT(*) FROM card_relations r JOIN cards p ON p.id = r.from_card_id
            JOIN cards c ON c.id = r.to_card_id
            WHERE r.relation_type = 'contains' AND (p.type NOT IN ('comic', 'set') OR c.type <> 'media');
            """);
        var duplicate = await connection.ExecuteScalarAsync<long>("""
            SELECT COUNT(*) FROM (
                SELECT to_card_id FROM card_relations WHERE relation_type = 'contains'
                GROUP BY to_card_id HAVING COUNT(*) > 1
            ) duplicates;
            """);
        var malformedPosition = await connection.ExecuteScalarAsync<long>("""
            SELECT COUNT(*) FROM card_relations WHERE relation_type = 'contains'
              AND (JSON_TYPE(JSON_EXTRACT(properties, '$.position')) IS NULL
                   OR JSON_TYPE(JSON_EXTRACT(properties, '$.position')) <> 'INTEGER'
                   OR CAST(JSON_UNQUOTE(JSON_EXTRACT(properties, '$.position')) AS SIGNED) < 0);
            """);
        var duplicatePosition = await connection.ExecuteScalarAsync<long>("""
            SELECT COUNT(*) FROM (
                SELECT from_card_id FROM card_relations WHERE relation_type = 'contains'
                GROUP BY from_card_id
                HAVING COUNT(*) <> COUNT(DISTINCT CAST(JSON_UNQUOTE(JSON_EXTRACT(properties, '$.position')) AS SIGNED))
            ) duplicates;
            """);
        if (invalid != 0 || duplicate != 0 || malformedPosition != 0 || duplicatePosition != 0)
            throw new InvalidOperationException("Collection ownership is invalid; resolve it before schema migration.");
    }

    private sealed class MigrationRow
    {
        public int Version { get; set; }
        public string Name { get; set; } = "";
        public string Checksum { get; set; } = "";
    }

    private sealed record DatabaseSnapshot(
        long Cards, long CardRelations, long Decks, long DeckCards,
        long Tags, long Sources, long TagAliases, long CardTags, long CardSources,
        long CardTagSuggestions, long CardImageAnalysisJobs, long CardSpeechAnalysisJobs,
        long CardViewEvents, long CardDailyVotes, long CardCreationRequests, long CardTagDecisions,
        long AppFiles, long UserAppFiles, long UserDecks, long ModelFiles = 0)
    {
        public bool HasUserData => Cards > 0 || CardRelations > 0 || DeckCards > 0 ||
            UserDecks > 0 || Tags > 0 || Sources > 0 || TagAliases > 0 ||
            CardTags > 0 || CardSources > 0 || CardTagSuggestions > 0 ||
            CardImageAnalysisJobs > 0 || CardSpeechAnalysisJobs > 0 ||
            CardViewEvents > 0 || CardDailyVotes > 0 || CardCreationRequests > 0 ||
            CardTagDecisions > 0 || UserAppFiles > 0;
    }
}

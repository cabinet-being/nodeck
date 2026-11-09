using System.Net;
using System.Text;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyApp.Api.Cards;
using MyApp.Api.Analysis;
using MyApp.Api.Catalog;
using MyApp.Api.Decks;
using MyApp.Api.Interest;
using MyApp.Api.Migrations;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class DanbooruTagImporterTests
{
    [DatabaseFact]
    public async Task WdSeedIsIdempotentAndDoesNotCompleteFullImport()
    {
        await using var database = await ImportDatabase.CreateAsync();
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "tag_id,name,category,count\n10,general_tag,0,100\n20,character_tag,4,50\n30,rating_tag,9,20\n");
            var seeder = new WdVocabularySeeder(database.ConnectionString);
            var first = await seeder.RunAsync(path);
            var second = await seeder.RunAsync(path);

            Assert.Equal(new WdVocabularySeedResult(2, 2, 0, 0), first);
            Assert.Equal(new WdVocabularySeedResult(2, 0, 2, 0), second);
            Assert.Equal(new[] { "general", "character" },
                (await database.QueryAsync<string>("SELECT category FROM tags ORDER BY danbooru_id;")).ToArray());
            Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM danbooru_import_state WHERE kind = 'tags' AND completed = TRUE;"));
            Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM danbooru_import_state WHERE kind = 'wd_vocabulary' AND completed = TRUE;"));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = database.ConnectionString
            }).Build();
            Assert.True(await new AnalysisQueueRepository(config).IsVocabularyReadyAsync(default));
            using var http = new HttpClient(new StubHandler(uri => uri.AbsolutePath == "/tags.json" && !uri.Query.Contains("page=")
                ? """[{"id":20,"name":"character_tag","category":4},{"id":10,"name":"general_tag","category":0}]"""
                : "[]"));
            var full = await new DanbooruTagImporter(database.ConnectionString, http).RunAsync(
                new DanbooruImportOptions { UserAgent = "nodeck-tests/1.0", PageSize = 2 });
            Assert.True(full.TagsCompleted);
            Assert.True(full.AliasesCompleted);
            Assert.Equal(2, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tags WHERE origin = 'danbooru';"));
        }
        finally { File.Delete(path); }
    }

    [DatabaseFact]
    public async Task WdSeedSkipsOccupiedNamesAndPreservesExistingRows()
    {
        await using var database = await ImportDatabase.CreateAsync();
        await database.ExecuteAsync("INSERT INTO tags (name, canonical_key, category, origin) VALUES ('custom_name', 'custom_name', 'general', 'user');");
        await database.ExecuteAsync("INSERT INTO tags (name, canonical_key, category, origin, danbooru_id) VALUES ('old_name', 'danbooru-tag:20', 'general', 'danbooru', 20);");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "tag_id,name,category,count\n10,custom_name,0,100\n20,new_name,4,50\n30,safe_name,0,20\n");
            var result = await new WdVocabularySeeder(database.ConnectionString).RunAsync(path);

            Assert.Equal(new WdVocabularySeedResult(3, 1, 0, 2), result);
            Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tags WHERE name = 'custom_name' AND origin = 'user';"));
            Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tags WHERE danbooru_id = 20 AND name = 'old_name' AND category = 'general';"));
            Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tags WHERE name = 'new_name';"));
        }
        finally { File.Delete(path); }
    }

    [DatabaseFact]
    public async Task InvalidWdCsvDoesNotPublishReadiness()
    {
        await using var database = await ImportDatabase.CreateAsync();
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "tag_id,name,category,count\n10,good,0,100\ninvalid,row,4,10\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => new WdVocabularySeeder(database.ConnectionString).RunAsync(path));
            Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tags;"));
            Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM danbooru_import_state WHERE kind = 'wd_vocabulary';"));
        }
        finally { File.Delete(path); }
    }

    [DatabaseFact]
    public async Task TwoPagesResumeAndRerunKeepUniqueTagsCategoriesAndAliasState()
    {
        await using var database = await ImportDatabase.CreateAsync();
        var replies = new Dictionary<string, string>
        {
            ["tags.json?limit=2"] = """[{"id":40,"name":"general_tag","category":0},{"id":30,"name":"artist_tag","category":1}]""",
            ["tags.json?limit=2&page=b30"] = """[{"id":20,"name":"copyright_tag","category":3},{"id":10,"name":"character_tag","category":4}]""",
            ["tags.json?limit=2&page=b10"] = "[]",
            ["tag_aliases.json?limit=2"] = """[{"id":90,"antecedent_name":"active_alias","consequent_name":"general_tag","status":"active"},{"id":80,"antecedent_name":"deleted_alias","consequent_name":"general_tag","status":"deleted"}]""",
            ["tag_aliases.json?limit=2&page=b80"] = "[]",
        };
        using var http = new HttpClient(new StubHandler(uri => replies[uri.PathAndQuery.TrimStart('/')]));
        var importer = new DanbooruTagImporter(database.ConnectionString, http);
        var options = new DanbooruImportOptions { UserAgent = "nodeck-tests/1.0", PageSize = 2, MaxPages = 1 };

        var first = await importer.RunAsync(options);
        Assert.Equal(1, first.Pages);
        Assert.Equal(2, first.Processed);
        Assert.Equal(30, await database.ScalarAsync<long>("SELECT cursor_before_id FROM danbooru_import_state WHERE kind = 'tags';"));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases;"));
        await database.ExecuteAsync("""
            INSERT INTO tag_aliases (tag_id, alias, origin, danbooru_id)
            SELECT id, 'deleted_alias', 'danbooru', 80 FROM tags WHERE name = 'general_tag';
            """);

        var second = await importer.RunAsync(new DanbooruImportOptions { UserAgent = options.UserAgent, PageSize = 2, MaxPages = 4 });
        Assert.Equal(4, second.Pages);
        Assert.True(second.TagsCompleted);
        Assert.True(second.AliasesCompleted);
        Assert.Equal(new[] { "general", "artist", "copyright", "character" },
            (await database.QueryAsync<string>("SELECT category FROM tags WHERE origin = 'danbooru' ORDER BY danbooru_id DESC;")).ToArray());
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases WHERE alias = 'active_alias' AND origin = 'danbooru' AND danbooru_id = 90;"));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases WHERE alias = 'deleted_alias';"));

        var again = await importer.RunAsync(new DanbooruImportOptions { UserAgent = options.UserAgent, PageSize = 2 });
        Assert.Equal(0, again.Pages);
        Assert.Equal(4, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tags WHERE origin = 'danbooru';"));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases;"));
    }

    [DatabaseFact]
    public async Task ImportedNamesCannotReplaceCustomOrLegacyTagsAndAliases()
    {
        await using var database = await ImportDatabase.CreateAsync();
        var customId = await database.ScalarAsync<long>("""
            INSERT INTO tags (name, canonical_key, category, origin)
            VALUES ('custom_name', 'custom_name', 'general', 'user'); SELECT LAST_INSERT_ID();
            """);
        await database.ExecuteAsync("INSERT INTO tags (name, canonical_key, category, origin) VALUES ('legacy_name', 'legacy_name', 'artist', 'legacy');");
        await database.ExecuteAsync("INSERT INTO tag_aliases (tag_id, alias) VALUES (@Id, 'custom_alias');", new { Id = customId });
        using var http = new HttpClient(new StubHandler(uri => uri.AbsolutePath switch
        {
            "/tags.json" when !uri.Query.Contains("page=") => """[{"id":50,"name":"custom_name","category":0},{"id":40,"name":"legacy_name","category":0},{"id":30,"name":"custom_alias","category":0},{"id":20,"name":"safe_target","category":5}]""",
            "/tag_aliases.json" when !uri.Query.Contains("page=") => """[{"id":90,"antecedent_name":"custom_name","consequent_name":"safe_target","status":"active"},{"id":80,"antecedent_name":"custom_alias","consequent_name":"safe_target","status":"active"}]""",
            _ => "[]"
        }));
        var result = await new DanbooruTagImporter(database.ConnectionString, http).RunAsync(
            new DanbooruImportOptions { UserAgent = "nodeck-tests/1.0", PageSize = 10 });

        Assert.Equal(5, result.Conflicts);
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tags WHERE name = 'custom_name' AND origin = 'user' AND danbooru_id IS NULL;"));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tags WHERE name = 'legacy_name' AND origin = 'legacy' AND danbooru_id IS NULL;"));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases WHERE alias = 'custom_alias' AND origin = 'user' AND danbooru_id IS NULL;"));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tags WHERE name = 'safe_target' AND category = 'meta' AND origin = 'danbooru';"));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases WHERE origin = 'danbooru';"));
    }

    [DatabaseFact]
    public async Task FailedPageDoesNotAdvanceCursorOrKeepPartialRows()
    {
        await using var database = await ImportDatabase.CreateAsync();
        using var http = new HttpClient(new StubHandler(uri => uri.Query.Contains("page=b30")
            ? """[{"id":20,"name":"partial_tag","category":0},{"id":30,"name":"invalid_cursor","category":0}]"""
            : """[{"id":30,"name":"first_tag","category":0}]"""));
        var importer = new DanbooruTagImporter(database.ConnectionString, http);
        var options = new DanbooruImportOptions { UserAgent = "nodeck-tests/1.0", PageSize = 2, MaxPages = 1 };
        await importer.RunAsync(options);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => importer.RunAsync(options));

        Assert.Equal(30, await database.ScalarAsync<long>("SELECT cursor_before_id FROM danbooru_import_state WHERE kind = 'tags';"));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tags WHERE origin = 'danbooru';"));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tags WHERE name = 'partial_tag';"));
    }

    [DatabaseFact]
    public async Task RefreshUpdatesImportedCategoryWithoutChangingManualAssignment()
    {
        await using var database = await ImportDatabase.CreateAsync();
        var refreshed = false;
        using var http = new HttpClient(new StubHandler(uri => uri.AbsolutePath == "/tags.json" && !uri.Query.Contains("page=")
            ? refreshed
                ? """[{"id":10,"name":"assigned_tag","category":1}]"""
                : """[{"id":10,"name":"assigned_tag","category":0}]"""
            : "[]"));
        var importer = new DanbooruTagImporter(database.ConnectionString, http);
        var options = new DanbooruImportOptions { UserAgent = "nodeck-tests/1.0", PageSize = 2 };
        await importer.RunAsync(options);
        var tagId = await database.ScalarAsync<long>("SELECT id FROM tags WHERE danbooru_id = 10;");
        var cardId = await database.ScalarAsync<long>("""
            INSERT INTO cards (type, title, metadata)
            VALUES ('media', 'assigned-card', JSON_OBJECT()); SELECT LAST_INSERT_ID();
            """);
        await database.ExecuteAsync("""
            INSERT INTO card_tags (card_id, tag_id, assignment_source)
            VALUES (@CardId, @TagId, 'manual');
            """, new { CardId = cardId, TagId = tagId });

        refreshed = true;
        var result = await importer.RunAsync(new DanbooruImportOptions
        {
            UserAgent = options.UserAgent, PageSize = 2, Refresh = true
        });

        Assert.True(result.TagsCompleted);
        Assert.Equal(tagId, await database.ScalarAsync<long>("SELECT id FROM tags WHERE danbooru_id = 10 AND category = 'artist';"));
        Assert.Equal(1, await database.ScalarAsync<int>("""
            SELECT COUNT(*) FROM card_tags
            WHERE card_id = @CardId AND tag_id = @TagId AND assignment_source = 'manual';
            """, new { CardId = cardId, TagId = tagId }));
    }

    [DatabaseFact]
    public async Task DeprecatedImportedTagKeepsUserAlias()
    {
        await using var database = await ImportDatabase.CreateAsync();
        var deprecated = false;
        using var http = new HttpClient(new StubHandler(uri => uri.AbsolutePath == "/tags.json" && !uri.Query.Contains("page=")
            ? deprecated
                ? """[{"id":20,"name":"aliased_tag","category":0,"is_deprecated":true}]"""
                : """[{"id":20,"name":"aliased_tag","category":0}]"""
            : "[]"));
        var importer = new DanbooruTagImporter(database.ConnectionString, http);
        var options = new DanbooruImportOptions { UserAgent = "nodeck-tests/1.0", PageSize = 2 };
        await importer.RunAsync(options);
        var tagId = await database.ScalarAsync<long>("SELECT id FROM tags WHERE danbooru_id = 20;");
        await database.ExecuteAsync("""
            INSERT INTO tag_aliases (tag_id, alias, origin)
            VALUES (@TagId, 'my_alias', 'user');
            """, new { TagId = tagId });

        deprecated = true;
        await importer.RunAsync(new DanbooruImportOptions
        {
            UserAgent = options.UserAgent, PageSize = 2, Refresh = true
        });

        Assert.Equal(1, await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM tags WHERE id = @TagId AND origin = 'danbooru';", new { TagId = tagId }));
        Assert.Equal(1, await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM tag_aliases WHERE tag_id = @TagId AND alias = 'my_alias' AND origin = 'user';",
            new { TagId = tagId }));
    }

    [DatabaseFact]
    public async Task LimitedRefreshResumesAliasesOnNextNormalRun()
    {
        await using var database = await ImportDatabase.CreateAsync();
        var refreshed = false;
        using var http = new HttpClient(new StubHandler(uri => uri.AbsolutePath switch
        {
            "/tags.json" when !uri.Query.Contains("page=") =>
                """[{"id":10,"name":"alias_target","category":0}]""",
            "/tag_aliases.json" when !uri.Query.Contains("page=") => refreshed
                ? """[{"id":30,"antecedent_name":"new_alias","consequent_name":"alias_target","status":"active"}]"""
                : """[{"id":30,"antecedent_name":"old_alias","consequent_name":"alias_target","status":"active"}]""",
            _ => "[]"
        }));
        var importer = new DanbooruTagImporter(database.ConnectionString, http);
        var options = new DanbooruImportOptions { UserAgent = "nodeck-tests/1.0", PageSize = 2 };
        var initial = await importer.RunAsync(options);
        Assert.True(initial.TagsCompleted);
        Assert.True(initial.AliasesCompleted);
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases WHERE alias = 'old_alias';"));

        refreshed = true;
        var limited = await importer.RunAsync(new DanbooruImportOptions
        {
            UserAgent = options.UserAgent, PageSize = 2, Refresh = true, MaxPages = 1
        });
        Assert.Equal(1, limited.Pages);
        Assert.False(limited.AliasesCompleted);

        var resumed = await importer.RunAsync(options);
        Assert.True(resumed.AliasesCompleted);
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases WHERE danbooru_id = 30 AND alias = 'new_alias';"));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM tag_aliases WHERE alias = 'old_alias';"));
    }

    private sealed class StubHandler(Func<Uri, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("nodeck-tests/1.0", request.Headers.UserAgent.ToString());
            var json = respond(request.RequestUri!)
                ?? throw new InvalidOperationException("Stub response JSON is missing.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ImportDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _name;
        public string ConnectionString { get; }

        private ImportDatabase(string adminConnectionString, string name, string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _name = name;
            ConnectionString = connectionString;
        }

        public static async Task<ImportDatabase> CreateAsync()
        {
            var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!);
            if (!builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Importer tests require a dedicated test database.");
            var name = $"nodeck_import_{Guid.NewGuid():N}_test";
            builder.Database = "";
            var adminConnectionString = builder.ConnectionString;
            await using (var admin = new MySqlConnection(adminConnectionString))
            {
                await admin.OpenAsync();
                await admin.ExecuteAsync($"CREATE DATABASE `{name}`;");
            }
            builder.Database = name;
            var database = new ImportDatabase(adminConnectionString, name, builder.ConnectionString);
            try
            {
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "database/init/001_cards.sql")))
                    root = root.Parent;
                Assert.NotNull(root);
                await using var connection = new MySqlConnection(database.ConnectionString);
                await connection.OpenAsync();
                var sql = await File.ReadAllTextAsync(Path.Combine(root.FullName, "database/init/001_cards.sql"));
                foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    await connection.ExecuteAsync(statement);
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = database.ConnectionString
                }).Build();
                await new SchemaMigrationRunner(configuration, NullLogger<SchemaMigrationRunner>.Instance,
                    new CardDatabaseInitializer(configuration), new DeckDatabaseInitializer(configuration),
                    new CatalogDatabaseInitializer(configuration, NullLogger<CatalogDatabaseInitializer>.Instance),
                    new InterestDatabaseInitializer(configuration)).InitializeAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
        {
            await using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync();
            return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
        }

        public async Task<IEnumerable<T>> QueryAsync<T>(string sql)
        {
            await using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync();
            return await connection.QueryAsync<T>(sql);
        }

        public async Task ExecuteAsync(string sql, object? parameters = null)
        {
            await using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(sql, parameters);
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new MySqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE `{_name}`;");
        }
    }
}

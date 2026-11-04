using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyApp.Api.Cards;
using MyApp.Api.Catalog;
using MyApp.Api.Decks;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class CatalogMigrationTests
{
    [DatabaseFact]
    public async Task LegacyCatalogCopiesEveryIdentityAndLinkWithoutChangingLegacyData()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString })
            .Build();
        await EnsureSchemaAsync(connectionString, configuration);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        var cardIds = new List<long>();
        try
        {
            var mediaId = await InsertCardAsync(connection, "media", $"media-{token}");
            var firstTagId = await InsertCardAsync(connection, "tag", $"same-{token}");
            var secondTagId = await InsertCardAsync(connection, "tag", $"same-{token}");
            var firstSourceId = await InsertCardAsync(connection, "source", $"source-{token}");
            var secondSourceId = await InsertCardAsync(connection, "source", $"source-{token}");
            cardIds.AddRange([mediaId, firstTagId, secondTagId, firstSourceId, secondSourceId]);

            var firstTagLink = await InsertRelationAsync(connection, mediaId, firstTagId, "tagged_with");
            var secondTagLink = await InsertRelationAsync(connection, mediaId, secondTagId, "tagged_with");
            var originalUrl = $"https://example.test/{token}/original";
            var sourceLink = await InsertRelationAsync(connection, mediaId, firstSourceId, "sourced_from",
                new { original_url = originalUrl });
            var secondUrl = $"https://example.test/{token}/another";
            var secondSourceLink = await InsertRelationAsync(connection, mediaId, secondSourceId, "sourced_from",
                new { original_url = secondUrl });
            var specialLink = await InsertRelationAsync(connection, firstTagId, firstSourceId, "related_to");

            var initializer = new CatalogDatabaseInitializer(configuration,
                NullLogger<CatalogDatabaseInitializer>.Instance);
            await initializer.InitializeAsync();
            await initializer.InitializeAsync();

            var tags = (await connection.QueryAsync<(long Id, string Name, string CanonicalKey, bool NameConflict)>(
                "SELECT id, name, canonical_key, name_conflict FROM tags WHERE legacy_card_id IN @Ids ORDER BY legacy_card_id;",
                new { Ids = new[] { firstTagId, secondTagId } })).ToArray();
            Assert.Equal(2, tags.Length);
            Assert.All(tags, tag => Assert.True(tag.NameConflict));
            Assert.Equal(tags[0].Name, tags[1].Name);
            Assert.NotEqual(tags[0].CanonicalKey, tags[1].CanonicalKey);

            var sources = (await connection.QueryAsync<(long Id, string Name, string CanonicalKey, bool NameConflict)>(
                "SELECT id, name, canonical_key, name_conflict FROM sources WHERE legacy_card_id IN @Ids ORDER BY legacy_card_id;",
                new { Ids = new[] { firstSourceId, secondSourceId } })).ToArray();
            Assert.Equal(2, sources.Length);
            Assert.All(sources, source => Assert.True(source.NameConflict));
            Assert.Equal(sources[0].Name, sources[1].Name);
            Assert.NotEqual(sources[0].CanonicalKey, sources[1].CanonicalKey);
            var copiedTagLinks = (await connection.QueryAsync<long>(
                "SELECT legacy_relation_id FROM card_tags WHERE legacy_relation_id IN @Ids ORDER BY legacy_relation_id;",
                new { Ids = new[] { firstTagLink, secondTagLink } })).ToArray();
            Assert.Equal(new[] { firstTagLink, secondTagLink }, copiedTagLinks);
            var copiedSource = await connection.QuerySingleAsync<(long LegacyRelationId, string OriginalUrl)>(
                "SELECT legacy_relation_id, original_url FROM card_sources WHERE legacy_relation_id = @Id;",
                new { Id = sourceLink });
            Assert.Equal(sourceLink, copiedSource.LegacyRelationId);
            Assert.Equal(originalUrl, copiedSource.OriginalUrl);
            Assert.Equal(secondUrl, await connection.QuerySingleAsync<string>(
                "SELECT original_url FROM card_sources WHERE legacy_relation_id = @Id;",
                new { Id = secondSourceLink }));

            Assert.Equal(5, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM card_relations WHERE id IN @Ids;",
                new { Ids = new[] { firstTagLink, secondTagLink, sourceLink, secondSourceLink, specialLink } }));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM card_relations WHERE id = @Id AND relation_type = 'related_to';",
                new { Id = specialLink }));

            var changedUrl = $"https://example.test/{token}/updated";
            await connection.ExecuteAsync(
                "UPDATE card_relations SET properties = JSON_OBJECT('original_url', @Url) WHERE id = @Id;",
                new { Url = changedUrl, Id = sourceLink });
            await initializer.InitializeAsync();
            Assert.Equal(changedUrl, await connection.QuerySingleAsync<string>(
                "SELECT original_url FROM card_sources WHERE legacy_relation_id = @Id;",
                new { Id = sourceLink }));
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM card_tags WHERE legacy_relation_id IN @Ids;",
                new { Ids = new[] { firstTagLink, secondTagLink } }));
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM card_sources WHERE legacy_relation_id IN @Ids;",
                new { Ids = new[] { sourceLink, secondSourceLink } }));
        }
        finally
        {
            if (cardIds.Count > 0)
                await DeleteSeededCardsAsync(connection, cardIds);
        }
    }

    [DatabaseFact]
    public async Task DeletingLegacyCatalogCardsRemovesTheirCopiesOnNextRun()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString })
            .Build();
        await EnsureSchemaAsync(connectionString, configuration);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        var cardIds = new List<long>();
        try
        {
            var mediaId = await InsertCardAsync(connection, "media", $"media-{token}");
            var tagId = await InsertCardAsync(connection, "tag", $"tag-{token}");
            var sourceId = await InsertCardAsync(connection, "source", $"source-{token}");
            cardIds.AddRange([mediaId, tagId, sourceId]);
            var tagLink = await InsertRelationAsync(connection, mediaId, tagId, "tagged_with");
            var sourceLink = await InsertRelationAsync(connection, mediaId, sourceId, "sourced_from");
            var initializer = new CatalogDatabaseInitializer(configuration,
                NullLogger<CatalogDatabaseInitializer>.Instance);
            await initializer.InitializeAsync();

            await connection.ExecuteAsync("DELETE FROM cards WHERE id IN @Ids;", new { Ids = new[] { tagId, sourceId } });
            await initializer.InitializeAsync();

            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM tags WHERE legacy_card_id = @Id;", new { Id = tagId }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sources WHERE legacy_card_id = @Id;", new { Id = sourceId }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM card_tags WHERE legacy_relation_id = @Id;", new { Id = tagLink }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM card_sources WHERE legacy_relation_id = @Id;", new { Id = sourceLink }));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM cards WHERE id = @Id;", new { Id = mediaId }));
        }
        finally
        {
            if (cardIds.Count > 0)
                await DeleteSeededCardsAsync(connection, cardIds);
        }
    }

    private static async Task EnsureSchemaAsync(string connectionString, IConfiguration configuration)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "database/init/001_cards.sql")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        foreach (var file in new[] { "001_cards.sql", "002_catalog.sql" })
        {
            var sql = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "database/init", file));
            foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await connection.ExecuteAsync(statement);
        }
        await new CardDatabaseInitializer(configuration).InitializeAsync();
        await new DeckDatabaseInitializer(configuration).InitializeAsync();
    }

    private static Task<long> InsertCardAsync(MySqlConnection connection, string type, string title) =>
        connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO cards (type, title, metadata)
            VALUES (@Type, @Title, JSON_OBJECT('created_at', '2026-11-04T00:00:00Z'));
            SELECT LAST_INSERT_ID();
            """, new { Type = type, Title = title });

    private static Task<long> InsertRelationAsync(
        MySqlConnection connection, long fromId, long toId, string type, object? properties = null) =>
        connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO card_relations (from_card_id, to_card_id, relation_type, properties, metadata)
            VALUES (@FromId, @ToId, @Type, @Properties, JSON_OBJECT('created_at', '2026-11-04T00:00:00Z'));
            SELECT LAST_INSERT_ID();
            """, new { FromId = fromId, ToId = toId, Type = type,
                Properties = properties is null ? null : System.Text.Json.JsonSerializer.Serialize(properties) });

    private static async Task DeleteSeededCardsAsync(MySqlConnection connection, List<long> cardIds)
    {
        await connection.ExecuteAsync("DELETE FROM tags WHERE legacy_card_id IN @Ids;", new { Ids = cardIds });
        await connection.ExecuteAsync("DELETE FROM sources WHERE legacy_card_id IN @Ids;", new { Ids = cardIds });
        await connection.ExecuteAsync("DELETE FROM cards WHERE id IN @Ids;", new { Ids = cardIds });
    }
}

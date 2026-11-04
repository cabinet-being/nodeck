using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyApp.Api.Cards;
using MyApp.Api.Catalog;
using MyApp.Api.Decks;
using MySqlConnector;

namespace MyApp.Api.Tests;

[Collection("Collection database")]
public sealed class CatalogApiTests
{
    [DatabaseFact]
    public async Task CardAssignmentsBridgeLegacyRelationsAndNewCatalogEntries()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = Configuration(connectionString);
        await EnsureSchemaAsync(connectionString, configuration);
        var catalog = new CatalogRepository(configuration);
        var assignments = new CardCatalogRepository(configuration);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        var cardId = await InsertCardAsync(connection, "media", $"media-{token}");
        var legacyTagCardId = await InsertCardAsync(connection, "tag", $"legacy-tag-{token}");
        var legacySourceCardId = await InsertCardAsync(connection, "source", $"legacy-source-{token}");
        long customTagId = 0;
        long customSourceId = 0;
        try
        {
            await new CatalogDatabaseInitializer(configuration, NullLogger<CatalogDatabaseInitializer>.Instance)
                .InitializeAsync();
            var legacyTagId = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM tags WHERE legacy_card_id = @Id;", new { Id = legacyTagCardId });
            var legacySourceId = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM sources WHERE legacy_card_id = @Id;", new { Id = legacySourceCardId });
            Assert.True((await catalog.GetSourceAsync(legacySourceId))!.IsLegacy);
            customTagId = (await catalog.CreateTagAsync(new TagRequest($"custom-{token}", null))).Id;
            customSourceId = (await catalog.CreateSourceAsync(new SourceRequest($"custom-source-{token}"))).Id;
            Assert.False((await catalog.GetSourceAsync(customSourceId))!.IsLegacy);

            await assignments.SetTagAsync(cardId, legacyTagId);
            await assignments.SetTagAsync(cardId, legacyTagId);
            await assignments.SetTagAsync(cardId, customTagId);
            Assert.Equal(2, (await assignments.ListTagsAsync(cardId)).Count);
            Assert.Equal(1, (await catalog.GetTagAsync(legacyTagId))!.ReferenceCount);
            Assert.Equal(1, await connection.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM card_relations WHERE from_card_id = @CardId
                    AND to_card_id = @TagCardId AND relation_type = 'tagged_with';
                """, new { CardId = cardId, TagCardId = legacyTagCardId }));

            var url = $"https://example.test/{token}/original";
            await assignments.SetSourceAsync(cardId, legacySourceId, new CardSourceRequest(url));
            await assignments.SetSourceAsync(cardId, customSourceId,
                new CardSourceRequest($"https://example.test/{token}/other"));
            Assert.Equal(2, (await assignments.ListSourcesAsync(cardId)).Count);
            Assert.Equal(url, (await assignments.ListSourcesAsync(cardId))
                .Single(source => source.Id == legacySourceId).OriginalUrl);
            Assert.Equal(1, (await catalog.GetSourceAsync(legacySourceId))!.ReferenceCount);
            Assert.Equal(1, await connection.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM card_relations WHERE from_card_id = @CardId
                    AND to_card_id = @SourceCardId AND relation_type = 'sourced_from';
                """, new { CardId = cardId, SourceCardId = legacySourceCardId }));
            await Assert.ThrowsAsync<CatalogConflictException>(() =>
                catalog.UpdateSourceAsync(legacySourceId, new SourceRequest($"changed-source-{token}")));
            var updatedUrl = $"https://example.test/{token}/changed";
            await assignments.SetSourceAsync(cardId, legacySourceId, new CardSourceRequest(updatedUrl));
            Assert.Equal(updatedUrl, (await assignments.ListSourcesAsync(cardId))
                .Single(source => source.Id == legacySourceId).OriginalUrl);

            Assert.True(await assignments.DeleteTagAsync(cardId, legacyTagId));
            Assert.True(await assignments.DeleteSourceAsync(cardId, legacySourceId));
            Assert.Equal(0, (await catalog.GetTagAsync(legacyTagId))!.ReferenceCount);
            Assert.Equal(0, (await catalog.GetSourceAsync(legacySourceId))!.ReferenceCount);
            Assert.Single(await assignments.ListTagsAsync(cardId));
            Assert.Single(await assignments.ListSourcesAsync(cardId));
            await connection.ExecuteAsync("""
                INSERT INTO card_relations (from_card_id, to_card_id, relation_type, metadata)
                VALUES (@CardId, @TagCardId, 'tagged_with', JSON_OBJECT());
                INSERT INTO card_relations (from_card_id, to_card_id, relation_type, properties, metadata)
                VALUES (@CardId, @SourceCardId, 'sourced_from', JSON_OBJECT('original_url', @Url), JSON_OBJECT());
                """, new { CardId = cardId, TagCardId = legacyTagCardId,
                    SourceCardId = legacySourceCardId, Url = url });
            Assert.Equal(2, (await assignments.ListTagsAsync(cardId)).Count);
            Assert.Equal(2, (await assignments.ListSourcesAsync(cardId)).Count);
            Assert.Equal(1, (await catalog.GetTagAsync(legacyTagId))!.ReferenceCount);
            Assert.Equal(1, (await catalog.GetSourceAsync(legacySourceId))!.ReferenceCount);
            await Assert.ThrowsAsync<CatalogValidationException>(() => assignments.SetSourceAsync(
                cardId, customSourceId, new CardSourceRequest("javascript:invalid")));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM tags WHERE legacy_card_id = @Id;",
                new { Id = legacyTagCardId });
            await connection.ExecuteAsync("DELETE FROM sources WHERE legacy_card_id = @Id;",
                new { Id = legacySourceCardId });
            await connection.ExecuteAsync("DELETE FROM cards WHERE id IN @Ids;",
                new { Ids = new[] { cardId, legacyTagCardId, legacySourceCardId } });
            if (customTagId != 0)
                await connection.ExecuteAsync("DELETE FROM tags WHERE id = @Id;", new { Id = customTagId });
            if (customSourceId != 0)
                await connection.ExecuteAsync("DELETE FROM sources WHERE id = @Id;", new { Id = customSourceId });
        }
    }

    [DatabaseFact]
    public async Task CustomTagNamesAliasesAndProtectedLegacyRowsRemainUnambiguous()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = Configuration(connectionString);
        await EnsureSchemaAsync(connectionString, configuration);
        var repository = new CatalogRepository(configuration);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        var name = $"tag-{token}";
        var alias = $"alias-{token}";
        long tagId = 0;
        long legacyCardId = 0;
        try
        {
            var created = await repository.CreateTagAsync(new TagRequest(name, [alias]));
            tagId = created.Id;
            Assert.Equal("custom", created.Category);
            Assert.Equal("user", created.Origin);
            Assert.Equal(new[] { alias }, created.Aliases);
            Assert.Equal(tagId, Assert.Single(await repository.ListTagsAsync(alias)).Id);

            await Assert.ThrowsAsync<CatalogConflictException>(() =>
                repository.CreateTagAsync(new TagRequest(alias.ToUpperInvariant(), null)));
            await Assert.ThrowsAsync<CatalogConflictException>(() =>
                repository.CreateTagAsync(new TagRequest($"second-{token}", [name])));

            var renamed = await repository.UpdateTagAsync(tagId,
                new TagRequest($"renamed-{token}", [$"fresh-{token}"]));
            Assert.Equal($"renamed-{token}", renamed!.Name);
            Assert.Empty(await repository.ListTagsAsync(alias));
            Assert.Equal(tagId, Assert.Single(await repository.ListTagsAsync($"fresh-{token}")).Id);

            legacyCardId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO cards (type, title, metadata) VALUES ('tag', @Name, JSON_OBJECT());
                SELECT LAST_INSERT_ID();
                """, new { Name = $"legacy-{token}" });
            await new CatalogDatabaseInitializer(configuration, NullLogger<CatalogDatabaseInitializer>.Instance)
                .InitializeAsync();
            var legacyTagId = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM tags WHERE legacy_card_id = @Id;", new { Id = legacyCardId });
            await Assert.ThrowsAsync<CatalogConflictException>(() =>
                repository.UpdateTagAsync(legacyTagId, new TagRequest($"changed-{token}", null)));
            await Assert.ThrowsAsync<CatalogConflictException>(() => repository.DeleteTagAsync(legacyTagId));
            await Assert.ThrowsAsync<CatalogConflictException>(() =>
                repository.CreateTagAsync(new TagRequest($"legacy-{token}", null)));

            Assert.True(await repository.DeleteTagAsync(tagId));
            tagId = 0;
            Assert.Null(await repository.GetTagAsync(created.Id));
        }
        finally
        {
            if (tagId != 0)
                await connection.ExecuteAsync("DELETE FROM tags WHERE id = @Id;", new { Id = tagId });
            if (legacyCardId != 0)
            {
                await connection.ExecuteAsync("DELETE FROM tags WHERE legacy_card_id = @Id;", new { Id = legacyCardId });
                await connection.ExecuteAsync("DELETE FROM cards WHERE id = @Id;", new { Id = legacyCardId });
            }
        }
    }

    [DatabaseFact]
    public async Task SourceNameIsCatalogDataWhileOriginalUrlBelongsToEachCardLink()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = Configuration(connectionString);
        await EnsureSchemaAsync(connectionString, configuration);
        var repository = new CatalogRepository(configuration);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        long sourceId = 0;
        long cardId = 0;
        try
        {
            var source = await repository.CreateSourceAsync(new SourceRequest($"source-{token}"));
            sourceId = source.Id;
            await Assert.ThrowsAsync<CatalogConflictException>(() =>
                repository.CreateSourceAsync(new SourceRequest(source.Name.ToUpperInvariant())));
            cardId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO cards (type, title, metadata) VALUES ('media', @Name, JSON_OBJECT());
                SELECT LAST_INSERT_ID();
                """, new { Name = $"media-{token}" });
            var url = $"https://example.test/{token}";
            await connection.ExecuteAsync("""
                INSERT INTO card_sources (card_id, source_id, original_url)
                VALUES (@CardId, @SourceId, @Url);
                """, new { CardId = cardId, SourceId = sourceId, Url = url });
            Assert.Equal(1, (await repository.GetSourceAsync(sourceId))!.ReferenceCount);
            Assert.Equal(url, await connection.ExecuteScalarAsync<string>(
                "SELECT original_url FROM card_sources WHERE card_id = @Id;", new { Id = cardId }));
            await Assert.ThrowsAsync<CatalogConflictException>(() => repository.DeleteSourceAsync(sourceId));
            await connection.ExecuteAsync("DELETE FROM card_sources WHERE card_id = @Id;", new { Id = cardId });
            Assert.True(await repository.DeleteSourceAsync(sourceId));
            sourceId = 0;
        }
        finally
        {
            if (cardId != 0)
                await connection.ExecuteAsync("DELETE FROM cards WHERE id = @Id;", new { Id = cardId });
            if (sourceId != 0)
                await connection.ExecuteAsync("DELETE FROM sources WHERE id = @Id;", new { Id = sourceId });
        }
    }

    private static IConfiguration Configuration(string connectionString) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString })
        .Build();

    private static Task<long> InsertCardAsync(MySqlConnection connection, string type, string title) =>
        connection.ExecuteScalarAsync<long>("""
            INSERT INTO cards (type, title, metadata) VALUES (@Type, @Title, JSON_OBJECT());
            SELECT LAST_INSERT_ID();
            """, new { Type = type, Title = title });

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
        await new CatalogDatabaseInitializer(configuration, NullLogger<CatalogDatabaseInitializer>.Instance)
            .InitializeAsync();
    }
}

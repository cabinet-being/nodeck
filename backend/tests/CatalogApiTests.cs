using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task CardSearchFindsAssignedTagNamesAndAliasesAcrossPages()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = Configuration(connectionString);
        await EnsureSchemaAsync(connectionString, configuration);
        var cards = new CardRepository(configuration);
        var catalog = new CatalogRepository(configuration);
        var assignments = new CardCatalogRepository(configuration);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        var firstId = await InsertCardAsync(connection, "media", $"first-{token}");
        var secondId = await InsertCardAsync(connection, "media", $"second-{token}");
        var groupId = await InsertCardAsync(connection, "set", $"group-{token}");
        long tagId = 0;
        try
        {
            tagId = (await catalog.CreateTagAsync(new TagRequest($"tag-{token}",
                [$"alias-{token}"]))).Id;
            await assignments.SetTagAsync(firstId, tagId);
            await connection.ExecuteAsync("""
                INSERT INTO card_relations (from_card_id, to_card_id, relation_type, metadata)
                VALUES (@GroupId, @FirstId, 'contains', JSON_OBJECT());
                """, new { GroupId = groupId, FirstId = firstId });

            async Task<IReadOnlyList<CardSummary>> List(string search, int limit = 200, int offset = 0) =>
                await cards.ListAsync(["media"], null, search, false, [], null, [], null,
                    "created_at", "asc", limit, offset);

            Assert.Equal(firstId, Assert.Single(await List($"tag-{token}")).Id);
            Assert.Equal(firstId, Assert.Single(await List($"alias-{token}")).Id);
            Assert.Empty(await List($"tag%{token}"));
            Assert.Equal(groupId, Assert.Single(await cards.ListAsync(["set"], null, $"tag-{token}",
                true, [], null, [], null, "created_at", "desc")).Id);
            Assert.Equal(groupId, Assert.Single(await cards.ListAsync(["set"], null, null,
                true, [], null, [tagId], null, "created_at", "desc")).Id);
            Assert.Equal(firstId, Assert.Single(await List(token, 1)).Id);
            Assert.Equal(secondId, Assert.Single(await List(token, 1, 1)).Id);
            Assert.Empty(await List(token, 1, 2));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM cards WHERE id IN @Ids;",
                new { Ids = new[] { firstId, secondId, groupId } });
            if (tagId != 0)
                await connection.ExecuteAsync("DELETE FROM tags WHERE id = @Id;", new { Id = tagId });
        }
    }

    [DatabaseFact]
    public async Task CardAssignmentsBridgeLegacyRelationsAndNewCatalogEntries()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = Configuration(connectionString);
        await EnsureSchemaAsync(connectionString, configuration);
        var catalog = new CatalogRepository(configuration);
        var assignments = new CardCatalogRepository(configuration);
        var cards = new CardRepository(configuration);
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
            Assert.All(await assignments.ListTagsAsync(cardId),
                tag => Assert.Equal("manual", tag.AssignmentSource));
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
            Assert.Contains(await cards.ListAsync(["media"], null, null, false, [], null,
                [legacyTagId], legacySourceId, "created_at", "desc"), item => item.Id == cardId);
            Assert.Contains(await cards.ListAsync(["media"], null, null, false, [], null,
                [customTagId], customSourceId, "created_at", "desc"), item => item.Id == cardId);
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
            Assert.DoesNotContain(await cards.ListAsync(["media"], null, null, false, [], null,
                [legacyTagId], null, "created_at", "desc"), item => item.Id == cardId);
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
    public async Task TagSearchMatchesNameAndAliasPrefixesWithLiteralWildcards()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = Configuration(connectionString);
        await EnsureSchemaAsync(connectionString, configuration);
        var repository = new CatalogRepository(configuration);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        var prefix = $"literal!_%\\{token}";
        var nameId = 0L;
        var aliasId = 0L;
        var infixId = 0L;
        var wildcardId = 0L;
        try
        {
            nameId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO tags (name, canonical_key, category, origin)
                VALUES (@Name, @Key, 'general', 'danbooru'); SELECT LAST_INSERT_ID();
                """, new { Name = prefix + "-name", Key = "search-test-name:" + token });
            aliasId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO tags (name, canonical_key, category, origin)
                VALUES (@Name, @Key, 'character', 'danbooru'); SELECT LAST_INSERT_ID();
                """, new { Name = "alias-target-" + token, Key = "search-test-alias:" + token });
            await connection.ExecuteAsync("INSERT INTO tag_aliases (tag_id, alias) VALUES (@TagId, @Alias);",
                new { TagId = aliasId, Alias = prefix + "-alias" });
            infixId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO tags (name, canonical_key, category, origin)
                VALUES (@Name, @Key, 'general', 'danbooru'); SELECT LAST_INSERT_ID();
                """, new { Name = "inside-" + prefix, Key = "search-test-infix:" + token });
            wildcardId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO tags (name, canonical_key, category, origin)
                VALUES (@Name, @Key, 'general', 'danbooru'); SELECT LAST_INSERT_ID();
                """, new { Name = $"literal!XQZ{token}-decoy", Key = "search-test-decoy:" + token });

            var matches = await repository.ListTagsAsync(prefix.ToUpperInvariant());
            Assert.Equal(new[] { aliasId, nameId }, matches.Select(tag => tag.Id).ToArray());
            Assert.DoesNotContain(matches, tag => tag.Id == infixId || tag.Id == wildcardId);
            Assert.Equal(aliasId, Assert.Single(await repository.ListTagsAsync(prefix + "-alias")).Id);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM tags WHERE id IN @Ids;",
                new { Ids = new[] { nameId, aliasId, infixId, wildcardId }.Where(id => id > 0).ToArray() });
        }
    }

    [DatabaseFact]
    public async Task TagSearchReturnsFirstTwoHundredMatchingNamesInOrder()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = Configuration(connectionString);
        await EnsureSchemaAsync(connectionString, configuration);
        var repository = new CatalogRepository(configuration);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        var prefix = "cap" + token;
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO tags (name, canonical_key, category, origin)
                VALUES (@Name, @Key, 'general', 'danbooru');
                """, Enumerable.Range(0, 205).Select(index => new
                {
                    Name = $"{prefix}{index:000}",
                    Key = $"search-test-cap:{token}:{index:000}"
                }));

            var matches = await repository.ListTagsAsync(prefix);
            Assert.Equal(200, matches.Count);
            Assert.Equal(Enumerable.Range(0, 200).Select(index => $"{prefix}{index:000}"),
                matches.Select(tag => tag.Name));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM tags WHERE canonical_key LIKE @Prefix;",
                new { Prefix = $"search-test-cap:{token}:%" });
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

    [DatabaseFact]
    public async Task AutomaticTagsCanBeReviewedAndReanalysisPreservesDecisions()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = Configuration(connectionString);
        await EnsureSchemaAsync(connectionString, configuration);
        var catalog = new CatalogRepository(configuration);
        var assignments = new CardCatalogRepository(configuration);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        var cardId = await InsertCardAsync(connection, "media", $"suggested-{token}");
        var manualId = 0L;
        var acceptId = 0L;
        var rejectId = 0L;
        try
        {
            manualId = (await catalog.CreateTagAsync(new TagRequest($"manual-{token}", null))).Id;
            acceptId = (await catalog.CreateTagAsync(new TagRequest($"accept-{token}", null))).Id;
            rejectId = (await catalog.CreateTagAsync(new TagRequest($"reject-{token}", null))).Id;
            await assignments.SetTagAsync(cardId, manualId);

            await assignments.UpsertSuggestionsAsync(cardId,
            [
                new TagSuggestionProposal(manualId, 0.99m, "test-model:v1"),
                new TagSuggestionProposal(acceptId, 0.91m, "test-model:v1"),
                new TagSuggestionProposal(rejectId, 0.87m, "test-model:v1")
            ]);
            Assert.Equal(new[] { acceptId, rejectId },
                (await assignments.ListPendingSuggestionsAsync(cardId))
                    .Select(item => item.TagId).Order().ToArray());
            Assert.Equal(new[] { manualId, acceptId, rejectId }.Order(),
                (await assignments.ListTagsAsync(cardId)).Select(item => item.Id).Order());
            Assert.All((await assignments.ListTagsAsync(cardId)).Where(item => item.Id != manualId),
                item => Assert.Equal("auto", item.AssignmentSource));
            await assignments.UpsertSuggestionsAsync(cardId,
                [new TagSuggestionProposal(acceptId, 0.92m, "test-model:v2")]);
            Assert.Equal(0.92m, (await assignments.ListPendingSuggestionsAsync(cardId))
                .Single(item => item.TagId == acceptId).Confidence);

            Assert.True(await assignments.DecideSuggestionAsync(cardId, acceptId, "accepted"));
            Assert.True(await assignments.DecideSuggestionAsync(cardId, acceptId, "accepted"));
            Assert.True(await assignments.DecideSuggestionAsync(cardId, rejectId, "rejected"));
            Assert.True(await assignments.DecideSuggestionAsync(cardId, rejectId, "rejected"));
            Assert.False(await assignments.DecideSuggestionAsync(cardId, manualId, "accepted"));
            Assert.Empty(await assignments.ListPendingSuggestionsAsync(cardId));
            Assert.Equal("confirmed", (await assignments.ListTagsAsync(cardId))
                .Single(item => item.Id == acceptId).AssignmentSource);
            Assert.Equal(0.92m, await connection.ExecuteScalarAsync<decimal>("""
                SELECT confidence FROM card_tags WHERE card_id = @CardId AND tag_id = @TagId;
                """, new { CardId = cardId, TagId = acceptId }));

            await assignments.UpsertSuggestionsAsync(cardId,
            [
                new TagSuggestionProposal(manualId, 0.01m, "test-model:v2"),
                new TagSuggestionProposal(acceptId, 0.01m, "test-model:v2"),
                new TagSuggestionProposal(rejectId, 0.99m, "test-model:v2")
            ]);
            Assert.Empty(await assignments.ListPendingSuggestionsAsync(cardId));
            Assert.Equal(0.92m, await connection.ExecuteScalarAsync<decimal>("""
                SELECT confidence FROM card_tags WHERE card_id = @CardId AND tag_id = @TagId;
                """, new { CardId = cardId, TagId = acceptId }));
            Assert.Equal("rejected", await connection.ExecuteScalarAsync<string>("""
                SELECT state FROM card_tag_suggestions WHERE card_id = @CardId AND tag_id = @TagId;
                """, new { CardId = cardId, TagId = rejectId }));
            await Assert.ThrowsAsync<CatalogConflictException>(() =>
                assignments.DecideSuggestionAsync(cardId, rejectId, "accepted"));
            await Assert.ThrowsAsync<CatalogValidationException>(() =>
                assignments.DecideSuggestionAsync(cardId, acceptId, "unknown"));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM cards WHERE id = @Id;", new { Id = cardId });
            foreach (var tagId in new[] { manualId, acceptId, rejectId }.Where(id => id > 0))
                await connection.ExecuteAsync("DELETE FROM tags WHERE id = @Id;", new { Id = tagId });
        }
    }

    [DatabaseFact]
    public async Task BatchReviewActivatesOldSuggestionsAndPreservesExplicitDecisions()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = Configuration(connectionString);
        await EnsureSchemaAsync(connectionString, configuration);
        var catalog = new CatalogRepository(configuration);
        var assignments = new CardCatalogRepository(configuration);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        var cardId = await InsertCardAsync(connection, "media", $"batch-review-{token}");
        var tagIds = new List<long>();
        try
        {
            for (var index = 0; index < 3; index++)
                tagIds.Add((await catalog.CreateTagAsync(new TagRequest($"batch-{index}-{token}", null))).Id);
            await assignments.UpsertSuggestionsAsync(cardId, tagIds.Select(id =>
                new TagSuggestionProposal(id, 0.8m, "test-model:v1")).ToArray());
            Assert.All(await assignments.ListTagsAsync(cardId),
                tag => Assert.Equal("auto", tag.AssignmentSource));

            // An older pending proposal with no assignment becomes visible on startup.
            await connection.ExecuteAsync("DELETE FROM card_tags WHERE card_id = @CardId AND tag_id = @TagId;",
                new { CardId = cardId, TagId = tagIds[0] });
            Assert.True(await assignments.ActivatePendingSuggestionsAsync() >= 1);
            Assert.Equal(3, (await assignments.ListTagsAsync(cardId)).Count);

            Assert.Equal(2, await assignments.DecideSuggestionsAsync(cardId, tagIds.Take(2).ToArray(), "accepted"));
            Assert.Equal(0, await assignments.DecideSuggestionsAsync(cardId, tagIds.Take(2).ToArray(), "accepted"));
            Assert.Equal(1, await assignments.DecideSuggestionsAsync(cardId, null, "rejected"));
            Assert.Empty(await assignments.ListPendingSuggestionsAsync(cardId));
            Assert.Equal(tagIds.Take(2).Order(),
                (await assignments.ListTagsAsync(cardId)).Select(tag => tag.Id).Order());
            Assert.All(await assignments.ListTagsAsync(cardId),
                tag => Assert.Equal("confirmed", tag.AssignmentSource));
            Assert.Equal("rejected", await connection.ExecuteScalarAsync<string>("""
                SELECT state FROM card_tag_suggestions WHERE card_id = @CardId AND tag_id = @TagId;
                """, new { CardId = cardId, TagId = tagIds[2] }));
            await Assert.ThrowsAsync<CatalogConflictException>(() =>
                assignments.DecideSuggestionsAsync(cardId, tagIds, "rejected"));
            Assert.Equal(2, (await assignments.ListTagsAsync(cardId)).Count);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM cards WHERE id = @Id;", new { Id = cardId });
            foreach (var tagId in tagIds)
                await connection.ExecuteAsync("DELETE FROM tags WHERE id = @Id;", new { Id = tagId });
        }
    }

    [DatabaseFact]
    public async Task ManualRemovalSuppressesFutureProposalsAndCardDeletionCascades()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = Configuration(connectionString);
        await EnsureSchemaAsync(connectionString, configuration);
        var catalog = new CatalogRepository(configuration);
        var assignments = new CardCatalogRepository(configuration);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        var cardId = await InsertCardAsync(connection, "media", $"manual-review-{token}");
        var tagId = 0L;
        var pendingId = 0L;
        try
        {
            tagId = (await catalog.CreateTagAsync(new TagRequest($"manual-review-{token}", null))).Id;
            pendingId = (await catalog.CreateTagAsync(new TagRequest($"pending-review-{token}", null))).Id;
            await assignments.SetTagAsync(cardId, tagId);
            Assert.True(await assignments.DeleteTagAsync(cardId, tagId));
            Assert.False(await assignments.DeleteTagAsync(cardId, tagId));
            Assert.Equal("rejected", await connection.ExecuteScalarAsync<string>("""
                SELECT state FROM card_tag_suggestions WHERE card_id = @CardId AND tag_id = @TagId;
                """, new { CardId = cardId, TagId = tagId }));
            await assignments.UpsertSuggestionsAsync(cardId,
                [new TagSuggestionProposal(tagId, 0.99m, "test-model:v2")]);
            Assert.Empty(await assignments.ListPendingSuggestionsAsync(cardId));
            Assert.Empty(await assignments.ListTagsAsync(cardId));

            await assignments.SetTagAsync(cardId, tagId);
            Assert.Equal("manual", Assert.Single(await assignments.ListTagsAsync(cardId)).AssignmentSource);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM card_tag_suggestions WHERE card_id = @CardId AND tag_id = @TagId;
                """, new { CardId = cardId, TagId = tagId }));
            await assignments.UpsertSuggestionsAsync(cardId,
                [new TagSuggestionProposal(pendingId, 0.75m, "test-model:v3")]);
            Assert.Single(await assignments.ListPendingSuggestionsAsync(cardId));
            await connection.ExecuteAsync("DELETE FROM cards WHERE id = @Id;", new { Id = cardId });
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM card_tag_suggestions WHERE card_id = @CardId;
                """, new { CardId = cardId }));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM cards WHERE id = @Id;", new { Id = cardId });
            if (tagId != 0)
                await connection.ExecuteAsync("DELETE FROM tags WHERE id = @Id;", new { Id = tagId });
            if (pendingId != 0)
                await connection.ExecuteAsync("DELETE FROM tags WHERE id = @Id;", new { Id = pendingId });
        }
    }

    private sealed record DecisionRow(long? SuggestedTagId, long? ChosenTagId, string Decision,
        string? SuggestionSource, string? ModelKey, decimal? Confidence);

    private static async Task<List<DecisionRow>> DecisionsAsync(MySqlConnection connection, long cardId) =>
        (await connection.QueryAsync<DecisionRow>("""
            SELECT suggested_tag_id AS SuggestedTagId, chosen_tag_id AS ChosenTagId, decision AS Decision,
                   suggestion_source AS SuggestionSource, model_key AS ModelKey, confidence AS Confidence
            FROM card_tag_decisions WHERE card_id = @CardId ORDER BY id;
            """, new { CardId = cardId })).ToList();

    [DatabaseFact]
    public async Task DecisionLogRecordsActualDecisionsOnlyOnce()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = Configuration(connectionString);
        await EnsureSchemaAsync(connectionString, configuration);
        var catalog = new CatalogRepository(configuration);
        var assignments = new CardCatalogRepository(configuration);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        var cardId = await InsertCardAsync(connection, "media", $"decision-log-{token}");
        var tagIds = new List<long>();
        try
        {
            for (var index = 0; index < 5; index++)
                tagIds.Add((await catalog.CreateTagAsync(new TagRequest($"log-{index}-{token}", null))).Id);
            await assignments.UpsertSuggestionsAsync(cardId, tagIds.Select(id =>
                new TagSuggestionProposal(id, 0.8m, "test-model:v1")).ToArray());
            var source = await connection.ExecuteScalarAsync<string?>("""
                SELECT suggestion_source FROM card_tag_suggestions WHERE card_id = @CardId LIMIT 1;
                """, new { CardId = cardId });
            Assert.Empty(await DecisionsAsync(connection, cardId));

            Assert.True(await assignments.DecideSuggestionAsync(cardId, tagIds[0], "accepted"));
            Assert.True(await assignments.DecideSuggestionAsync(cardId, tagIds[0], "accepted"));
            Assert.True(await assignments.DecideSuggestionAsync(cardId, tagIds[1], "rejected"));
            Assert.True(await assignments.DecideSuggestionAsync(cardId, tagIds[1], "rejected"));
            var rows = await DecisionsAsync(connection, cardId);
            Assert.Equal(2, rows.Count);
            var accepted = rows[0];
            Assert.Equal("accepted", accepted.Decision);
            Assert.Equal(tagIds[0], accepted.SuggestedTagId);
            Assert.Equal(tagIds[0], accepted.ChosenTagId);
            Assert.Equal("test-model:v1", accepted.ModelKey);
            Assert.Equal(0.8m, accepted.Confidence);
            Assert.Equal(source, accepted.SuggestionSource);
            Assert.Equal("rejected", rows[1].Decision);
            Assert.Equal(tagIds[1], rows[1].SuggestedTagId);
            Assert.Null(rows[1].ChosenTagId);

            // Batch: one new row per actually changed suggestion; repeats are not logged.
            Assert.Equal(2, await assignments.DecideSuggestionsAsync(cardId, [tagIds[2], tagIds[3]], "accepted"));
            Assert.Equal(0, await assignments.DecideSuggestionsAsync(cardId, [tagIds[2], tagIds[3]], "accepted"));
            Assert.Equal(1, await assignments.DecideSuggestionsAsync(cardId, null, "rejected"));
            rows = await DecisionsAsync(connection, cardId);
            Assert.Equal(5, rows.Count);
            Assert.Equal(new[] { tagIds[2], tagIds[3] }.Order(),
                rows.Skip(2).Take(2).Select(row => row.SuggestedTagId!.Value).Order());
            Assert.All(rows.Skip(2).Take(2), row => Assert.Equal("accepted", row.Decision));
            Assert.Equal("rejected", rows[4].Decision);
            Assert.Equal(tagIds[4], rows[4].SuggestedTagId);

            // A failed batch (conflict) must not leave partial log rows.
            await Assert.ThrowsAsync<CatalogConflictException>(() =>
                assignments.DecideSuggestionsAsync(cardId, tagIds, "rejected"));
            Assert.Equal(5, (await DecisionsAsync(connection, cardId)).Count);

            // Removing an assigned tag is logged as a rejection.
            Assert.True(await assignments.DeleteTagAsync(cardId, tagIds[0]));
            Assert.False(await assignments.DeleteTagAsync(cardId, tagIds[0]));
            rows = await DecisionsAsync(connection, cardId);
            Assert.Equal(6, rows.Count);
            Assert.Equal("rejected", rows[5].Decision);
            Assert.Equal(tagIds[0], rows[5].SuggestedTagId);

            // Cascade: card deletion removes the log; tag deletion keeps rows with NULL ids.
            await connection.ExecuteAsync("DELETE FROM tags WHERE id = @Id;", new { Id = tagIds[1] });
            Assert.Null((await DecisionsAsync(connection, cardId))[1].SuggestedTagId);
            await connection.ExecuteAsync("DELETE FROM cards WHERE id = @Id;", new { Id = cardId });
            Assert.Empty(await DecisionsAsync(connection, cardId));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM cards WHERE id = @Id;", new { Id = cardId });
            foreach (var tagId in tagIds)
                await connection.ExecuteAsync("DELETE FROM tags WHERE id = @Id;", new { Id = tagId });
        }
    }

    [DatabaseFact]
    public async Task CorrectionRejectsSuggestionAssignsReplacementAndLogsOnce()
    {
        var connectionString = Environment.GetEnvironmentVariable("NODECK_TEST_CONNECTION_STRING")!;
        var configuration = Configuration(connectionString);
        await EnsureSchemaAsync(connectionString, configuration);
        var catalog = new CatalogRepository(configuration);
        var assignments = new CardCatalogRepository(configuration);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var token = Guid.NewGuid().ToString("N");
        var cardId = await InsertCardAsync(connection, "media", $"correction-{token}");
        var tagIds = new List<long>();
        try
        {
            for (var index = 0; index < 5; index++)
                tagIds.Add((await catalog.CreateTagAsync(new TagRequest($"corr-{index}-{token}", null))).Id);
            var (suggested, replacement, confirmedReplacement, accepted, other) =
                (tagIds[0], tagIds[1], tagIds[2], tagIds[3], tagIds[4]);
            await assignments.UpsertSuggestionsAsync(cardId,
            [
                new TagSuggestionProposal(suggested, 0.66m, "test-model:v1"),
                new TagSuggestionProposal(confirmedReplacement, 0.7m, "test-model:v1"),
                new TagSuggestionProposal(accepted, 0.9m, "test-model:v1"),
                new TagSuggestionProposal(other, 0.5m, "test-model:v1")
            ]);
            var source = await connection.ExecuteScalarAsync<string?>("""
                SELECT suggestion_source FROM card_tag_suggestions WHERE card_id = @CardId AND tag_id = @TagId;
                """, new { CardId = cardId, TagId = suggested });

            // Validation and lookup failures log nothing.
            await Assert.ThrowsAsync<CatalogValidationException>(() =>
                assignments.CorrectSuggestionAsync(cardId, suggested, 0));
            await Assert.ThrowsAsync<CatalogValidationException>(() =>
                assignments.CorrectSuggestionAsync(cardId, suggested, -3));
            await Assert.ThrowsAsync<CatalogValidationException>(() =>
                assignments.CorrectSuggestionAsync(cardId, suggested, suggested));
            await Assert.ThrowsAsync<CatalogMissingException>(() =>
                assignments.CorrectSuggestionAsync(cardId, suggested, long.MaxValue));
            await Assert.ThrowsAsync<CatalogMissingException>(() =>
                assignments.CorrectSuggestionAsync(cardId, replacement, suggested));
            Assert.Empty(await DecisionsAsync(connection, cardId));

            // Pending suggestion corrected with a fresh replacement.
            await assignments.CorrectSuggestionAsync(cardId, suggested, replacement);
            Assert.Equal("rejected", await connection.ExecuteScalarAsync<string>("""
                SELECT state FROM card_tag_suggestions WHERE card_id = @CardId AND tag_id = @TagId;
                """, new { CardId = cardId, TagId = suggested }));
            var tags = await assignments.ListTagsAsync(cardId);
            Assert.DoesNotContain(tags, tag => tag.Id == suggested);
            Assert.Equal("manual", tags.Single(tag => tag.Id == replacement).AssignmentSource);
            var row = Assert.Single(await DecisionsAsync(connection, cardId));
            Assert.Equal("corrected", row.Decision);
            Assert.Equal(suggested, row.SuggestedTagId);
            Assert.Equal(replacement, row.ChosenTagId);
            Assert.Equal(source, row.SuggestionSource);
            Assert.Equal("test-model:v1", row.ModelKey);
            Assert.Equal(0.66m, row.Confidence);

            // Repeating on the now-rejected suggestion with the replacement assigned is a no-op.
            await assignments.CorrectSuggestionAsync(cardId, suggested, replacement);
            Assert.Single(await DecisionsAsync(connection, cardId));

            // Re-analysis must not resurrect or alter the corrected decision.
            await assignments.UpsertSuggestionsAsync(cardId,
                [new TagSuggestionProposal(suggested, 0.99m, "test-model:v2")]);
            Assert.Equal("rejected", await connection.ExecuteScalarAsync<string>("""
                SELECT state FROM card_tag_suggestions WHERE card_id = @CardId AND tag_id = @TagId;
                """, new { CardId = cardId, TagId = suggested }));
            Assert.DoesNotContain(await assignments.ListTagsAsync(cardId), tag => tag.Id == suggested);
            Assert.Single(await DecisionsAsync(connection, cardId));

            // A replacement already confirmed stays confirmed.
            Assert.True(await assignments.DecideSuggestionAsync(cardId, confirmedReplacement, "accepted"));
            var before = (await DecisionsAsync(connection, cardId)).Count;
            await assignments.CorrectSuggestionAsync(cardId, other, confirmedReplacement);
            Assert.Equal("confirmed", (await assignments.ListTagsAsync(cardId))
                .Single(tag => tag.Id == confirmedReplacement).AssignmentSource);
            Assert.Equal(before + 1, (await DecisionsAsync(connection, cardId)).Count);

            // Accepted suggestions cannot be corrected.
            Assert.True(await assignments.DecideSuggestionAsync(cardId, accepted, "accepted"));
            var count = (await DecisionsAsync(connection, cardId)).Count;
            await Assert.ThrowsAsync<CatalogConflictException>(() =>
                assignments.CorrectSuggestionAsync(cardId, accepted, replacement));
            Assert.Equal(count, (await DecisionsAsync(connection, cardId)).Count);
            Assert.Contains(await assignments.ListTagsAsync(cardId), tag => tag.Id == accepted);

            await connection.ExecuteAsync("DELETE FROM cards WHERE id = @Id;", new { Id = cardId });
            Assert.Empty(await DecisionsAsync(connection, cardId));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM cards WHERE id = @Id;", new { Id = cardId });
            foreach (var tagId in tagIds)
                await connection.ExecuteAsync("DELETE FROM tags WHERE id = @Id;", new { Id = tagId });
        }
    }

    [DatabaseFact]
    public async Task TagListSortsByNameByDefaultAndByUsageOnRequest()
    {
        await using var host = await DeckApiHost.CreateAsync();
        await using var api = await TagApi.StartAsync(host.Configuration);
        var catalog = new CatalogRepository(host.Configuration);
        var assignments = new CardCatalogRepository(host.Configuration);
        var heavy = (await catalog.CreateTagAsync(new TagRequest("zz-heavy", null))).Id;
        var middle = (await catalog.CreateTagAsync(new TagRequest("mm-middle", null))).Id;
        var light = (await catalog.CreateTagAsync(new TagRequest("aa-light", null))).Id;
        var unused = (await catalog.CreateTagAsync(new TagRequest("bb-unused", null))).Id;
        var legacy = (await catalog.CreateTagAsync(new TagRequest("cc-legacy", null))).Id;
        var cards = new List<long>();
        for (var i = 0; i < 6; i++) cards.Add(await host.InsertCardAsync("media", $"usage-{i}"));
        foreach (var card in cards.Take(3)) await assignments.SetTagAsync(card, heavy);
        foreach (var card in cards.Take(2)) await assignments.SetTagAsync(card, middle);
        await assignments.SetTagAsync(cards[0], light);
        // Legacy usage (tagged_with to the legacy tag card) counts too: 4 links beat every catalog tag.
        var legacyCard = await host.InsertCardAsync("tag", "legacy tag card");
        await host.Connection.ExecuteAsync("UPDATE tags SET legacy_card_id = @Card WHERE id = @Tag;",
            new { Card = legacyCard, Tag = legacy });
        foreach (var card in cards.Take(4))
            await host.Connection.ExecuteAsync("""
                INSERT INTO card_relations (from_card_id, to_card_id, relation_type, properties, metadata)
                VALUES (@From, @To, 'tagged_with', JSON_OBJECT(), JSON_OBJECT());
                """, new { From = card, To = legacyCard });

        var byDefault = await api.ListAsync("");
        var byName = await api.ListAsync("sort=name");
        var byUsage = await api.ListAsync("sort=usage");

        var nameOrder = new[] { light, unused, legacy, middle, heavy };
        Assert.Equal(nameOrder, byDefault.Select(t => t.Id).ToArray());
        Assert.Equal(nameOrder, byName.Select(t => t.Id).ToArray());
        // Unused tags are left out of the usage list without a search.
        Assert.Equal(new[] { legacy, heavy, middle, light }, byUsage.Select(t => t.Id).ToArray());
        Assert.Equal(new[] { 4, 3, 2, 1 }, byUsage.Select(t => t.ReferenceCount).ToArray());
        Assert.Contains(unused, byDefault.Select(t => t.Id));
    }

    [DatabaseFact]
    public async Task UsageSortWithSearchKeepsExactMatchFirstThenUsageAndFindsUnusedTags()
    {
        await using var host = await DeckApiHost.CreateAsync();
        await using var api = await TagApi.StartAsync(host.Configuration);
        var catalog = new CatalogRepository(host.Configuration);
        var assignments = new CardCatalogRepository(host.Configuration);
        var exact = (await catalog.CreateTagAsync(new TagRequest("rock", null))).Id;
        var popular = (await catalog.CreateTagAsync(new TagRequest("rock-popular", null))).Id;
        var fewer = (await catalog.CreateTagAsync(new TagRequest("rock-fewer", null))).Id;
        var unused = (await catalog.CreateTagAsync(new TagRequest("rock-aaa-unused", null))).Id;
        var unrelated = (await catalog.CreateTagAsync(new TagRequest("jazz", null))).Id;
        var cards = new List<long>();
        for (var i = 0; i < 4; i++) cards.Add(await host.InsertCardAsync("media", $"search-usage-{i}"));
        await assignments.SetTagAsync(cards[0], exact);
        foreach (var card in cards.Take(3)) await assignments.SetTagAsync(card, popular);
        foreach (var card in cards.Take(2)) await assignments.SetTagAsync(card, fewer);
        foreach (var card in cards) await assignments.SetTagAsync(card, unrelated);

        var byUsage = await api.ListAsync("search=rock&sort=usage");
        var byName = await api.ListAsync("search=rock");

        Assert.Equal(new[] { exact, popular, fewer, unused }, byUsage.Select(t => t.Id).ToArray());
        Assert.Equal(new[] { exact, unused, fewer, popular }, byName.Select(t => t.Id).ToArray());
        Assert.DoesNotContain(unrelated, byUsage.Select(t => t.Id));
        Assert.Equal(0, byUsage.Single(t => t.Id == unused).ReferenceCount);
    }

    [DatabaseFact]
    public async Task InvalidTagSortIsRejected()
    {
        await using var host = await DeckApiHost.CreateAsync();
        await using var api = await TagApi.StartAsync(host.Configuration);

        foreach (var query in new[] { "sort=popular", "sort=", "sort=USAGE" })
        {
            using var response = await api.Client.GetAsync("/api/tags?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal("sort must be name or usage.", body!.RootElement.GetProperty("error").GetString());
        }
        using var ok = await api.Client.GetAsync("/api/tags?sort=usage");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [DatabaseFact]
    public async Task CategoryFilterAppliesWithBothSortsAndSearch()
    {
        await using var host = await DeckApiHost.CreateAsync();
        await using var api = await TagApi.StartAsync(host.Configuration);
        var catalog = new CatalogRepository(host.Configuration);
        var assignments = new CardCatalogRepository(host.Configuration);
        async Task<long> ImportedAsync(string name, string category) =>
            await host.Connection.ExecuteScalarAsync<long>("""
                INSERT INTO tags (name, canonical_key, category, origin)
                VALUES (@Name, @Key, @Category, 'danbooru');
                SELECT LAST_INSERT_ID();
                """, new { Name = name, Key = "danbooru:" + name, Category = category });
        var artistZ = await ImportedAsync("artist-zed", "artist");
        var artistM = await ImportedAsync("artist-mid", "artist");
        var artistUnused = await ImportedAsync("artist-unused", "artist");
        var character = await ImportedAsync("char-one", "character");
        // A user tag whose category is also 'artist' must not pass the artist filter (origin 'user').
        var userArtist = await ImportedAsync("user-fake-artist", "artist");
        await host.Connection.ExecuteAsync("UPDATE tags SET origin = 'user' WHERE id = @Id;", new { Id = userArtist });
        var custom = (await catalog.CreateTagAsync(new TagRequest("my-custom", null))).Id;
        var customUnused = (await catalog.CreateTagAsync(new TagRequest("aaa-custom-unused", null))).Id;
        var cards = new List<long>();
        for (var i = 0; i < 4; i++) cards.Add(await host.InsertCardAsync("media", $"category-{i}"));
        foreach (var card in cards.Take(1)) await assignments.SetTagAsync(card, artistZ);
        foreach (var card in cards.Take(3)) await assignments.SetTagAsync(card, artistM);
        foreach (var card in cards) await assignments.SetTagAsync(card, character);
        foreach (var card in cards) await assignments.SetTagAsync(card, userArtist);
        foreach (var card in cards.Take(2)) await assignments.SetTagAsync(card, custom);

        var artistsByUsage = await api.ListAsync("category=artist&sort=usage");
        var artistsByName = await api.ListAsync("category=artist");
        var artistSearch = await api.ListAsync("category=artist&search=artist&sort=usage");
        var customByUsage = await api.ListAsync("category=custom&sort=usage");
        var customByName = await api.ListAsync("category=custom");
        var characterSearch = await api.ListAsync("category=character&search=artist");

        // Only that category's used tags, most used first.
        Assert.Equal(new[] { artistM, artistZ }, artistsByUsage.Select(t => t.Id).ToArray());
        Assert.Equal(new[] { artistM, artistUnused, artistZ }, artistsByName.Select(t => t.Id).ToArray());
        // With search the unused matching tag is found again, still within the category and in usage order.
        Assert.Equal(new[] { artistM, artistZ, artistUnused }, artistSearch.Select(t => t.Id).ToArray());
        // Custom means user-created tags only: includes the user-origin 'artist' row, never imported ones.
        Assert.Equal(new[] { userArtist, custom }, customByUsage.Select(t => t.Id).ToArray());
        Assert.Equal(new[] { customUnused, custom, userArtist }, customByName.Select(t => t.Id).ToArray());
        Assert.Empty(characterSearch);
        Assert.DoesNotContain(character, artistsByName.Select(t => t.Id));
    }

    [DatabaseFact]
    public async Task InvalidTagCategoryIsRejected()
    {
        await using var host = await DeckApiHost.CreateAsync();
        await using var api = await TagApi.StartAsync(host.Configuration);

        foreach (var query in new[] { "category=bogus", "category=", "category=Artist" })
        {
            using var response = await api.Client.GetAsync("/api/tags?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal("category must be general, artist, character, copyright, meta or custom.",
                body!.RootElement.GetProperty("error").GetString());
        }
        foreach (var valid in new[] { "general", "artist", "character", "copyright", "meta", "custom" })
        {
            using var ok = await api.Client.GetAsync($"/api/tags?category={valid}");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
    }

    private sealed record TagRow(long Id, string Name, int ReferenceCount);

    /// <summary>Minimal Kestrel host mapping only the catalog endpoints over a given configuration.</summary>
    private sealed class TagApi : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Client { get; }

        private TagApi(WebApplication app, HttpClient client) { _app = app; Client = client; }

        public static async Task<TagApi> StartAsync(IConfiguration configuration)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddConfiguration(configuration);
            builder.Services.AddScoped<CatalogRepository>();
            builder.Services.AddScoped<CardCatalogRepository>();
            var app = builder.Build();
            app.MapCatalogEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            return new TagApi(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async Task<TagRow[]> ListAsync(string query)
        {
            using var response = await Client.GetAsync("/api/tags?" + query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
            return body!.RootElement.EnumerateArray().Select(e => new TagRow(
                e.GetProperty("id").GetInt64(), e.GetProperty("name").GetString()!,
                e.GetProperty("referenceCount").GetInt32())).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
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

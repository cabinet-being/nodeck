using Dapper;
using MySqlConnector;

namespace MyApp.Api.Cards;

public sealed record MoveChildRequest(long TargetParentId, int? Position);

public enum MoveChildStatus { Moved, NotFound, Invalid }

public sealed record DuplicateMatch(string Sha256, IReadOnlyList<long> CardIds);

public sealed record DuplicatesRequest(IReadOnlyList<string>? Sha256);

public sealed partial class CardRepository
{
    private static string ChildOrderSql =>
        "ORDER BY CAST(JSON_UNQUOTE(JSON_EXTRACT(properties, '$.position')) AS SIGNED), id";

    public async Task<(MoveChildStatus Status, string? Error)> MoveChildAsync(
        long sourceId, long childId, long targetId, int? position)
    {
        if (sourceId == targetId) return (MoveChildStatus.Invalid, "Target must differ from the source collection.");

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var parents = (await connection.QueryAsync<(long Id, string Type)>(
                "SELECT id, type FROM cards WHERE id IN @Ids ORDER BY id FOR UPDATE;",
                new { Ids = new[] { sourceId, targetId } }, transaction)).ToDictionary(p => p.Id, p => p.Type);
            if (!parents.TryGetValue(sourceId, out var sourceType) || !parents.TryGetValue(targetId, out var targetType))
            {
                await transaction.RollbackAsync();
                return (MoveChildStatus.NotFound, "Source or target collection not found.");
            }
            if (sourceType is not ("comic" or "set"))
            {
                await transaction.RollbackAsync();
                return (MoveChildStatus.NotFound, "Child is not part of the source collection.");
            }
            if (targetType is not ("comic" or "set"))
            {
                await transaction.RollbackAsync();
                return (MoveChildStatus.Invalid, "Target must be a comic or set.");
            }

            var sourceChildren = (await connection.QueryAsync<long>(
                $"SELECT to_card_id FROM card_relations WHERE from_card_id = @Id AND relation_type = 'contains' {ChildOrderSql} FOR UPDATE;",
                new { Id = sourceId }, transaction)).ToList();
            if (!sourceChildren.Remove(childId))
            {
                await transaction.RollbackAsync();
                return (MoveChildStatus.NotFound, "Child is not part of the source collection.");
            }
            var targetChildren = (await connection.QueryAsync<long>(
                $"SELECT to_card_id FROM card_relations WHERE from_card_id = @Id AND relation_type = 'contains' {ChildOrderSql} FOR UPDATE;",
                new { Id = targetId }, transaction)).ToList();
            var index = position is >= 0 && position <= targetChildren.Count ? position.Value : targetChildren.Count;
            targetChildren.Insert(index, childId);

            await ReplaceCollectionChildrenAsync(connection, transaction, sourceId, sourceType, sourceChildren);
            await ReplaceCollectionChildrenAsync(connection, transaction, targetId, targetType, targetChildren);
            await transaction.CommitAsync();
            return (MoveChildStatus.Moved, null);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<DuplicateMatch>> FindDuplicatesAsync(IReadOnlyList<string> hashes)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        var rows = await connection.QueryAsync<(string Hash, long Id)>(
            "SELECT content_sha256, id FROM cards WHERE content_sha256 IN @Hashes ORDER BY id;",
            new { Hashes = hashes.ToArray() });
        return rows.GroupBy(r => r.Hash)
            .Select(g => new DuplicateMatch(g.Key, g.Select(r => r.Id).ToList()))
            .OrderBy(m => m.Sha256, StringComparer.Ordinal).ToList();
    }
}

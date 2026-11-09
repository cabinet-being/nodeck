using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;

namespace MyApp.Api.Catalog;

public sealed record WdVocabularySeedResult(int Eligible, int Inserted, int Existing, int Conflicts);

/// <summary>Seeds the WD model's selected Danbooru tags without advancing the full import cursor.</summary>
public sealed class WdVocabularySeeder(string connectionString)
{
    private readonly string _connectionString = !string.IsNullOrWhiteSpace(connectionString)
        ? connectionString : throw new ArgumentException("A connection string is required.", nameof(connectionString));

    public async Task<WdVocabularySeedResult> RunAsync(string labelsPath, CancellationToken cancellationToken = default)
    {
        var labels = ReadLabels(labelsPath);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        // Use the same lock as the full importer so both cannot alter Danbooru tags concurrently.
        var lockName = "nodeck-danbooru-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(connection.Database)))[..24];
        var acquired = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT GET_LOCK(@LockName, 30);", new { LockName = lockName }, cancellationToken: cancellationToken));
        if (acquired != 1)
            throw new InvalidOperationException("Could not acquire the Danbooru import lock.");
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var inserted = 0;
            var existing = 0;
            var conflicts = 0;
            foreach (var label in labels)
            {
                var current = await connection.QuerySingleOrDefaultAsync<ExistingTag>(new CommandDefinition(
                    "SELECT id, name, category, origin FROM tags WHERE danbooru_id = @Id FOR UPDATE;",
                    new { label.Id }, transaction, cancellationToken: cancellationToken));
                if (current is not null)
                {
                    if (current.Origin == "danbooru" && current.Name == label.Name && current.Category == label.Category)
                        existing++;
                    else
                        conflicts++;
                    continue;
                }
                var occupied = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                    SELECT (SELECT COUNT(*) FROM tags WHERE name = @Name OR canonical_key = @Key)
                         + (SELECT COUNT(*) FROM tag_aliases WHERE alias = @Name);
                    """, new { label.Name, Key = $"danbooru-tag:{label.Id}" },
                    transaction, cancellationToken: cancellationToken));
                if (occupied != 0)
                {
                    conflicts++;
                    continue;
                }
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO tags (name, canonical_key, category, origin, danbooru_id)
                    VALUES (@Name, @Key, @Category, 'danbooru', @Id);
                    """, new { label.Name, Key = $"danbooru-tag:{label.Id}", label.Category, label.Id },
                    transaction, cancellationToken: cancellationToken));
                inserted++;
            }
            if (inserted + existing == 0)
                throw new InvalidOperationException("No WD labels could be matched or inserted; vocabulary remains unavailable.");
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO danbooru_import_state
                    (kind, completed, processed, conflicts, updated_at_utc)
                VALUES ('wd_vocabulary', TRUE, @Processed, @Conflicts, UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE completed = TRUE, processed = @Processed,
                    conflicts = @Conflicts, updated_at_utc = UTC_TIMESTAMP(6);
                """, new { Processed = labels.Count, Conflicts = conflicts }, transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new WdVocabularySeedResult(labels.Count, inserted, existing, conflicts);
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT RELEASE_LOCK(@LockName);", new { LockName = lockName }));
        }
    }

    private static List<WdLabel> ReadLabels(string labelsPath)
    {
        if (string.IsNullOrWhiteSpace(labelsPath) || !File.Exists(labelsPath))
            throw new FileNotFoundException("The WD ViT selected_tags.csv file is missing.", labelsPath);
        using var reader = new StreamReader(labelsPath);
        if (reader.ReadLine() is not "tag_id,name,category,count")
            throw new InvalidDataException("The WD ViT labels have an unexpected header.");
        var labels = new List<WdLabel>();
        var ids = new HashSet<long>();
        string? line;
        var row = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            row++;
            if (line.Length == 0) continue;
            var firstComma = line.IndexOf(',');
            var lastComma = line.LastIndexOf(',');
            var categoryComma = lastComma > 0 ? line.LastIndexOf(',', lastComma - 1) : -1;
            if (firstComma <= 0 || categoryComma <= firstComma || lastComma <= categoryComma ||
                !long.TryParse(line.AsSpan(0, firstComma), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var id) ||
                !int.TryParse(line.AsSpan(categoryComma + 1, lastComma - categoryComma - 1),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var category) ||
                !long.TryParse(line.AsSpan(lastComma + 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException($"Invalid WD ViT label at row {row}.");
            if (category is not (0 or 4)) continue;
            var name = line[(firstComma + 1)..categoryComma].Trim();
            if (id <= 0 || name.Length is < 1 or > 255 || !ids.Add(id))
                throw new InvalidDataException($"Invalid WD ViT general/character label at row {row}.");
            labels.Add(new WdLabel(id, name, category == 0 ? "general" : "character"));
        }
        if (labels.Count == 0)
            throw new InvalidDataException("The WD ViT labels have no general or character tags.");
        return labels;
    }

    private sealed record WdLabel(long Id, string Name, string Category);
    private sealed record ExistingTag(long Id, string Name, string Category, string Origin);
}

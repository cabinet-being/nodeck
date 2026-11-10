namespace MyApp.Api.Storage;

/// <summary>Finds numeric card directories under cards/ and cache/cards/ that have no card row. Never deletes.</summary>
internal static class OrphanStorageScanner
{
    internal static IReadOnlyList<string> FindOrphans(
        string appDataRoot, ISet<long> knownCardIds, TimeSpan minimumAge, DateTime? utcNow = null)
    {
        var cutoff = (utcNow ?? DateTime.UtcNow) - minimumAge;
        var orphans = new List<string>();
        foreach (var root in new[] { Path.Combine(appDataRoot, "cards"), Path.Combine(appDataRoot, "cache", "cards") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (!long.TryParse(Path.GetFileName(directory), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var id) || knownCardIds.Contains(id))
                    continue;
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff) orphans.Add(directory);
            }
        }
        orphans.Sort(StringComparer.Ordinal);
        return orphans;
    }
}

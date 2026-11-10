using MyApp.Api.Catalog;

namespace MyApp.Api.Analysis;

/// <summary>Seeds the WD vocabulary once, the first time the model's selected_tags.csv is available.</summary>
public static class WdVocabularyAutoSeed
{
    /// <summary>Returns the seed result, or null when nothing was needed (already seeded or no labels file).</summary>
    public static async Task<WdVocabularySeedResult?> SeedIfNeededAsync(string connectionString,
        AnalysisQueueRepository queue, string labelsPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(labelsPath) || await queue.IsVocabularyReadyAsync(cancellationToken)) return null;
        return await new WdVocabularySeeder(connectionString).RunAsync(labelsPath, cancellationToken);
    }
}

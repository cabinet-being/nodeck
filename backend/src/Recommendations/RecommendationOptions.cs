namespace MyApp.Api.Recommendations;

/// <summary>Tunable recommendation scoring, bound to the "Recommendations" configuration section.</summary>
public sealed class RecommendationOptions
{
    public const string SectionName = "Recommendations";

    // Score term weights.
    public double WAffinity { get; set; } = 1.0;
    public double WContext { get; set; } = 1.0;
    public double WSuggest { get; set; } = 0.5;
    public double WSelfVote { get; set; } = 1.0;
    public double WSeen { get; set; } = 1.5;
    public double WFresh { get; set; } = 1.0;
    public double WDiversity { get; set; } = 4.0;

    // Suggestions below this confidence are ignored.
    public double SuggestMinConfidence { get; set; } = 0.5;
    // Views of a card inside this window are penalised as "already seen".
    public int SeenWindowDays { get; set; } = 3;

    // Signal sources.
    public int ViewWindowDays { get; set; } = 30;
    public int VoteWindowDays { get; set; } = 90;
    public int RecentViewLimit { get; set; } = 500;
    public int RecentVoteLimit { get; set; } = 500;
    public int NewestCardLimit { get; set; } = 200;
    public int TagCandidateLimit { get; set; } = 50;
    public double ViewHalfLifeDays { get; set; } = 7.0;
    public double ViewCardCap { get; set; } = 3.0;
    public double PositiveVoteWeight { get; set; } = 2.0;
    public double NegativeVoteWeight { get; set; } = 3.0;
    public double VoteCardClamp { get; set; } = 8.0;
    public double TagProfileClamp { get; set; } = 12.0;
    public double ContextTagWeight { get; set; } = 4.0;
    public double ContextScale { get; set; } = 8.0;
    /// <summary>Score range of the normalised profile affinity, which lies in [-1, 1] before scaling.</summary>
    public double AffinityScale { get; set; } = 8.0;
    /// <summary>Specificity mass added to the affinity denominator; shrinks sparsely tagged cards.</summary>
    public double AffinitySpecPrior { get; set; } = 2.0;
    public double SuggestCap { get; set; } = 3.0;
    public double SeenCap { get; set; } = 3.0;
    public double FreshnessMax { get; set; } = 2.0;
    public double FreshnessHalfLifeDays { get; set; } = 7.0;

    // "Similar" (a context card is given): candidates are the cards that share the most
    // specific tags with the context, and the personal profile only nudges the order.
    public int ContextCandidateLimit { get; set; } = 300;
    public int ContextCandidateTagLimit { get; set; } = 24;
    public double ContextAffinityFactor { get; set; } = 0.25;

    // Paged feeds rank at most this many cards; it is also the largest reported total.
    public int MaxRankedCards { get; set; } = 500;
    // Today's picks scans at most this many cards in the day's order (file checks included).
    public int DailyScanLimit { get; set; } = 5000;

    // Rarer tags and more informative categories count more (see tags.category).
    public Dictionary<string, double> CategoryMultipliers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["artist"] = 1.5,
        ["character"] = 1.3,
        ["copyright"] = 1.2,
        ["custom"] = 1.2,
        ["general"] = 1.0,
        ["meta"] = 0.3,
    };
    public double UnknownCategoryMultiplier { get; set; } = 1.0;
}

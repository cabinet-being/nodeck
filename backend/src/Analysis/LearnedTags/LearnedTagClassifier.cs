namespace MyApp.Api.Analysis.LearnedTags;

/// <summary>One training card for a tag. Hard negatives are cards where the tag was rejected or corrected.</summary>
public sealed record LearnedTagExample(VisualFeatureVector Features, bool Positive, bool HardNegative = false);

public sealed record LearnedTagModel(
    int Dimension, float[] Weights, double Bias, decimal? Threshold,
    int Positives, int Negatives, int HardNegatives, decimal? Precision, decimal? Recall)
{
    public bool Enabled => Threshold is not null;

    public double Score(VisualFeatureVector features)
    {
        if (features.Dimension != Dimension)
            throw new InvalidOperationException("The feature vector does not match the model dimension.");
        return LearnedTagClassifier.Sigmoid(features.Dot(Weights) + Bias);
    }
}

/// <summary>
/// Per-tag L2-regularised logistic regression on sparse tagger probabilities, trained with AdaGrad.
/// A tag gets a threshold only when cross-validation shows it can be proposed at the target precision;
/// otherwise its model is kept for status but proposes nothing.
/// </summary>
public static class LearnedTagClassifier
{
    public const int MinPositives = 5;
    public const double TargetPrecision = 0.8;
    public const double MinThreshold = 0.5;
    public const int MinTruePositivesAtThreshold = 3;
    public const double HardNegativeWeight = 3;
    private const int Folds = 3;
    private const int Epochs = 50;
    private const double LearningRate = 1.0;
    private const double L2 = 3e-2;

    /// <param name="negativeScale">
    /// How many library cards each sampled ordinary negative stands for, so precision is estimated for the whole
    /// library rather than for the sample.
    /// </param>
    public static LearnedTagModel Train(int dimension, IReadOnlyList<LearnedTagExample> examples,
        double negativeScale, int seed)
    {
        if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension));
        if (negativeScale < 1 || !double.IsFinite(negativeScale)) throw new ArgumentOutOfRangeException(nameof(negativeScale));
        if (examples.Any(item => item.Features.Dimension != dimension))
            throw new ArgumentException("Every example must have the model dimension.", nameof(examples));
        var positives = examples.Count(item => item.Positive);
        var negatives = examples.Count - positives;
        var hard = examples.Count(item => !item.Positive && item.HardNegative);
        if (positives < MinPositives || negatives == 0)
        {
            var empty = Fit(dimension, examples, seed);
            return new LearnedTagModel(dimension, empty.Weights, empty.Bias, null, positives, negatives, hard,
                null, null);
        }

        // Stratified folds: out-of-fold scores estimate how the model ranks cards it has not seen.
        var random = new Random(seed);
        var fold = new int[examples.Count];
        foreach (var group in new[] { true, false })
        {
            var members = Enumerable.Range(0, examples.Count).Where(i => examples[i].Positive == group)
                .OrderBy(_ => random.Next()).ToArray();
            for (var k = 0; k < members.Length; k++) fold[members[k]] = k % Folds;
        }
        var outOfFold = new double[examples.Count];
        for (var f = 0; f < Folds; f++)
        {
            var train = Enumerable.Range(0, examples.Count).Where(i => fold[i] != f).Select(i => examples[i]).ToArray();
            if (!train.Any(item => item.Positive) || train.All(item => item.Positive)) continue;
            var partial = Fit(dimension, train, seed + f + 1);
            for (var i = 0; i < examples.Count; i++)
                if (fold[i] == f) outOfFold[i] = Sigmoid(examples[i].Features.Dot(partial.Weights) + partial.Bias);
        }
        var (threshold, precision, recall) = ChooseThreshold(examples, outOfFold, negativeScale, positives);
        var final = Fit(dimension, examples, seed);
        return new LearnedTagModel(dimension, final.Weights, final.Bias, threshold, positives, negatives, hard,
            precision, recall);
    }

    /// <summary>The lowest cut that keeps the estimated precision at the target, so the highest recall.</summary>
    private static (decimal? Threshold, decimal? Precision, decimal? Recall) ChooseThreshold(
        IReadOnlyList<LearnedTagExample> examples, double[] scores, double negativeScale, int positives)
    {
        var ranked = Enumerable.Range(0, examples.Count).OrderByDescending(i => scores[i])
            .ThenBy(i => examples[i].Positive ? 1 : 0).ToArray();
        double truePositives = 0, falsePositives = 0;
        (decimal? Threshold, decimal? Precision, decimal? Recall) best = (null, null, null);
        var groupHasPositive = false;
        for (var k = 0; k < ranked.Length; k++)
        {
            var example = examples[ranked[k]];
            if (example.Positive) truePositives++;
            else falsePositives += example.HardNegative ? 1 : negativeScale;
            groupHasPositive |= example.Positive;
            // Decide only after the last card with this score, so ties never split, and only where a
            // positive sits: lowering the threshold to a negative adds false positives and no recall.
            if (k + 1 < ranked.Length && scores[ranked[k + 1]] == scores[ranked[k]]) continue;
            if (!groupHasPositive) continue;
            groupHasPositive = false;
            var score = scores[ranked[k]];
            if (score < MinThreshold) break;
            var precision = truePositives / (truePositives + falsePositives);
            if (truePositives >= MinTruePositivesAtThreshold && precision >= TargetPrecision)
            {
                // Any cut between this card and the next lower one scores the same in validation; take the
                // middle (in log-odds) for a margin on unseen cards.
                var cut = k + 1 < ranked.Length
                    ? Math.Max(MinThreshold, Sigmoid((Logit(score) + Logit(scores[ranked[k + 1]])) / 2))
                    : score;
                best = (Round(cut), Round(precision), Round(truePositives / positives));
            }
        }
        return best;
    }

    private static decimal Round(double value) =>
        decimal.Round((decimal)Math.Clamp(value, 0, 1), 5, MidpointRounding.ToZero);

    private static (float[] Weights, double Bias) Fit(int dimension, IReadOnlyList<LearnedTagExample> examples,
        int seed)
    {
        var weights = new float[dimension];
        var squared = new double[dimension];
        double bias = 0, biasSquared = 0;
        var positives = examples.Count(item => item.Positive);
        var negativeWeight = examples.Where(item => !item.Positive)
            .Sum(item => item.HardNegative ? HardNegativeWeight : 1);
        if (positives == 0 || negativeWeight == 0) return (weights, 0);
        // Balance the classes; the cross-validated threshold, not 0.5, decides what is proposed.
        var positiveWeight = negativeWeight / positives;
        var order = Enumerable.Range(0, examples.Count).ToArray();
        var random = new Random(seed);
        for (var epoch = 0; epoch < Epochs; epoch++)
        {
            random.Shuffle(order);
            foreach (var index in order)
            {
                var example = examples[index];
                var features = example.Features;
                var weight = example.Positive ? positiveWeight : example.HardNegative ? HardNegativeWeight : 1;
                var error = (Sigmoid(features.Dot(weights) + bias) - (example.Positive ? 1 : 0)) * weight;
                for (var i = 0; i < features.Count; i++)
                {
                    var j = features.Indices[i];
                    var gradient = error * features.Values[i] + L2 * weights[j];
                    squared[j] += gradient * gradient;
                    weights[j] -= (float)(LearningRate * gradient / (Math.Sqrt(squared[j]) + 1e-8));
                }
                biasSquared += error * error;
                bias -= LearningRate * error / (Math.Sqrt(biasSquared) + 1e-8);
            }
        }
        return (weights, bias);
    }

    private static double Logit(double probability)
    {
        var p = Math.Clamp(probability, 1e-9, 1 - 1e-9);
        return Math.Log(p / (1 - p));
    }

    internal static double Sigmoid(double value) => value >= 0
        ? 1 / (1 + Math.Exp(-value))
        : Math.Exp(value) / (1 + Math.Exp(value));
}

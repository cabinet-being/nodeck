namespace MyApp.Api.Analysis;

public sealed record VisualTagPrediction(long DanbooruId, decimal Confidence, int Category);

/// <summary>Thresholded tag predictions plus, when the tagger provides one, the card's visual feature vector.</summary>
public sealed record VisualTagAnalysis(IReadOnlyList<VisualTagPrediction> Predictions, VisualFeatureVector? Features);

public interface IVisualTagger : IDisposable
{
    string ModelKey { get; }

    IReadOnlyList<VisualTagPrediction> Predict(string imagePath);

    /// <summary>Taggers without a feature vector fall back to plain predictions.</summary>
    VisualTagAnalysis Analyze(string imagePath) => new(Predict(imagePath), null);
}

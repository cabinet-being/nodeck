namespace MyApp.Api.Analysis;

public sealed record VisualTagPrediction(long DanbooruId, decimal Confidence, int Category);

public interface IVisualTagger : IDisposable
{
    string ModelKey { get; }

    IReadOnlyList<VisualTagPrediction> Predict(string imagePath);
}

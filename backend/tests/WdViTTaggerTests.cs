using MyApp.Api.Analysis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MyApp.Api.Tests;

public sealed class TaggerModelFactAttribute : FactAttribute
{
    public TaggerModelFactAttribute()
    {
        var directory = Environment.GetEnvironmentVariable("NODECK_TAGGER_MODEL_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            Skip = "Set NODECK_TAGGER_MODEL_DIR to run real WD ViT inference.";
    }
}

public sealed class WdViTTaggerTests
{
    [TaggerModelFact]
    public void InstalledModelRunsInferenceOnDeterministicImage()
    {
        var directory = Environment.GetEnvironmentVariable("NODECK_TAGGER_MODEL_DIR")!;
        var imagePath = Path.Combine(Path.GetTempPath(), $"nodeck-tagger-smoke-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = new Image<Rgba32>(32, 32))
            {
                for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                    image[x, y] = (x + y) % 2 == 0
                        ? new Rgba32(220, 80, 90)
                        : new Rgba32(60, 140, 225);
                image.SaveAsPng(imagePath);
            }

            using var tagger = new WdViTTagger(
                Path.Combine(directory, "model.onnx"),
                Path.Combine(directory, "selected_tags.csv"));
            var predictions = tagger.Predict(imagePath);

            Assert.Equal(WdViTTagger.PinnedModelKey, tagger.ModelKey);
            Assert.NotEmpty(predictions);
            Assert.All(predictions, item =>
            {
                Assert.True(item.DanbooruId > 0);
                Assert.InRange(item.Confidence, 0m, 1m);
                Assert.Contains(item.Category, new[] { 0, 4 });
            });
            Assert.Equal(predictions.OrderByDescending(item => item.Confidence), predictions);
        }
        finally
        {
            if (File.Exists(imagePath)) File.Delete(imagePath);
        }
    }
}

using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MyApp.Api.Analysis;

/// <summary>Local CPU inference for the pinned WD ViT v3 Danbooru tagger.</summary>
public sealed class WdViTTagger : IVisualTagger
{
    public const string PinnedModelKey =
        "wd-vit-tagger-v3@7f6b584d0bd3f55c4531f14ba3d4761b2bccdc0f";
    private const float GeneralThreshold = 0.35f;
    private const float CharacterThreshold = 0.85f;
    private const long MaxImagePixels = 40_000_000;

    private readonly InferenceSession _session;
    private readonly Label[] _labels;
    private readonly string _inputName;
    private readonly int _inputSize;
    private readonly object _gate = new();
    private bool _disposed;

    public string ModelKey => PinnedModelKey;

    public WdViTTagger(string modelPath, string labelsPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new FileNotFoundException("The WD ViT ONNX model is missing.", modelPath);
        _labels = ReadLabels(labelsPath);
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
        };
        _session = new InferenceSession(modelPath, options);
        try
        {
            var input = _session.InputMetadata.Single();
            var dimensions = input.Value.Dimensions;
            if (dimensions.Length != 4 || dimensions[1] <= 0 ||
                dimensions[1] != dimensions[2] || dimensions[3] != 3 || dimensions[1] > 1024)
                throw new InvalidOperationException("The WD ViT model must have an NHWC square RGB input.");
            _inputName = input.Key;
            _inputSize = dimensions[1];
            if (_session.OutputNames.Count != 1)
                throw new InvalidOperationException("The WD ViT model must have one score output.");
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    public IReadOnlyList<VisualTagPrediction> Predict(string imagePath)
    {
        var pixels = PrepareImage(imagePath, _inputSize);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var input = OrtValue.CreateTensorValueFromMemory(
                pixels, new long[] { 1, _inputSize, _inputSize, 3 });
            using var runOptions = new RunOptions();
            using var outputs = _session.Run(runOptions,
                new Dictionary<string, OrtValue> { [_inputName] = input }, _session.OutputNames);
            if (outputs.Count != 1)
                throw new InvalidOperationException("The WD ViT model returned an unexpected output count.");
            var scores = outputs[0].GetTensorDataAsSpan<float>();
            if (scores.Length != _labels.Length)
                throw new InvalidOperationException(
                    $"The WD ViT labels have {_labels.Length} rows, but the model returned {scores.Length} scores.");
            var predictions = new List<VisualTagPrediction>();
            for (var index = 0; index < scores.Length; index++)
            {
                var label = _labels[index];
                var score = scores[index];
                if (label.DanbooruId <= 0 || !float.IsFinite(score) || score is < 0 or > 1)
                    continue;
                var threshold = label.Category switch
                {
                    0 => GeneralThreshold,
                    4 => CharacterThreshold,
                    _ => float.PositiveInfinity,
                };
                if (score <= threshold) continue;
                predictions.Add(new VisualTagPrediction(label.DanbooruId,
                    decimal.Round((decimal)score, 5, MidpointRounding.AwayFromZero), label.Category));
            }
            return predictions.OrderByDescending(item => item.Confidence).ToArray();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _session.Dispose();
            _disposed = true;
        }
    }

    internal static float[] PrepareImage(string imagePath, int size)
    {
        if (size is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(size));
        var info = Image.Identify(imagePath)
            ?? throw new InvalidOperationException("The image cannot be identified.");
        if ((long)info.Width * info.Height > MaxImagePixels)
            throw new InvalidOperationException("The image exceeds the analysis pixel limit.");

        var squareSize = Math.Max(info.Width, info.Height);
        if ((long)squareSize * squareSize > MaxImagePixels)
            throw new InvalidOperationException("The padded image exceeds the analysis pixel limit.");

        using var source = Image.Load<Rgba32>(imagePath);
        using var image = new Image<Rgb24>(squareSize, squareSize, new Rgb24(255, 255, 255));
        var left = (squareSize - source.Width) / 2;
        var top = (squareSize - source.Height) / 2;
        image.ProcessPixelRows(targetAccessor =>
        {
            for (var y = 0; y < source.Height; y++)
            {
                var targetRow = targetAccessor.GetRowSpan(y + top);
                for (var x = 0; x < source.Width; x++)
                {
                    var pixel = source[x, y];
                    var alpha = pixel.A / 255f;
                    var white = 255f * (1f - alpha);
                    targetRow[x + left] = new Rgb24(
                        (byte)Math.Clamp(MathF.Round(pixel.R * alpha + white), 0, 255),
                        (byte)Math.Clamp(MathF.Round(pixel.G * alpha + white), 0, 255),
                        (byte)Math.Clamp(MathF.Round(pixel.B * alpha + white), 0, 255));
                }
            }
        });
        image.Mutate(context => context.Resize(size, size, KnownResamplers.Bicubic));

        var pixels = new float[size * size * 3];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < size; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < size; x++)
                {
                    var source = row[x];
                    var offset = (y * size + x) * 3;
                    pixels[offset] = source.B;
                    pixels[offset + 1] = source.G;
                    pixels[offset + 2] = source.R;
                }
            }
        });
        return pixels;
    }

    private static Label[] ReadLabels(string labelsPath)
    {
        if (string.IsNullOrWhiteSpace(labelsPath) || !File.Exists(labelsPath))
            throw new FileNotFoundException("The WD ViT selected_tags.csv file is missing.", labelsPath);
        using var reader = new StreamReader(labelsPath);
        if (reader.ReadLine() is not "tag_id,name,category,count")
            throw new InvalidDataException("The WD ViT labels have an unexpected header.");
        var labels = new List<Label>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var firstComma = line.IndexOf(',');
            var lastComma = line.LastIndexOf(',');
            if (firstComma <= 0 || lastComma <= firstComma)
                throw new InvalidDataException($"Invalid WD ViT label at row {labels.Count + 2}.");
            var categoryComma = line.LastIndexOf(',', lastComma - 1);
            if (categoryComma <= firstComma || lastComma <= categoryComma ||
                !long.TryParse(line.AsSpan(0, firstComma), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var id) ||
                !int.TryParse(line.AsSpan(categoryComma + 1, lastComma - categoryComma - 1),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var category))
                throw new InvalidDataException($"Invalid WD ViT label at row {labels.Count + 2}.");
            labels.Add(new Label(id, category));
        }
        if (labels.Count == 0)
            throw new InvalidDataException("The WD ViT labels are empty.");
        return labels.ToArray();
    }

    private readonly record struct Label(long DanbooruId, int Category);
}

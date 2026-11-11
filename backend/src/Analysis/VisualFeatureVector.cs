using System.Buffers.Binary;

namespace MyApp.Api.Analysis;

/// <summary>
/// A sparse visual feature vector: the tagger's per-label probabilities above a small floor, strongest first
/// capped, with indices in ascending order. Used as the input of learned custom-tag classifiers.
/// </summary>
public sealed class VisualFeatureVector
{
    public const int MaxEntries = 384;
    public const float MinValue = 0.02f;

    public VisualFeatureVector(int dimension, int[] indices, float[] values)
    {
        if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension));
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(values);
        if (indices.Length != values.Length)
            throw new ArgumentException("Indices and values must have the same length.");
        for (var i = 0; i < indices.Length; i++)
        {
            if (indices[i] < 0 || indices[i] >= dimension || (i > 0 && indices[i] <= indices[i - 1]))
                throw new ArgumentException("Feature indices must be ascending and within the dimension.");
            if (!float.IsFinite(values[i]))
                throw new ArgumentException("Feature values must be finite.");
        }
        Dimension = dimension;
        Indices = indices;
        Values = values;
    }

    public int Dimension { get; }
    public int[] Indices { get; }
    public float[] Values { get; }
    public int Count => Indices.Length;

    /// <summary>Keeps probabilities of at least <see cref="MinValue"/>, at most <see cref="MaxEntries"/> of them.</summary>
    public static VisualFeatureVector FromProbabilities(ReadOnlySpan<float> scores)
    {
        var kept = new List<(int Index, float Value)>();
        for (var index = 0; index < scores.Length; index++)
        {
            var score = scores[index];
            if (float.IsFinite(score) && score >= MinValue && score <= 1) kept.Add((index, score));
        }
        return FromEntries(scores.Length, kept);
    }

    /// <summary>Combines frames by the peak probability of each label.</summary>
    public static VisualFeatureVector? MaxOf(IEnumerable<VisualFeatureVector?> vectors)
    {
        Dictionary<int, float>? peaks = null;
        var dimension = 0;
        foreach (var vector in vectors)
        {
            if (vector is null) continue;
            if (peaks is null)
            {
                peaks = new Dictionary<int, float>();
                dimension = vector.Dimension;
            }
            else if (vector.Dimension != dimension)
                throw new InvalidOperationException("Frame feature vectors have different dimensions.");
            for (var i = 0; i < vector.Count; i++)
                if (!peaks.TryGetValue(vector.Indices[i], out var peak) || vector.Values[i] > peak)
                    peaks[vector.Indices[i]] = vector.Values[i];
        }
        return peaks is null ? null : FromEntries(dimension, peaks.Select(item => (item.Key, item.Value)));
    }

    private static VisualFeatureVector FromEntries(int dimension, IEnumerable<(int Index, float Value)> entries)
    {
        var kept = entries.OrderByDescending(item => item.Value).ThenBy(item => item.Index)
            .Take(MaxEntries).OrderBy(item => item.Index).ToArray();
        return new VisualFeatureVector(dimension, kept.Select(item => item.Index).ToArray(),
            kept.Select(item => item.Value).ToArray());
    }

    public double Dot(float[] weights)
    {
        double sum = 0;
        for (var i = 0; i < Indices.Length; i++) sum += weights[Indices[i]] * Values[i];
        return sum;
    }

    /// <summary>Little-endian pairs of int32 index and float32 value.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[Indices.Length * 8];
        for (var i = 0; i < Indices.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 8), Indices[i]);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8 + 4), Values[i]);
        }
        return bytes;
    }

    public static VisualFeatureVector FromBytes(int dimension, byte[] bytes)
    {
        if (bytes.Length % 8 != 0) throw new InvalidDataException("Invalid feature vector encoding.");
        var count = bytes.Length / 8;
        var indices = new int[count];
        var values = new float[count];
        for (var i = 0; i < count; i++)
        {
            indices[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 8));
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 8 + 4));
        }
        return new VisualFeatureVector(dimension, indices, values);
    }
}

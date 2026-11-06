namespace MyApp.Api.Analysis;

/// <summary>Recognition text stays transient and must not be logged or stored with the job.</summary>
public sealed record SpeechSegment(decimal StartSeconds, decimal EndSeconds, string Text);

public sealed record SpeechRecognitionResult(
    bool? SpeechDetected, bool? EnglishSpeechDetected, string? Language,
    decimal ProcessedSeconds, bool Truncated, string ModelKey,
    IReadOnlyList<SpeechSegment> Segments);

public interface ISpeechRecognizer
{
    Task<SpeechRecognitionResult> RecognizeAsync(
        string originalPath, double durationSeconds, CancellationToken cancellationToken);
}

using MyApp.Api.Analysis;

namespace MyApp.Api.Tests;

public sealed class WhisperCppSpeechRecognizerTests
{
    [Fact]
    public void EmptyTranscriptionLeavesSpeechAndLanguageUnknown()
    {
        const string json = """
            {"result":{"language":"en"},"transcription":[]}
            """;

        var result = WhisperCppSpeechRecognizer.ParseJson(json, 12.5m, truncated: false);

        Assert.Null(result.SpeechDetected);
        Assert.Null(result.EnglishSpeechDetected);
        Assert.Null(result.Language);
        Assert.Empty(result.Segments);
        Assert.Equal(12.5m, result.ProcessedSeconds);
        Assert.False(result.Truncated);
        Assert.Equal(WhisperCppSpeechRecognizer.PinnedModelKey, result.ModelKey);
    }

    [Fact]
    public void EnglishSegmentsUseMillisecondOffsetsAndPreserveMetadata()
    {
        const string json = """
            {
              "result":{"language":"en"},
              "transcription":[
                {"offsets":{"from":250,"to":1750},"text":" Hello there. "},
                {"offsets":{"from":2250,"to":4500},"text":"The second sentence."}
              ]
            }
            """;

        var result = WhisperCppSpeechRecognizer.ParseJson(json, 5m, truncated: true);

        Assert.True(result.SpeechDetected);
        Assert.True(result.EnglishSpeechDetected);
        Assert.Equal("en", result.Language);
        Assert.Equal(5m, result.ProcessedSeconds);
        Assert.True(result.Truncated);
        Assert.Equal(WhisperCppSpeechRecognizer.PinnedModelKey, result.ModelKey);
        Assert.Collection(result.Segments,
            segment => Assert.Equal(new SpeechSegment(0.25m, 1.75m, "Hello there."), segment),
            segment => Assert.Equal(new SpeechSegment(2.25m, 4.5m, "The second sentence."), segment));
    }

    [Fact]
    public void NonEnglishSpeechIsDetectedWithoutEnglishFlag()
    {
        const string json = """
            {"result":{"language":"uk"},"transcription":[{"offsets":{"from":1000,"to":2200},"text":"Привіт, світе."}]}
            """;

        var result = WhisperCppSpeechRecognizer.ParseJson(json, 3m, truncated: false);

        Assert.True(result.SpeechDetected);
        Assert.False(result.EnglishSpeechDetected);
        Assert.Equal("uk", result.Language);
        Assert.Equal(new SpeechSegment(1m, 2.2m, "Привіт, світе."), Assert.Single(result.Segments));
    }

    [Fact]
    public void MusicAndWhitespaceDoNotCountAsSpeech()
    {
        const string json = """
            {
              "result":{"language":"en"},
              "transcription":[
                {"offsets":{"from":0,"to":1000},"text":"   "},
                {"offsets":{"from":1000,"to":2000},"text":"[Music]"},
                {"offsets":{"from":2000,"to":3000},"text":"(music)"},
                {"offsets":{"from":3000,"to":4000},"text":"♪ instrumental ♪"}
              ]
            }
            """;

        var result = WhisperCppSpeechRecognizer.ParseJson(json, 4m, truncated: false);

        Assert.Null(result.SpeechDetected);
        Assert.Null(result.EnglishSpeechDetected);
        Assert.Null(result.Language);
        Assert.Empty(result.Segments);
    }

    [Theory]
    [InlineData("{\"transcription\":[{\"text\":\"private spoken words\"}")]
    [InlineData("{\"result\":{\"language\":\"en\"},\"text\":\"private spoken words\"}")]
    [InlineData("{\"result\":{\"language\":\"en\"},\"transcription\":\"private spoken words\"}")]
    public void InvalidOutputIsRejectedWithoutEchoingTranscript(string json)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            WhisperCppSpeechRecognizer.ParseJson(json, 3m, truncated: false));

        Assert.DoesNotContain("private spoken words", error.ToString(), StringComparison.Ordinal);
    }
}

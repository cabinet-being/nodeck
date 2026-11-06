using MyApp.Api.Analysis;

namespace MyApp.Api.Tests;

public sealed class SpeechTagMatcherTests
{
    [Fact]
    public void ExtractsExactTwoToFourWordPhrasesWithNormalizedSpacesAndSegmentTiming()
    {
        var candidates = SpeechTagMatcher.FindCandidates(
            [new SpeechSegment(1.25m, 2.75m, "Blue   hair\tred  dress")]);

        Assert.Contains(new SpeechPhraseCandidate("blue hair", 1.25m, 2.75m, 2), candidates);
        Assert.Contains(new SpeechPhraseCandidate("blue hair red", 1.25m, 2.75m, 3), candidates);
        Assert.Contains(new SpeechPhraseCandidate("blue hair red dress", 1.25m, 2.75m, 4), candidates);
        Assert.All(candidates, candidate => Assert.InRange(candidate.WordCount, 2, 4));
    }

    [Theory]
    [InlineData("blue hairstyle", "blue hair")]
    [InlineData("preblue hair", "blue hair")]
    [InlineData("blue hairpin", "blue hair")]
    [InlineData("blue hair2", "blue hair")]
    [InlineData("blue_hair", "blue hair")]
    public void DoesNotMatchInsideOtherTokens(string speech, string forbiddenPhrase)
    {
        Assert.DoesNotContain(SpeechTagMatcher.FindCandidates([new SpeechSegment(0, 1, speech)]),
            candidate => candidate.Phrase == forbiddenPhrase);
    }

    [Theory]
    [InlineData("not blue hair")]
    [InlineData("no blue hair")]
    [InlineData("never blue hair")]
    [InlineData("without blue hair")]
    [InlineData("The tag blue hair is spoken")]
    [InlineData("She said blue hair")]
    [InlineData("The phrase blue hair appears")]
    [InlineData("He wore \"blue hair\" today")]
    public void SuppressesNegatedQuotedAndMetaMentions(string speech)
    {
        Assert.DoesNotContain(SpeechTagMatcher.FindCandidates([new SpeechSegment(0, 1, speech)]),
            candidate => candidate.Phrase == "blue hair");
    }

    [Fact]
    public void ClauseBoundaryEndsNegationContext()
    {
        var candidates = SpeechTagMatcher.FindCandidates(
            [new SpeechSegment(0, 2, "not red eyes. Blue hair shines")]);

        Assert.Contains(candidates, candidate => candidate.Phrase == "blue hair");
        Assert.DoesNotContain(candidates, candidate => candidate.Phrase == "red eyes");
    }

    [Fact]
    public void CapsCandidatesAndSamplesSegmentsAcrossEntireTimelineDeterministically()
    {
        var longText = string.Join(' ', Enumerable.Range(0, 128)
            .Select(index => $"{(char)('a' + index / 26)}{(char)('a' + index % 26)}word"));
        var crowdedSegments = Enumerable.Range(0, 250)
            .Select(index => new SpeechSegment(index, index + 1, longText))
            .ToArray();
        var crowded = SpeechTagMatcher.FindCandidates(crowdedSegments);
        Assert.InRange(crowded.Count, 1, 500);
        Assert.Equal(crowded, SpeechTagMatcher.FindCandidates(crowdedSegments));
        Assert.Contains(crowded, candidate => candidate.StartSeconds == 249m);

        var segments = Enumerable.Range(0, 250)
            .Select(index => new SpeechSegment(index, index + 1, "blue hair"))
            .ToArray();
        var first = SpeechTagMatcher.FindCandidates(segments);
        var second = SpeechTagMatcher.FindCandidates(segments);
        Assert.Equal(100, first.Count);
        Assert.Equal(first, second);
        Assert.Equal(0m, first[0].StartSeconds);
        Assert.Equal(249m, first[^1].StartSeconds);
        Assert.Equal(100, first.Select(candidate => candidate.StartSeconds).Distinct().Count());
    }
}

using System.Text.RegularExpressions;

namespace MyApp.Api.Analysis;

public sealed record SpeechPhraseCandidate(
    string Phrase, decimal StartSeconds, decimal EndSeconds, int WordCount);

/// <summary>Extracts bounded phrases from transient speech; a catalog lookup must confirm every phrase.</summary>
public static class SpeechTagMatcher
{
    private const int MaxSegments = 100;
    private const int MaxCandidates = 500;
    private const int MaxCandidatesPerSegment = 64;
    private const int MaxTextLength = 4096;
    private const int MaxWordsPerSegment = 128;

    private static readonly Regex WordPattern = new(
        @"(?<![\p{L}\p{N}_])[A-Za-z]+(?:['’][A-Za-z]+)*(?![\p{L}\p{N}_])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "been", "being", "but", "by",
        "can", "could", "did", "do", "does", "for", "from", "had", "has", "have",
        "he", "her", "hers", "him", "his", "i", "in", "into", "is", "it", "its",
        "me", "my", "of", "on", "or", "our", "ours", "she", "so", "some", "than",
        "that", "the", "their", "them", "there", "these", "they", "this", "those",
        "to", "us", "was", "were", "what", "when", "where", "which", "who",
        "why", "will", "with", "would", "you", "your", "yours", "no", "not",
        "never", "without", "don't", "doesn't", "isn't", "can't", "dont", "doesnt",
        "isnt", "cant"
    };

    private static readonly HashSet<string> Negators = new(StringComparer.Ordinal)
    {
        "no", "not", "never", "without", "don't", "doesn't", "isn't", "can't",
        "dont", "doesnt", "isnt", "cant"
    };

    private static readonly HashSet<string> MetaWords = new(StringComparer.Ordinal)
    {
        "word", "words", "phrase", "phrases", "tag", "tags", "called", "mention",
        "mentions", "mentioned", "say", "says", "said"
    };

    public static IReadOnlyList<SpeechPhraseCandidate> FindCandidates(
        IReadOnlyList<SpeechSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var candidates = new List<SpeechPhraseCandidate>();
        var segmentCandidates = new List<List<SpeechPhraseCandidate>>();
        var count = Math.Min(segments.Count, MaxSegments);

        for (var sample = 0; sample < count; sample++)
        {
            var index = count == 1 ? 0 : (int)((long)sample * (segments.Count - 1) / (count - 1));
            var segment = segments[index];
            var source = segment.Text;
            if (string.IsNullOrWhiteSpace(source) || source.IndexOfAny(['"', '“', '”']) >= 0)
                continue;

            // Deliberate misses: long segments are sampled only at the start, and quoted
            // segments are discarded wholesale because quote spans can be ambiguous.
            var length = Math.Min(source.Length, MaxTextLength);
            if (length < source.Length)
            {
                while (length > 0 && IsWordPart(source[length])) length--;
            }
            var text = source[..length];
            var words = new List<Word>();
            var truncated = length < source.Length;
            var clause = 0;
            var previousEnd = 0;
            foreach (Match match in WordPattern.Matches(text))
            {
                if (words.Count == MaxWordsPerSegment)
                {
                    truncated = true;
                    break;
                }
                if (HasClauseBoundary(text.AsSpan(previousEnd, match.Index - previousEnd))) clause++;
                var value = match.Value.ToLowerInvariant().Replace('’', '\'');
                words.Add(new Word(value, clause));
                previousEnd = match.Index + match.Length;
            }

            var localCandidates = new List<SpeechPhraseCandidate>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var size = 2; size <= 4 && localCandidates.Count < MaxCandidatesPerSegment; size++)
            {
                for (var start = 0; start + size <= words.Count &&
                    localCandidates.Count < MaxCandidatesPerSegment; start++)
                {
                    if (Stopwords.Contains(words[start].Text) || words[start].Text.Length > 32 ||
                        HasEarlierNegator(words, start)) continue;
                    var end = start + size - 1;
                    if (words[end].Clause != words[start].Clause) continue;
                    // Keep the three-word context available for the meta-language check.
                    if (truncated && end >= words.Count - 3) continue;
                    if (Stopwords.Contains(words[end].Text) || words[end].Text.Length > 32 ||
                        HasMetaWordNearby(words, start, end)) continue;

                    var contentWords = 0;
                    var invalid = false;
                    for (var i = start; i <= end; i++)
                    {
                        if (words[i].Text.Length > 32 || Negators.Contains(words[i].Text))
                        {
                            invalid = true;
                            break;
                        }
                        if (!Stopwords.Contains(words[i].Text)) contentWords++;
                    }
                    if (invalid || contentWords < 2) continue;

                    var phrase = string.Join(' ', words.Skip(start).Take(size).Select(word => word.Text));
                    if (seen.Add(phrase))
                        localCandidates.Add(new SpeechPhraseCandidate(phrase, segment.StartSeconds,
                            segment.EndSeconds, size));
                }
            }
            if (localCandidates.Count > 0) segmentCandidates.Add(localCandidates);
        }

        for (var round = 0; candidates.Count < MaxCandidates; round++)
        {
            var added = false;
            foreach (var group in segmentCandidates)
            {
                if (round >= group.Count) continue;
                candidates.Add(group[round]);
                added = true;
                if (candidates.Count == MaxCandidates) break;
            }
            if (!added) break;
        }

        return candidates;
    }

    private static bool HasEarlierNegator(List<Word> words, int start)
    {
        for (var i = start - 1; i >= 0 && words[i].Clause == words[start].Clause; i--)
            if (Negators.Contains(words[i].Text)) return true;
        return false;
    }

    private static bool HasMetaWordNearby(List<Word> words, int start, int end)
    {
        for (var i = Math.Max(0, start - 3); i <= Math.Min(words.Count - 1, end + 3); i++)
            if (words[i].Clause == words[start].Clause && MetaWords.Contains(words[i].Text))
                return true;
        return false;
    }

    private static bool HasClauseBoundary(ReadOnlySpan<char> gap)
    {
        foreach (var character in gap)
            if (character is '.' or '!' or '?' or ';' or ':' or ',' or '\n' or '\r') return true;
        return false;
    }

    private static bool IsWordPart(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '\'' or '’';

    private readonly record struct Word(string Text, int Clause);
}

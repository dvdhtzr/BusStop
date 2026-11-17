using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BusStop.Engine.Evaluation;

/// <summary>
/// V7d-1/2: signals about already committed speech. This is not a moderator,
/// a semantic truth classifier, a new canon authority, or a participant context.
/// </summary>
public static class V7dProgressContract
{
    public const string Version = "busstop-v7d-progress-v1";
    public const string InitialDevelopmentTranscriptSha256 =
        "ee7d59bf51b05c900a03293dea41ef7e1082c5b2b9d1fa04def0de8671e3c2d5";
    public const int MaxTurns = 2000;
    public const int RecentLookback = 6;
    public const double LexicalOverlapThreshold = 0.72;
    public const int MinimumTokensForOverlap = 8;
    public const int LoopWindow = 6;
    public const int LoopCandidateMinimumSignals = 3;
}

public sealed record V7dInputTurn(
    long Sequence,
    string SpeakerId,
    string Text);

/// <summary>
/// PRIVATE turn index. A cue is not a violation and can be a deliberate quote,
/// callback or a productive clarification; positive labels require human review.
/// </summary>
public sealed record V7dPrivateSignal(
    long Sequence,
    string TextSha256,
    bool ExactPriorUtterance,
    long? ExactReferenceSequence,
    bool LexicalOverlapCandidate,
    long? LexicalReferenceSequence,
    double MaximumRecentTokenSimilarity,
    bool LoopWindowCandidate);

public sealed record V7dPrivateAnalysis(
    string ContractVersion,
    string TranscriptSha256,
    IReadOnlyList<V7dPrivateSignal> Signals);

/// <summary>
/// SAFE aggregate only: no utterance, speaker, sequence or case identifiers.
/// </summary>
public sealed record V7dSafeProgressReport(
    string ContractVersion,
    string TranscriptSha256,
    int TotalTurns,
    int DistinctSpeakers,
    int ExactCopyCandidates,
    int LexicalOverlapCandidates,
    int TurnsInsideCandidateLoopWindows,
    bool MatchesKnownDevelopmentTranscript,
    bool HumanReviewCompleted,
    bool SemanticAccuracyAccepted,
    bool BehavioralInterventionAuthorized,
    string Warning);

public static class V7dProgressAnalyzer
{
    private static readonly Regex Tokens = new(
        @"[\p{L}\p{N}]+", RegexOptions.Compiled |
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    public static string TextHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    public static V7dPrivateAnalysis Analyze(
        IReadOnlyList<V7dInputTurn> turns, string transcriptSha256)
    {
        ArgumentNullException.ThrowIfNull(turns);
        if (turns.Count == 0 || turns.Count > V7dProgressContract.MaxTurns ||
            !IsSha(transcriptSha256))
            throw new InvalidDataException("V7d requires a bounded, digested transcript.");

        var all = new List<(V7dInputTurn Turn, string Norm, HashSet<string> Tokens)>();
        var seenExact = new Dictionary<string, long>(StringComparer.Ordinal);
        var signals = new List<V7dPrivateSignal>(turns.Count);
        var overlapSignal = new List<bool>();

        foreach (var (turn, index) in turns.Select((t, i) => (t, i)))
        {
            if (turn is null || turn.Sequence != index + 1 ||
                string.IsNullOrWhiteSpace(turn.SpeakerId) ||
                turn.SpeakerId.Length > 128 ||
                turn.Text is null || turn.Text.Length is < 1 or > 20000)
                throw new InvalidDataException(
                    "V7d transcript contains an invalid or out-of-order turn.");

            var words = Tokens.Matches(turn.Text.ToLowerInvariant())
                .Cast<Match>().Select(m => m.Value).ToArray();
            // Ignore punctuation/case differences for candidate exact copies.
            // Empty normalization is never proof of duplicated speech.
            var normalized = string.Join(" ", words);
            var set = words.ToHashSet(StringComparer.Ordinal);
            long exactAt = 0;
            var exact = normalized.Length > 0 &&
                seenExact.TryGetValue(normalized, out exactAt);
            if (!exact && normalized.Length > 0)
                seenExact.Add(normalized, turn.Sequence);

            double maximum = 0;
            long? closest = null;
            // Compare only with earlier, bounded contextual speech, without
            // promoting keyword similarity into a semantic contradiction.
            foreach (var previous in all.TakeLast(V7dProgressContract.RecentLookback))
            {
                if (words.Length < V7dProgressContract.MinimumTokensForOverlap ||
                    previous.Tokens.Count < V7dProgressContract.MinimumTokensForOverlap ||
                    set.Count < 5 || previous.Tokens.Count < 5)
                    continue;

                var intersection = set.Count(w => previous.Tokens.Contains(w));
                var union = set.Count + previous.Tokens.Count - intersection;
                var ratio = union == 0 ? 0 : (double)intersection / union;
                if (ratio > maximum)
                {
                    maximum = ratio;
                    closest = previous.Turn.Sequence;
                }
            }
            // Detecting an exact copy does not depend on it being in the last
            // six turns. Avoid duplicating the exact cue as lexical overlap.
            var lexical = !exact &&
                maximum >= V7dProgressContract.LexicalOverlapThreshold;

            overlapSignal.Add(exact || lexical);
            var window = overlapSignal
                .TakeLast(V7dProgressContract.LoopWindow).Count(x => x);
            // A loop candidate is a coarse local pressure indicator, never
            // an automatic finding of bad or stalled conversation.
            var loop = overlapSignal.Count >= V7dProgressContract.LoopWindow &&
                window >= V7dProgressContract.LoopCandidateMinimumSignals;

            signals.Add(new V7dPrivateSignal(
                turn.Sequence, TextHash(turn.Text), exact,
                exact ? exactAt : null, lexical,
                lexical ? closest : null,
                Math.Round(maximum, 4), loop));
            all.Add((turn, normalized, set));
        }

        return new V7dPrivateAnalysis(
            V7dProgressContract.Version, transcriptSha256,
            Array.AsReadOnly(signals.ToArray()));
    }

    public static V7dSafeProgressReport ExportSafe(
        V7dPrivateAnalysis analysis, IReadOnlyList<V7dInputTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(turns);
        if (analysis.ContractVersion != V7dProgressContract.Version ||
            analysis.Signals.Count != turns.Count ||
            !IsSha(analysis.TranscriptSha256) ||
            analysis.Signals.Where((s, i) =>
                s.Sequence != i + 1 || s.TextSha256 != TextHash(turns[i].Text))
                .Any())
            throw new InvalidDataException(
                "V7d private signal set no longer matches the supplied transcript.");

        return new V7dSafeProgressReport(
            V7dProgressContract.Version, analysis.TranscriptSha256,
            turns.Count, turns.Select(x => x.SpeakerId).Distinct().Count(),
            analysis.Signals.Count(x => x.ExactPriorUtterance),
            analysis.Signals.Count(x => x.LexicalOverlapCandidate),
            analysis.Signals.Count(x => x.LoopWindowCandidate),
            analysis.TranscriptSha256 ==
                V7dProgressContract.InitialDevelopmentTranscriptSha256,
            HumanReviewCompleted: false,
            SemanticAccuracyAccepted: false,
            BehavioralInterventionAuthorized: false,
            "Exact/lexical/loop signals are REVIEW CANDIDATES. " +
            "Lexical overlap is not semantic stagnation; quoted, emotional " +
            "and deliberate repeated speech may be healthy. " +
            "The known 35-turn transcript is DEVELOPMENT, never a holdout.");
    }

    private static bool IsSha(string? value) =>
        value is { Length: 64 } &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BusStop.Domain;
using BusStop.Moderation.Verification;

namespace BusStop.Engine.Verification;

/// <summary>V3 extraction is an isolated observer, never a moderation judgment.</summary>
public enum V3ExtractionCoverage
{
    DeterministicOnly,
    ModelAugmented,
    ModelSkippedBySampling,
    ModelSkippedForBudget,
    ModelTransportFailure,
    ModelInvalidResponse,
    InputTruncated
}

public sealed record V3ExtractionResult(
    IReadOnlyList<ClaimCandidate> Claims,
    V3ExtractionCoverage Coverage,
    int ModelAttempts,
    int ScannedUtf16Characters,
    bool SelectionComplete,
    string Detail);

/// <summary>
/// Exact-copy detection requires ALREADY authorized dialogue evidence. It detects
/// repetition, NOT a contradiction, and sends no calls to Ollama.
/// </summary>
public sealed record V3ExactCopySignal(string EvidenceReference, string Text);

public static class V3ClaimDetector
{
    public const string RuleVersion = "claim-cues-v3-1";
    public const int MaxScannedCharacters = 12_000;
    public const int MaxCandidates = 24;

    private static readonly Regex NumbersAndDates = new(
        @"(?<![\w])(?:\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)(?:\s*(?:%|percent|million|billion|thousand|miles?|kilometers?|km|hours?|minutes?|days?|weeks?|years?|dollars?|people|outages?|flights?))?\b|\b(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2}(?:,\s*\d{4})?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PersonalOpening = new(
        @"\b(?:I|I've|I'm|my|we|our)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PersonalEvidence = new(
        @"\b(?:worked|work|was|am|were|have|had|grew|served|taught|lived|live|born|retired|employed|job|career|graduated|studied|father|mother|sister|brother|son|daughter|children|kids|married|wife|husband|family)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IdentityCue = new(
        @"\b(?:worked|work|job|career|retired|employed|pilot|teacher|engineer|nurse|doctor|machinist|occupation|profession)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SpeechAttribution = new(
        @"\b(?:you|he|she|they|[A-Z][a-z]+)\s+(?:said|told|claimed|mentioned|asked|stated|recalled)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Quotes = new(
        "[\\\"“][^\\\"”\\r\\n]{1,200}[\\\"”]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Conditional = new(
        @"\b(?:if|would|could|might|perhaps|suppose|imagine|hypothetically)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Humor = new(
        @"\b(?:kidding|joking|just\s+kidding|ha\s*ha|haha|lol)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record Cue(int Start, int Length, ClaimCategory Category);

    public static V3ExtractionResult Detect(VerificationJobIdentity job, Utterance utterance)
    {
        EvidenceVerificationValidator.ValidateJob(job, utterance);
        var text = utterance.Text ?? throw new InvalidDataException("Null committed text.");
        var scanned = Math.Min(text.Length, MaxScannedCharacters);
        var visible = text[..scanned];

        var found = new List<Cue>();
        foreach (Match m in NumbersAndDates.Matches(visible))
            Add(found, m.Index, m.Length, ClaimCategory.SourceFact);

        // A self-reference and a biographical predicate must occur in the
        // SAME clause. This creates a review candidate, not binding canon.
        foreach (var clause in Clauses(visible))
        {
            if (PersonalOpening.IsMatch(clause.Text) &&
                PersonalEvidence.IsMatch(clause.Text))
            {
                var category = IdentityCue.IsMatch(clause.Text)
                    ? ClaimCategory.CharacterIdentity : ClaimCategory.PersonalHistory;
                Add(found, clause.Start, clause.Text.Length, category);
            }

            if (SpeechAttribution.IsMatch(clause.Text))
                Add(found, clause.Start, clause.Text.Length, ClaimCategory.DialogueAttribution);
        }
        foreach (Match m in Quotes.Matches(visible))
            Add(found, m.Index, m.Length, ClaimCategory.DialogueAttribution);

        var selected = found.Distinct().OrderBy(x => x.Start)
            .ThenBy(x => x.Length).ThenBy(x => x.Category)
            .Take(MaxCandidates).ToArray();
        var claims = selected.Select((cue, index) => MakeCandidate(
                job, utterance, index, cue.Start, cue.Length, cue.Category,
                ClassifyMode(visible, cue.Start, cue.Length),
                ClaimExtractionProvenance.HostRule))
            .ToArray();
        EvidenceVerificationValidator.ValidateClaims(claims, job, utterance);

        bool complete = scanned == text.Length && found.Distinct().Count() <= MaxCandidates;
        return new V3ExtractionResult(Array.AsReadOnly(claims),
            complete ? V3ExtractionCoverage.DeterministicOnly : V3ExtractionCoverage.InputTruncated,
            0, scanned, complete,
            complete ? "Host-first cues only; semantic claim coverage is not exhaustive." :
                "Utterance scan or candidate count was capped; remaining claims are unexamined.");
    }

    public static V3ExactCopySignal? CheckExactCopy(string committedText,
        VerificationEvidenceScope authorizedDialogue)
    {
        if (committedText is null || authorizedDialogue?.AuthorizedItems is null)
            throw new InvalidDataException("Missing copied text or authorized dialogue.");
        return authorizedDialogue.AuthorizedItems.Values
            .Where(x => x.Kind == EvidenceItemKind.Utterance &&
                x.Visibility == EvidenceVisibility.SharedDialogue &&
                string.Equals(x.Excerpt, committedText, StringComparison.Ordinal))
            .OrderBy(x => x.ReferenceId, StringComparer.Ordinal)
            .Select(x => new V3ExactCopySignal(x.ReferenceId, x.Excerpt))
            .FirstOrDefault();
    }

    internal static ClaimCandidate MakeCandidate(
        VerificationJobIdentity job, Utterance utterance, int index, int start,
        int length, ClaimCategory category, ClaimRhetoricalMode mode,
        ClaimExtractionProvenance provenance)
    {
        if (start < 0 || length <= 0 || start > utterance.Text.Length ||
            length > utterance.Text.Length - start)
            throw new InvalidDataException("Claim offsets are outside the committed utterance.");
        var span = utterance.Text.Substring(start, length);
        // Stable IDs on same job for repeatable isolated extraction/recovery.
        var seed = job.JobId.ToString("N") + ":" + start + ":" + length +
            ":" + category + ":" + span;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var candidate = new ClaimCandidate(new Guid(digest.AsSpan(0, 16)),
            job.JobId, index, start, length, span, category, mode,
            provenance, ClaimValidationStatus.HostValidated);
        EvidenceVerificationValidator.ValidateClaim(candidate, job, utterance);
        return candidate;
    }

    internal static ClaimRhetoricalMode ClassifyMode(string text, int start, int length)
    {
        var segment = text.Substring(start, length);
        int left = start;
        while (left > 0 && text[left - 1] is not ('.' or '!' or '?' or ';' or '\n'))
            left--;
        int right = start + length;
        while (right < text.Length && text[right] is not ('.' or '!' or '?' or ';' or '\n'))
            right++;
        var sentence = text.Substring(left, right - left);

        // A number inside quotation marks is not a speaker-asserted fact.
        foreach (Match q in Quotes.Matches(sentence))
            if (left + q.Index <= start &&
                left + q.Index + q.Length >= start + length)
                return ClaimRhetoricalMode.Quoted;

        if (segment.Contains('?') || (right < text.Length && text[right] == '?'))
            return ClaimRhetoricalMode.Question;
        if (Humor.IsMatch(sentence)) return ClaimRhetoricalMode.Humorous;
        if (Conditional.IsMatch(sentence)) return ClaimRhetoricalMode.Conditional;
        return ClaimRhetoricalMode.Asserted;
    }

    private static IEnumerable<(int Start, string Text)> Clauses(string text)
    {
        var begin = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] is not ('.' or '!' or '?' or ';' or ',' or '\n'))
                continue;
            var end = i;
            while (begin < end && char.IsWhiteSpace(text[begin])) begin++;
            while (end > begin && char.IsWhiteSpace(text[end - 1])) end--;
            if (end > begin) yield return (begin, text.Substring(begin, end - begin));
            begin = i + 1;
        }
    }

    private static void Add(List<Cue> found, int start, int length, ClaimCategory category)
    {
        if (start >= 0 && length > 0)
            found.Add(new Cue(start, length, category));
    }
}

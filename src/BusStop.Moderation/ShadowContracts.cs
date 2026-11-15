using BusStop.Domain;
using System.Text.Json;

namespace BusStop.Moderation;

public enum ShadowReviewStatus
{
    Reviewed,
    InvalidResponse,
    TransportFailure,
    BudgetExhausted,
    Error
}

public enum ModeratorSeverity
{
    None,
    Low,
    Medium,
    High
}

/// <summary>Immutable snapshot from a completed authoritative SPEAK. Shadow processing
/// is strictly downstream of the commit and cannot alter speech or recollections.</summary>
public sealed record ShadowObservation(
    ModerationCandidate Candidate,
    UtteranceId CommittedUtterance,
    IReadOnlyList<Utterance> PriorCommittedHistory);

public interface IShadowModerationSink
{
    /// <summary>Never wait for inference, I/O, or queue space here.</summary>
    bool TryObserve(ShadowObservation observation);
}

public sealed record ShadowParsedJudgment(ModerationJudgment Judgment, ModeratorSeverity Severity);

public sealed record ShadowReviewRecord(
    ModerationCandidate Candidate,
    UtteranceId CommittedUtterance,
    string Trigger,
    IReadOnlyList<string> AuthorizedReferences,
    string ModelName,
    string ModelDigest,
    Guid? RequestId,
    ShadowReviewStatus Status,
    ModerationJudgment? Judgment,
    ModeratorSeverity Severity,
    string? RawResponse,
    string? FailureDetail,
    DateTimeOffset RecordedAt);

/// <summary>Strict JSON protocol parser. A malformed or out-of-scope result is an
/// audit failure, never an authorization to reject, revise, or promote speech.</summary>
public static class ShadowResponseParser
{
    public const string ContractVersion = "shadow-m1-v1";

    public static ShadowParsedJudgment Parse(
        string rawJson, ModerationCandidate candidate, ModerationEvidenceScope scope)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            RequiredString(root, "contractVersion") != ContractVersion)
            throw new InvalidDataException("Moderator response contract version mismatch.");
        if (!Guid.TryParse(RequiredString(root, "candidateId"), out var id) ||
            id != candidate.CandidateId)
            throw new InvalidDataException("Moderator candidate ID mismatch.");

        var disposition = StrictEnum<ModerationDisposition>(RequiredString(root, "disposition"));
        var severity = StrictEnum<ModeratorSeverity>(RequiredString(root, "severity"));
        ModerationFinding? finding = null;
        if (root.TryGetProperty("finding", out var item) && item.ValueKind != JsonValueKind.Null)
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Moderator finding must be an object.");

            var refs = item.GetProperty("evidenceReferences");
            if (refs.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Moderator evidence references must be an array.");
            var values = new List<string>();
            foreach (var entry in refs.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { } value)
                    throw new InvalidDataException("Invalid moderator evidence reference.");
                values.Add(value);
            }

            var uncertainty = item.GetProperty("uncertain");
            if (uncertainty.ValueKind != JsonValueKind.True &&
                uncertainty.ValueKind != JsonValueKind.False)
                throw new InvalidDataException("Uncertainty must be a boolean.");
            string? feedback = null;
            if (item.TryGetProperty("narrowFeedback", out var feedbackValue))
            {
                if (feedbackValue.ValueKind == JsonValueKind.String)
                    feedback = feedbackValue.GetString();
                else if (feedbackValue.ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException("Invalid private moderator feedback.");
            }

            finding = new ModerationFinding(
                StrictEnum<ModerationIssueKind>(RequiredString(item, "kind")),
                RequiredString(item, "claimSpan"),
                RequiredString(item, "explanation"),
                values.AsReadOnly(), uncertainty.GetBoolean(), feedback);
        }

        if (finding is null && severity != ModeratorSeverity.None)
            throw new InvalidDataException("Severity requires a finding.");

        var judgment = new ModerationJudgment(id, disposition, finding);
        ModerationContractValidator.Validate(candidate, scope, judgment);
        return new ShadowParsedJudgment(judgment, severity);
    }

    private static string RequiredString(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 } text)
            throw new InvalidDataException($"Missing or invalid string: {property}.");
        return text;
    }

    private static T StrictEnum<T>(string value) where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(typeof(T), parsed) ||
            !string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
            throw new InvalidDataException($"Unknown moderator enum value: {value}.");
        return parsed;
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BusStop.Moderation.Verification;

namespace BusStop.Moderation.Evaluation;

/// <summary>
/// V6a is a model-free, PRIVATE by default, synthetic-only developmental lab.
/// An evaluation score is never authorization to enable enforcement.
/// </summary>
public static class V6EvaluationContract
{
    public const string CorpusVersion = "busstop-v6a-provisional-synthetic-v1";
    public const string LabelStatus = "provisional-owner-review-required";
    public const string Partition = "development";
    public const string ScorerVersion = "busstop-v6a-scoring-v1";
    public const int MaxCases = 128;
    public const int MaxEvidencePerCase = 10;
    public const int MaxFixtureBytes = 256_000;
}

public sealed record V6SyntheticEvidence(
    string ReferenceId,
    EvidenceItemKind Kind,
    EvidenceAuthorityTier AuthorityTier,
    EvidenceVisibility Visibility,
    string Excerpt,
    bool IsSuperseded);

public sealed record V6LabeledCase(
    string CaseId,
    string ScenarioGroup,
    ClaimCategory Category,
    ClaimRhetoricalMode RhetoricalMode,
    string ClaimText,
    IReadOnlyList<V6SyntheticEvidence> Evidence,
    ClaimVerificationOutcome ExpectedOutcome,
    IReadOnlyList<string> ExpectedSupportingRefs,
    IReadOnlyList<string> ExpectedOpposingRefs,
    string Rationale);

public sealed record V6CorpusDocument(
    string CorpusVersion,
    string LabelStatus,
    string Partition,
    string Origin,
    IReadOnlyList<V6LabeledCase> Cases);

/// <summary>Validated immutable view + SHA-256 of the original UTF-8 fixture.</summary>
public sealed record V6CorpusFixture(
    V6CorpusDocument Document, string Sha256);

public enum V6SelectionDisposition
{
    NotSelected,
    Selected,
    Skipped,
    Dropped
}

/// <summary>
/// Data-only candidate observation. Untrusted reports must be validated against
/// explicit case ID, selection status, operational result and authorized refs.
/// Never include prompts, source transcripts, model raw JSON or private paths.
/// </summary>
public sealed record V6CaseObservation(
    string CaseId,
    V6SelectionDisposition Selection,
    VerificationOperationalStatus OperationalStatus,
    ClaimVerificationOutcome? Outcome,
    IReadOnlyList<string> SupportingReferences,
    IReadOnlyList<string> OpposingReferences,
    string ProblematicText,
    int ChargedModelCalls);

public static class V6CorpusReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// V6a deliberately does NOT ingest owner-reviewed or unseen holdout data.
    /// Review/freeze manifests and thresholds are later separately approved gates.
    /// </summary>
    public static V6CorpusFixture LoadProvisionalSynthetic(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length == 0 || bytes.Length > V6EvaluationContract.MaxFixtureBytes)
            throw new InvalidDataException("V6a corpus exceeds the immutable fixture size limit.");

        V6CorpusDocument doc;
        try
        {
            doc = JsonSerializer.Deserialize<V6CorpusDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("V6a corpus body is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed V6a corpus JSON or unknown field.", ex);
        }

        if (doc.CorpusVersion != V6EvaluationContract.CorpusVersion ||
            doc.LabelStatus != V6EvaluationContract.LabelStatus ||
            doc.Partition != V6EvaluationContract.Partition ||
            !Valid(doc.Origin, 1000) ||
            !doc.Origin.Contains("synthetic", StringComparison.OrdinalIgnoreCase) ||
            doc.Cases is null or { Count: < 1 or > V6EvaluationContract.MaxCases })
            throw new InvalidDataException(
                "V6a requires a version-pinned provisional development-only synthetic corpus.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in doc.Cases)
        {
            if (item is null ||
                !ValidId(item.CaseId) ||
                !ValidId(item.ScenarioGroup) ||
                !ids.Add(item.CaseId) ||
                !Enum.IsDefined(item.Category) ||
                !Enum.IsDefined(item.RhetoricalMode) ||
                !Enum.IsDefined(item.ExpectedOutcome) ||
                !Valid(item.ClaimText, 4000) ||
                !Valid(item.Rationale, 2000) ||
                item.Evidence is null or { Count: > V6EvaluationContract.MaxEvidencePerCase } ||
                item.ExpectedSupportingRefs is null ||
                item.ExpectedOpposingRefs is null)
                throw new InvalidDataException("V6a case has invalid or duplicated identity/labels.");

            var refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var evidence in item.Evidence)
            {
                if (evidence is null ||
                    !ValidId(evidence.ReferenceId) ||
                    !refs.Add(evidence.ReferenceId) ||
                    !Enum.IsDefined(evidence.Kind) ||
                    !Enum.IsDefined(evidence.AuthorityTier) ||
                    !Enum.IsDefined(evidence.Visibility) ||
                    !Valid(evidence.Excerpt, 4000) ||
                    (evidence.IsSuperseded !=
                        (evidence.AuthorityTier == EvidenceAuthorityTier.SupersededRecollection)))
                    throw new InvalidDataException("V6a synthetic evidence has invalid identity or authority.");
            }

            var supports = item.ExpectedSupportingRefs;
            var opposes = item.ExpectedOpposingRefs;
            if (supports.Count > refs.Count || opposes.Count > refs.Count ||
                supports.Count != supports.Distinct(StringComparer.Ordinal).Count() ||
                opposes.Count != opposes.Distinct(StringComparer.Ordinal).Count() ||
                supports.Concat(opposes).Any(reference => !refs.Contains(reference)) ||
                (item.ExpectedOutcome == ClaimVerificationOutcome.ConfirmedContradiction &&
                    opposes.Count == 0) ||
                (item.ExpectedOutcome == ClaimVerificationOutcome.ConflictingEvidence &&
                    (supports.Count == 0 || opposes.Count == 0)) ||
                (item.ExpectedOutcome == ClaimVerificationOutcome.InsufficientEvidence &&
                    (supports.Count > 0 || opposes.Count > 0)))
                throw new InvalidDataException("V6a expected citations are out of scope or inconsistent.");
        }

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        // All data returned as new JSON-deserialized instances; callers can
        // inspect but cannot silently promote labels via any V6a API.
        return new V6CorpusFixture(doc, sha);
    }

    private static bool Valid(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max;

    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, @"^[a-z0-9][a-z0-9-]{2,79}$",
            RegexOptions.CultureInvariant);
}

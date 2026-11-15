using BusStop.Domain;

namespace BusStop.Moderation;

/// <summary>Moderation is separate from fresh/continuing conversation mode.</summary>
public enum ModerationOperatingMode
{
    Disabled,
    Shadow,
    ExperimentalEnforcement,
    DefaultEnforcement
}

public enum ModerationDisposition
{
    Accept,
    Advisory,
    RequestRevision,
    Abstain,
    Failed
}

public enum ModerationIssueKind
{
    UnestablishedAutobiography,
    MisattributedExperience,
    UnsupportedFactualClaim,
    ContradictedFactualClaim,
    ExactDuplicate,
    SemanticRepetition,
    Other
}

public enum ModerationInferencePurpose
{
    RevisionReevaluation,
    MaterialViolationReview,
    StagnationReview,
    ShadowSampling
}

public enum ModerationAttemptStatus
{
    Reserved,
    Completed,
    Interrupted
}

/// <summary>
/// Frozen per-session moderation settings. The host validates and persists this
/// snapshot at session creation; changing process defaults does not change it.
/// Non-disabled values define future contracts, but M0 cannot execute them.
/// </summary>
public sealed record ModerationSessionPolicy(
    string ContractVersion,
    ModerationOperatingMode Mode,
    string RuleVersion,
    string PromptVersion,
    string? ModelName,
    string? ModelDigest,
    double? Temperature,
    bool? Think,
    int MaxModeratorCalls,
    int MaxRevisionsPerOpportunity,
    int StagnationWindow,
    int NudgeCooldownOpportunities)
{
    public const string CurrentContractVersion = "moderation-m0-v1";

    public static ModerationSessionPolicy Disabled { get; } = new(
        CurrentContractVersion, ModerationOperatingMode.Disabled,
        "disabled", "disabled", null, null, null, null,
        0, 0, 0, 0);

    public void Validate()
    {
        if (ContractVersion != CurrentContractVersion)
            throw new InvalidOperationException("Unsupported moderation policy contract version.");
        if (!Enum.IsDefined(typeof(ModerationOperatingMode), Mode))
            throw new InvalidOperationException("Unknown moderation operating mode.");
        if (string.IsNullOrWhiteSpace(RuleVersion) || string.IsNullOrWhiteSpace(PromptVersion) ||
            RuleVersion.Length > 128 || PromptVersion.Length > 128 ||
            MaxModeratorCalls < 0 || MaxRevisionsPerOpportunity is < 0 or > 1 ||
            StagnationWindow < 0 || NudgeCooldownOpportunities < 0)
            throw new InvalidOperationException("Invalid moderation policy limits or versions.");

        if (Mode == ModerationOperatingMode.Disabled)
        {
            if (ModelName is not null || ModelDigest is not null || Temperature is not null ||
                Think is not null || MaxModeratorCalls != 0 ||
                MaxRevisionsPerOpportunity != 0 || StagnationWindow != 0 ||
                NudgeCooldownOpportunities != 0)
                throw new InvalidOperationException("Disabled moderation cannot reserve model resources.");
        }
        else if (string.IsNullOrWhiteSpace(ModelName) ||
                 string.IsNullOrWhiteSpace(ModelDigest) ||
                 ModelName.Length > 256 || ModelDigest.Length > 256 ||
                 Temperature is null || !double.IsFinite(Temperature.Value) ||
                 Temperature.Value is < 0 or > 2 || MaxModeratorCalls == 0)
        {
            throw new InvalidOperationException("Active moderation requires a pinned model and positive budget.");
        }
    }
}

/// <summary>A private, uncommitted SPEAK proposal; never an authoritative utterance.</summary>
public sealed record ModerationCandidate(
    Guid CandidateId,
    SessionId Session,
    OpportunityId Opportunity,
    CharacterId Speaker,
    long CommittedBoundary,
    string Text);

/// <summary>
/// Host-derived authorization scope. Referenced evidence must have been visible
/// to this speaker, and a reference's existence is not proof of its interpretation.
/// </summary>
public sealed record ModerationEvidenceScope(
    SessionId Session,
    OpportunityId Opportunity,
    CharacterId Speaker,
    long CommittedBoundary,
    IReadOnlySet<string> AllowedEvidenceReferences);

public sealed record ModerationFinding(
    ModerationIssueKind Kind,
    string ClaimSpan,
    string Explanation,
    IReadOnlyList<string> EvidenceReferences,
    bool Uncertain,
    string? NarrowFeedback);

public sealed record ModerationJudgment(
    Guid CandidateId,
    ModerationDisposition Disposition,
    ModerationFinding? Finding);

/// <summary>Host-side shape and scope checks, not semantic proof of a model claim.</summary>
public static class ModerationContractValidator
{
    public static void Validate(
        ModerationCandidate candidate,
        ModerationEvidenceScope scope,
        ModerationJudgment judgment)
    {
        if (candidate.CandidateId == Guid.Empty || candidate.Session != scope.Session ||
            candidate.Opportunity != scope.Opportunity || candidate.Speaker != scope.Speaker ||
            candidate.CommittedBoundary != scope.CommittedBoundary ||
            candidate.CommittedBoundary < 0 || string.IsNullOrWhiteSpace(candidate.Text))
            throw new InvalidOperationException("Invalid moderation candidate identity or scope.");

        if (judgment.CandidateId != candidate.CandidateId ||
            !Enum.IsDefined(typeof(ModerationDisposition), judgment.Disposition))
            throw new InvalidOperationException("Moderator judgment does not match the candidate.");

        if (judgment.Disposition == ModerationDisposition.RequestRevision &&
            judgment.Finding is null)
            throw new InvalidOperationException("A revision requires a specific finding.");

        if (judgment.Finding is not { } finding)
            return;

        if (!Enum.IsDefined(typeof(ModerationIssueKind), finding.Kind) ||
            string.IsNullOrWhiteSpace(finding.ClaimSpan) ||
            !candidate.Text.Contains(finding.ClaimSpan, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(finding.Explanation) ||
            finding.EvidenceReferences is null ||
            finding.EvidenceReferences.Any(r =>
                string.IsNullOrWhiteSpace(r) || !scope.AllowedEvidenceReferences.Contains(r)) ||
            (judgment.Disposition == ModerationDisposition.RequestRevision &&
                (finding.Uncertain || finding.EvidenceReferences.Count == 0 ||
                 string.IsNullOrWhiteSpace(finding.NarrowFeedback))))
            throw new InvalidOperationException("Unsupported or out-of-scope moderation finding.");
    }
}

/// <summary>In M0 the only permitted operational implementation is DisabledModerator.</summary>
public interface IModerator
{
    ModerationJudgment Review(ModerationCandidate candidate, ModerationEvidenceScope scope);
}

public sealed class DisabledModerator : IModerator
{
    public static DisabledModerator Instance { get; } = new();
    private DisabledModerator() { }

    public ModerationJudgment Review(ModerationCandidate candidate, ModerationEvidenceScope scope)
    {
        var judgment = new ModerationJudgment(candidate.CandidateId, ModerationDisposition.Accept, null);
        ModerationContractValidator.Validate(candidate, scope, judgment);
        return judgment;
    }
}

public sealed record ModeratorInferenceReservation(
    Guid RequestId,
    SessionId Session,
    OpportunityId? Opportunity,
    ModerationInferencePurpose Purpose,
    ModerationAttemptStatus Status,
    DateTimeOffset ReservedAt);

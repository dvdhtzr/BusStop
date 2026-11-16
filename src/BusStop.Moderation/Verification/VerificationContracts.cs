using BusStop.Domain;

namespace BusStop.Moderation.Verification;

/// <summary>
/// Separate, experimental contract family. It does not reinterpret shadow-m1-v1,
/// enable enforcement, or alter the authoritative conversation path.
/// </summary>
public static class EvidenceVerifierV1
{
    public const string ContractVersion = "evidence-verifier-v1";
    public const string PacketSerializerVersion = "stj-evidence-packet-v1";
    public const int MaxAttemptsPerUtterance = 3;
}

public enum VerificationOperatingMode { Disabled, Shadow }
public enum ClaimCategory { SourceFact, CharacterIdentity, PersonalHistory, DialogueAttribution, Other }
public enum ClaimRhetoricalMode { Asserted, Quoted, Speculative, Conditional, Question, Humorous }
public enum ClaimExtractionProvenance { HostRule, ModelProposed }
public enum EvidenceItemKind { Briefing, Passage, BioAnchor, History, Utterance }
public enum EvidenceAuthorityTier
{
    BindingAnchor, EstablishedRecollection, ProvisionalRecollection,
    SupersededRecollection, SourceSnapshot, PreviouslyHeardUtterance
}
public enum EvidenceVisibility { ExposedToSpeaker, OwnHistory, SharedDialogue }
public enum EvidenceCompletenessStatus { CompleteForDeclaredScope, Partial, Unknown }
public enum ClaimVerificationOutcome
{
    ConfirmedContradiction, NoMaterialIssue, InsufficientEvidence, ConflictingEvidence
}
public enum VerificationOperationalStatus
{
    NotStarted, Completed, SkippedForBudget, InvalidResponse,
    TransportFailure, IncompatibleCheckpoint, Error
}
public enum VerificationCompletionStatus { Complete, Partial, Incomplete }

/// <summary>
/// Declaration for a FUTURE new-session opt-in only. This V1 type is not wired to
/// CreateSession, prompts, M1 policies, or any runtime worker.
/// </summary>
public sealed record VerificationSessionPolicy(
    string ContractVersion,
    VerificationOperatingMode Mode,
    string? ModelName,
    string? ModelDigest,
    double? Temperature,
    bool? Think,
    string RuleVersion,
    string PromptVersion,
    string RetrievalSelectorVersion,
    int MaxSessionAttempts,
    int MaxUtteranceAttempts)
{
    public static VerificationSessionPolicy Disabled { get; } = new(
        EvidenceVerifierV1.ContractVersion, VerificationOperatingMode.Disabled,
        null, null, null, null, "disabled", "disabled", "disabled", 0, 0);
}

/// <summary>Identity bound to a successfully committed utterance, never a draft.</summary>
public sealed record VerificationJobIdentity(
    Guid JobId,
    SessionId SessionId,
    OpportunityId OpportunityId,
    UtteranceId UtteranceId,
    CharacterId SpeakerId,
    long CommittedBoundary,
    string ContractVersion,
    string UtteranceSha256);

/// <summary>UTF-16 offsets into the exact committed utterance, not a normalized paraphrase.</summary>
public sealed record ClaimCandidate(
    Guid ClaimId,
    Guid JobId,
    int Index,
    int Start,
    int Length,
    string ClaimText,
    ClaimCategory Category,
    ClaimRhetoricalMode RhetoricalMode,
    ClaimExtractionProvenance ExtractionProvenance);

/// <summary>What the host has explicitly authorized at the original speech boundary.</summary>
public sealed record VerificationEvidenceScope(
    SessionId SessionId,
    OpportunityId OpportunityId,
    CharacterId SpeakerId,
    long AsOfBoundary,
    IReadOnlyDictionary<string, EvidenceItem> AuthorizedItems);

public sealed record EvidenceCompleteness(
    EvidenceCompletenessStatus Status,
    string DeclaredScope,
    string QuerySemantics,
    string? ExcludedRanges);

/// <summary>
/// An item's reference attests to identity and scope, NOT the truth of its content.
/// SourceSha256 is the source-record hash, not necessarily the excerpt hash.
/// </summary>
public sealed record EvidenceItem(
    string ReferenceId,
    EvidenceItemKind Kind,
    EvidenceAuthorityTier AuthorityTier,
    EvidenceVisibility Visibility,
    string SourceId,
    string SourceVersion,
    string SourceSha256,
    string Excerpt,
    long AvailableAtBoundary,
    bool IsSuperseded = false,
    string? SupersededByReference = null);

/// <summary>
/// Canonically serialized as an ordered record, with evidence in host-selected order.
/// No unordered maps are serialized. A newer serializer requires a new version.
/// </summary>
public sealed record EvidencePacketContent(
    string ContractVersion,
    string SerializerVersion,
    Guid JobId,
    Guid ClaimId,
    long AsOfBoundary,
    string RetrievalSelectorVersion,
    string PromptVersion,
    string SchemaVersion,
    EvidenceCompleteness Completeness,
    IReadOnlyList<EvidenceItem> Items);

/// <summary>Frozen packet. The envelope ID is stable; the body is content-addressed.</summary>
public sealed record EvidencePacket(
    Guid PacketId,
    EvidencePacketContent Content,
    string CanonicalJson,
    string Sha256);

/// <summary>
/// Citation sets distinguish relevant support from conflicting evidence. They
/// remain model assertions until the host checks scope and a semantic evaluation
/// determines actual relevance: scope validation alone is NOT semantic proof.
/// </summary>
public sealed record ClaimJudgment(
    Guid ClaimId,
    Guid PacketId,
    string PacketSha256,
    ClaimVerificationOutcome Outcome,
    IReadOnlyList<string> SupportingReferences,
    IReadOnlyList<string> OpposingReferences,
    string ProblematicText,
    string Explanation);

/// <summary>Operational failures are never translated into epistemic outcomes.</summary>
public sealed record VerificationClaimResult(
    Guid ClaimId,
    VerificationOperationalStatus Status,
    ClaimJudgment? Judgment,
    string? FailureDetail);

/// <summary>
/// Data-only contract; V4 will deterministically reconcile independent results.
/// No summarizing model call or runtime hookup is present in V1.
/// </summary>
public sealed record VerificationObservationSummary(
    Guid JobId,
    int IdentifiedClaims,
    int VerifiedClaims,
    int ConfirmedContradictions,
    int NoMaterialIssues,
    int InsufficientEvidence,
    int ConflictingEvidence,
    int SkippedForBudget,
    int FailedClaims,
    VerificationCompletionStatus CompletionStatus,
    string CoverageDescription);

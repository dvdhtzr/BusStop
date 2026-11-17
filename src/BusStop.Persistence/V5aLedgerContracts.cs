using BusStop.Moderation.Verification;

namespace BusStop.Persistence;

/// <summary>Private-only staged V5a ledger; these are not participant-facing DTOs.</summary>
public static class V5aLedgerContract
{
    public const string Version = "verifier-ledger-v5a-1";
    public const string EvaluationPrefix = "evidence-evaluation-";
    public const int MaxClaims = 24;
    public const int MaxPackets = 24;
    public const int MaxStageEvents = 96;
}

public enum V5aLedgerStage
{
    Registered,
    ClaimsSealed,
    PacketsSealed,
    AttemptLinked,
    ClaimResultRecorded,
    SummarySealed
}

public sealed record V5aLedgerEvent(
    Guid JobId,
    long Sequence,
    string EventKey,
    V5aLedgerStage Stage,
    string PayloadJson,
    string PayloadSha256,
    string? PreviousSha256,
    string EventSha256,
    DateTimeOffset RecordedAt);

public sealed record V5aAttemptLink(
    Guid RequestId,
    V4AttemptPhase Phase,
    IReadOnlyList<Guid> ClaimIds);

/// <summary>
/// Restored strictly from durable frozen records. A checkpoint is NEVER rebuilt
/// from current history, fresh retrieval or a newer model. V5b/V5c must first
/// validate worker ownership and compatibility before dispatching any inference.
/// </summary>
public sealed record V5aLedgerCheckpoint(
    VerificationJobIdentity Job,
    string EvaluationKey,
    V4SessionPolicy PinnedPolicy,
    string PolicySha256,
    IReadOnlyList<V5aLedgerEvent> Events,
    IReadOnlyList<ClaimCandidate> Claims,
    IReadOnlyList<EvidencePacket> Packets,
    IReadOnlyList<V5aAttemptLink> AttemptLinks,
    IReadOnlyList<VerificationClaimResult> ClaimResults,
    VerificationObservationSummary? Summary)
{
    public bool IsTerminal => Summary is not null;
}

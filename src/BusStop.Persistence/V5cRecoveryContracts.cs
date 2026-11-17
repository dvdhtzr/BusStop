using BusStop.Domain;

namespace BusStop.Persistence;

/// <summary>
/// V5c recovery is a PRIVATE, explicitly invoked operation. It never runs a
/// model, retrieves new evidence, alters committed speech, or starts a worker.
/// </summary>
public static class V5cContract
{
    public const string Version = "verifier-recovery-v5c-1";
    public const int MaxJobsPerBatch = 4;
    public const int MaxOrphansPerJob = 3;
}

public enum V5cRecoveryDisposition
{
    ReadyForFrozenVerification,
    ReadyForSummary,
    NeedsOrphanReconciliation,
    BlockedActiveAttempt,
    StoppedIncompatibleCheckpoint,
    StoppedMissingFrozenStage,
    StoppedMissingClaimPacket,
    StoppedUnknownAttemptOwner,
    StoppedNoRemainingBudget,
    AlreadyStopped,
    AlreadyTerminal
}

/// <summary>Exact pinned settings of the already deployed verifier binary.</summary>
public sealed record V5cRuntimePins(
    string ModelName, string ModelDigest,
    string RuleVersion, string ExtractionPromptVersion,
    string VerificationPromptVersion, string RetrievalSelectorVersion,
    string VerificationSchemaVersion);

public sealed record V5cRecoveryPlan(
    Guid JobId, V5cRecoveryDisposition Disposition,
    IReadOnlyList<Guid> PendingClaimIds,
    IReadOnlyList<Guid> ReservedRequestIds,
    int RemainingSessionCalls, int RemainingUtteranceCalls,
    int RemainingVerificationCalls, string ReasonCode)
{
    /// <summary>Plan only; V5c never dispatches a call automatically.</summary>
    public bool MayPlanNewInference =>
        Disposition == V5cRecoveryDisposition.ReadyForFrozenVerification;
}

/// <summary>
 /// Exactly what the current owner may do with this previously charged
 /// request. A reconciled request remains spent in the original V4 budget.
 /// </summary>
public sealed record V5cOrphanReconciliation(
    Guid JobId, Guid RequestId, long PriorEpoch, long RecoveryEpoch,
    Guid RecoveryOwnerId, string PolicySha256, string OutcomeCode);

public sealed record V5cStopRecord(
    Guid JobId, V5cRecoveryDisposition Disposition,
    string ReasonCode, long OwnerEpoch, Guid OwnerId);

using BusStop.Domain;

namespace BusStop.Moderation.Verification;

/// <summary>
/// Isolated V4 verification contract; V1 packet and claim formats stay immutable.
/// This does not change the historical M1 shadow-m1-v1 response contract.
/// </summary>
public static class EvidenceVerifierV4
{
    public const string ContractVersion = "evidence-verifier-v4-1";
    public const string VerificationPromptVersion = "claim-verification-prompt-v4-1";
    public const string VerificationSchemaVersion = "claim-verification-json-v4-1";
    public const int MaxVerificationCalls = 2;
    public const int MaxTotalCalls = 3;
    public const int MaxExtractionCalls = 1;
    public const int MaxClaimsPerBatch = 4;
}

/// <summary>
/// Host-pinned, shadow-only V4 settings for explicitly enrolled sessions.
/// No live worker reads this in V4. A missing persisted row means DISABLED,
/// never permission to infer from the older M1 moderation policy.
/// </summary>
public sealed record V4SessionPolicy(
    string ContractVersion,
    VerificationOperatingMode Mode,
    string RuleVersion,
    string ExtractionPromptVersion,
    string VerificationPromptVersion,
    string RetrievalSelectorVersion,
    string VerificationSchemaVersion,
    string ModelName,
    string ModelDigest,
    double Temperature,
    bool Think,
    int MaxSessionCalls,
    int MaxCallsPerUtterance,
    int MaxVerificationCallsPerUtterance,
    int MaxGeneratedTokens,
    int ContextTokens)
{
    public void Validate()
    {
        if (ContractVersion != EvidenceVerifierV4.ContractVersion ||
            Mode != VerificationOperatingMode.Shadow ||
            !Valid(RuleVersion) || !Valid(ExtractionPromptVersion) ||
            VerificationPromptVersion != EvidenceVerifierV4.VerificationPromptVersion ||
            !Valid(RetrievalSelectorVersion) ||
            VerificationSchemaVersion != EvidenceVerifierV4.VerificationSchemaVersion ||
            !Valid(ModelName, 256) || !Valid(ModelDigest, 256) ||
            !double.IsFinite(Temperature) || Temperature is < 0 or > 2 ||
            MaxSessionCalls < 1 ||
            MaxCallsPerUtterance is < 1 or > EvidenceVerifierV4.MaxTotalCalls ||
            MaxVerificationCallsPerUtterance is < 1 or > EvidenceVerifierV4.MaxVerificationCalls ||
            MaxGeneratedTokens is < 128 or > 4_096 ||
            ContextTokens is < 1_024 or > 16_384)
            throw new InvalidDataException("V4 requires explicit shadow-only, version-pinned and bounded policy.");
    }

    private static bool Valid(string? text, int max = 128) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= max;
}

public enum V4AttemptPhase { ClaimExtraction, ClaimVerification }

/// <summary>
/// Reservation counts are a durable view across every job for one utterance.
/// Every Reserved/Completed/Interrupted request counts; no refunds or reuse.
/// </summary>
public sealed record V4AttemptCounts(
    int SessionSharedAttempts,
    int UtteranceExtractionAttempts,
    int UtteranceVerificationAttempts)
{
    public int UtteranceTotalAttempts =>
        checked(UtteranceExtractionAttempts + UtteranceVerificationAttempts);
}

/// <summary>
/// Host-reconciled claim results, with no LLM summary or secret acceptance flag.
/// </summary>
/// <summary>
/// Private per-attempt troubleshooting metadata. The model response body is
/// deliberately excluded: owner audit can identify structural failures without
/// copying untrusted source text into logs or participant-visible records.
/// </summary>
public sealed record V4AttemptDiagnostic(
    Guid RequestId,
    VerificationOperationalStatus Status,
    string Detail,
    string? DoneReason,
    int? PromptEvalCount,
    int? EvalCount,
    int ResponseCharacterCount,
    string? ResponseSha256);

public sealed record V4ReviewReport(
    VerificationJobIdentity Job,
    IReadOnlyList<VerificationClaimResult> ClaimResults,
    VerificationObservationSummary Summary,
    int VerificationAttemptsThisRun,
    IReadOnlyList<Guid> ChargedRequestIds,
    bool HasMaterialContradiction)
{
    public IReadOnlyList<V4AttemptDiagnostic> AttemptDiagnostics { get; init; } =
        Array.Empty<V4AttemptDiagnostic>();
}

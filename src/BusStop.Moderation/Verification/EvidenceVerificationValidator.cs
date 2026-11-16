using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Domain;

namespace BusStop.Moderation.Verification;

/// <summary>
/// Model-free host checks for V1. They establish identity, integrity and evidence
/// authorization, not whether an inference is semantically correct.
/// </summary>
public static class EvidenceVerificationValidator
{
    // Safety caps for the CONTRACT layer only; V3 must benchmark and version the
    // production selection/token ceilings before an integrated worker is enabled.
    private const int MaxPacketItems = 32;
    private const int MaxPacketUtf8Bytes = 131_072;
    private const int MaxExcerptLength = 8_192;

    public static void ValidatePolicy(VerificationSessionPolicy policy)
    {
        Require(policy is not null, "Missing verifier policy.");
        Require(policy.ContractVersion == EvidenceVerifierV1.ContractVersion,
            "Incompatible verifier contract.");
        Require(Enum.IsDefined(typeof(VerificationOperatingMode), policy.Mode),
            "Unsupported verifier mode.");
        Require(Version(policy.RuleVersion) && Version(policy.PromptVersion) &&
                Version(policy.RetrievalSelectorVersion), "Invalid pinned verifier versions.");

        if (policy.Mode == VerificationOperatingMode.Disabled)
        {
            Require(policy.ModelName is null && policy.ModelDigest is null &&
                    policy.Temperature is null && policy.Think is null &&
                    policy.MaxSessionAttempts == 0 && policy.MaxUtteranceAttempts == 0 &&
                    policy.RuleVersion == "disabled" && policy.PromptVersion == "disabled" &&
                    policy.RetrievalSelectorVersion == "disabled",
                "Disabled verifier must not reserve resources or activate a model.");
            return;
        }

        Require(Version(policy.ModelName, 256) && Version(policy.ModelDigest, 256) &&
                policy.Temperature is { } temperature && double.IsFinite(temperature) &&
                temperature is >= 0 and <= 2 &&
                policy.Think.HasValue &&
                policy.MaxSessionAttempts > 0 &&
                policy.MaxUtteranceAttempts is > 0 and <= EvidenceVerifierV1.MaxAttemptsPerUtterance,
            "Shadow verifier needs pinned settings and bounded budgets.");
    }

    public static void ValidateJob(VerificationJobIdentity job, Utterance committed)
    {
        Require(job is not null && committed is not null, "Missing verifier job or committed utterance.");
        Require(job.JobId != Guid.Empty && job.ContractVersion == EvidenceVerifierV1.ContractVersion,
            "Invalid verifier job ID or contract.");
        Require(job.SessionId == committed.Session &&
                job.OpportunityId == committed.Opportunity &&
                job.UtteranceId == committed.Id &&
                job.SpeakerId == committed.Speaker &&
                job.CommittedBoundary >= 0 &&
                committed.Sequence > 0 &&
                job.CommittedBoundary == committed.Sequence - 1,
            "Verifier job does not match committed speech and boundary.");
        Require(IsSha256(job.UtteranceSha256) &&
                job.UtteranceSha256 == Sha256Hex(committed.Text),
            "Committed utterance hash mismatch.");
    }

    public static void ValidateClaim(
        ClaimCandidate claim, VerificationJobIdentity job, Utterance committed)
    {
        ValidateJob(job, committed);
        Require(claim is not null && claim.ClaimId != Guid.Empty &&
                claim.JobId == job.JobId && claim.Index >= 0,
            "Invalid claim identity.");
        Require(Enum.IsDefined(typeof(ClaimCategory), claim.Category) &&
                Enum.IsDefined(typeof(ClaimRhetoricalMode), claim.RhetoricalMode) &&
                Enum.IsDefined(typeof(ClaimExtractionProvenance), claim.ExtractionProvenance) &&
                Enum.IsDefined(typeof(ClaimValidationStatus), claim.ValidationStatus),
            "Unknown claim classification.");
        Require(claim.Start >= 0 && claim.Length > 0 &&
                claim.Start <= committed.Text.Length &&
                claim.Length <= committed.Text.Length - claim.Start &&
                claim.ClaimText is not null &&
                claim.ClaimText.Length == claim.Length &&
                string.CompareOrdinal(committed.Text, claim.Start,
                    claim.ClaimText, 0, claim.Length) == 0,
            "Claim span must be the exact committed UTF-16 substring.");
    }

    /// <summary>
    /// Validate the full claim set without discarding overlapping but distinct
    /// propositions. Claim IDs and indices must identify one candidate each.
    /// </summary>
    public static void ValidateClaims(
        IReadOnlyList<ClaimCandidate> claims,
        VerificationJobIdentity job, Utterance committed)
    {
        Require(claims is not null, "Missing extracted claim set.");
        var ids = new HashSet<Guid>();
        var indices = new HashSet<int>();
        foreach (var claim in claims)
        {
            ValidateClaim(claim, job, committed);
            Require(ids.Add(claim.ClaimId) && indices.Add(claim.Index),
                "Duplicate claim ID or claim index.");
        }
    }

    public static void ValidatePacket(
        EvidencePacket packet,
        ClaimCandidate claim,
        VerificationJobIdentity job,
        Utterance committed,
        VerificationEvidenceScope scope)
    {
        ValidateClaim(claim, job, committed);
        Require(packet is not null && packet.PacketId != Guid.Empty &&
                packet.Content is not null && scope is not null &&
                scope.AuthorizedItems is not null, "Missing evidence packet or host-issued scope.");
        Require(scope.SessionId == job.SessionId &&
                scope.OpportunityId == job.OpportunityId &&
                scope.SpeakerId == job.SpeakerId &&
                scope.AsOfBoundary == job.CommittedBoundary,
            "Evidence scope is for another speaker, session or speech boundary.");

        var body = packet.Content;
        Require(body.ContractVersion == EvidenceVerifierV1.ContractVersion &&
                body.SerializerVersion == EvidenceVerifierV1.PacketSerializerVersion &&
                body.JobId == job.JobId && body.ClaimId == claim.ClaimId &&
                body.AsOfBoundary == job.CommittedBoundary &&
                Version(body.RetrievalSelectorVersion) &&
                Version(body.PromptVersion) && Version(body.SchemaVersion),
            "Evidence packet identity or pinned versions mismatch.");

        Require(body.Completeness is not null &&
                Enum.IsDefined(typeof(EvidenceCompletenessStatus), body.Completeness.Status) &&
                Version(body.Completeness.DeclaredScope, 512) &&
                Version(body.Completeness.QuerySemantics, 1_024),
            "Missing or invalid evidence completeness statement.");
        Require(body.Completeness.ExcludedRanges is null ||
                body.Completeness.ExcludedRanges.Length <= 1_024,
            "Unbounded excluded-range explanation.");
        Require(body.Completeness.Status != EvidenceCompletenessStatus.CompleteForDeclaredScope ||
                string.IsNullOrEmpty(body.Completeness.ExcludedRanges),
            "A complete search cannot silently report excluded ranges.");
        Require(body.Items is not null && body.Items.Count <= MaxPacketItems,
            "Evidence packet item count exceeds contract safety cap.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in body.Items)
        {
            Require(item is not null &&
                    Version(item.ReferenceId, 512) &&
                    Enum.IsDefined(typeof(EvidenceItemKind), item.Kind) &&
                    Enum.IsDefined(typeof(EvidenceAuthorityTier), item.AuthorityTier) &&
                    Enum.IsDefined(typeof(EvidenceVisibility), item.Visibility) &&
                    Version(item.SourceId, 512) &&
                    Version(item.SourceVersion) &&
                    IsSha256(item.SourceSha256) &&
                    item.Excerpt is not null && item.Excerpt.Length <= MaxExcerptLength &&
                    item.AvailableAtBoundary >= 0 &&
                    item.AvailableAtBoundary <= job.CommittedBoundary &&
                    (!item.IsSuperseded || Version(item.SupersededByReference, 512)) &&
                    (item.IsSuperseded || item.SupersededByReference is null) &&
                    seen.Add(item.ReferenceId),
                "Invalid, future-dated or duplicate evidence item.");

            Require(scope.AuthorizedItems.TryGetValue(item.ReferenceId, out var authorized) &&
                    item == authorized,
                "Evidence reference, content, provenance or visibility is not host-authorized.");
        }

        Require(packet.CanonicalJson is not null &&
                Encoding.UTF8.GetByteCount(packet.CanonicalJson) <= MaxPacketUtf8Bytes &&
                packet.CanonicalJson == EvidencePacketFactory.Serialize(body) &&
                IsSha256(packet.Sha256) &&
                packet.Sha256 == Sha256Hex(packet.CanonicalJson),
            "Noncanonical, mutated or incorrectly hashed evidence packet.");
    }

    public static void ValidateJudgment(
        ClaimJudgment judgment, EvidencePacket packet,
        ClaimCandidate claim, VerificationJobIdentity job,
        Utterance committed, VerificationEvidenceScope scope)
    {
        ValidatePacket(packet, claim, job, committed, scope);
        Require(judgment is not null &&
                judgment.ClaimId == claim.ClaimId &&
                judgment.PacketId == packet.PacketId &&
                judgment.PacketSha256 == packet.Sha256 &&
                Enum.IsDefined(typeof(ClaimVerificationOutcome), judgment.Outcome),
            "Judgment identity, packet hash or epistemic outcome mismatch.");
        Require(Version(judgment.Explanation, 512) &&
                judgment.ProblematicText is not null &&
                judgment.ProblematicText.Length <= claim.ClaimText.Length &&
                (judgment.ProblematicText.Length == 0 ||
                 claim.ClaimText.Contains(judgment.ProblematicText, StringComparison.Ordinal)),
            "Judgment text must identify an exact portion of the claim.");

        var refs = new HashSet<string>(
            packet.Content.Items.Select(i => i.ReferenceId), StringComparer.Ordinal);
        ValidateCitations(judgment.SupportingReferences, refs);
        ValidateCitations(judgment.OpposingReferences, refs);

        if (judgment.Outcome == ClaimVerificationOutcome.ConfirmedContradiction)
            Require(judgment.OpposingReferences.Count > 0 &&
                    judgment.ProblematicText.Length > 0,
                "A confirmed contradiction requires a specific span and cited opposing evidence.");
        if (judgment.Outcome == ClaimVerificationOutcome.ConflictingEvidence)
            Require(judgment.SupportingReferences.Count > 0 &&
                    judgment.OpposingReferences.Count > 0 &&
                    judgment.SupportingReferences.Except(judgment.OpposingReferences,
                        StringComparer.Ordinal).Any() &&
                    judgment.OpposingReferences.Except(judgment.SupportingReferences,
                        StringComparer.Ordinal).Any(),
                "Conflicting evidence requires distinct supporting and opposing sources.");
    }

    public static void ValidateClaimResult(
        VerificationClaimResult result, EvidencePacket packet,
        ClaimCandidate claim, VerificationJobIdentity job,
        Utterance committed, VerificationEvidenceScope scope)
    {
        Require(result is not null && result.ClaimId == claim.ClaimId &&
                Enum.IsDefined(typeof(VerificationOperationalStatus), result.Status),
            "Unknown result status or claim identity.");
        Require(result.FailureDetail is null || result.FailureDetail.Length <= 512,
            "Unbounded failure detail.");
        if (result.Status == VerificationOperationalStatus.Completed)
        {
            Require(result.Judgment is not null && result.FailureDetail is null,
                "Completed operation requires a judgment, without an error.");
            ValidateJudgment(result.Judgment, packet, claim, job, committed, scope);
        }
        else
        {
            Require(result.Judgment is null,
                "Uncompleted or failed operation cannot carry an epistemic judgment.");
        }
    }

    public static void ValidateSummary(VerificationObservationSummary summary)
    {
        Require(summary is not null && summary.JobId != Guid.Empty &&
                Enum.IsDefined(typeof(VerificationCompletionStatus), summary.CompletionStatus) &&
                Version(summary.CoverageDescription, 1_024),
            "Invalid summary identity, completion or coverage.");
        var counts = new[] { summary.IdentifiedClaims, summary.VerifiedClaims,
            summary.ConfirmedContradictions, summary.NoMaterialIssues,
            summary.InsufficientEvidence, summary.ConflictingEvidence,
            summary.SkippedForBudget, summary.FailedClaims };
        Require(counts.All(x => x >= 0) &&
                (long)summary.ConfirmedContradictions + summary.NoMaterialIssues +
                    summary.InsufficientEvidence + summary.ConflictingEvidence ==
                    summary.VerifiedClaims &&
                (long)summary.VerifiedClaims + summary.SkippedForBudget +
                    summary.FailedClaims <= summary.IdentifiedClaims,
            "Inconsistent verification summary counts.");
        if (summary.CompletionStatus == VerificationCompletionStatus.Complete)
            Require(summary.VerifiedClaims == summary.IdentifiedClaims &&
                    summary.SkippedForBudget == 0 && summary.FailedClaims == 0,
                "Complete summary cannot hide unverified claims.");
    }

    private static void ValidateCitations(
        IReadOnlyList<string> citations, IReadOnlySet<string> allowed)
    {
        Require(citations is not null && citations.Count <= 8 &&
                citations.All(r => r is not null && allowed.Contains(r)) &&
                citations.Distinct(StringComparer.Ordinal).Count() == citations.Count,
            "Duplicate or unauthorized judgment reference.");
    }

    private static bool Version(string? value, int maxLength = 128) =>
        value is { Length: > 0 } &&
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Require(bool condition, string detail)
    {
        if (!condition)
            throw new InvalidDataException(detail);
    }

    internal static string Hash(string value) => Sha256Hex(value);
}

/// <summary>
/// A deterministic, model-free freeze operation. Only the content body is hashed;
/// packet IDs distinguish observations of identical content. A future version
/// must be introduced if serializer behavior or field order changes.
/// </summary>
public static class EvidencePacketFactory
{
    public static EvidencePacket Freeze(Guid packetId, EvidencePacketContent content)
    {
        if (packetId == Guid.Empty || content is null)
            throw new InvalidDataException("Packet ID and content are required.");
        if (content.Items is null)
            throw new InvalidDataException("Packet evidence collection is required.");
        // Detach from caller-owned lists/arrays. A frozen evidence packet cannot
        // silently acquire a different item after the hash was calculated.
        var frozen = content with
        {
            Items = Array.AsReadOnly(content.Items.ToArray())
        };
        var json = Serialize(frozen);
        return new EvidencePacket(
            packetId, frozen, json, EvidenceVerificationValidator.Hash(json));
    }

    internal static string Serialize(EvidencePacketContent content) =>
        JsonSerializer.Serialize(content);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BusStop.Moderation.Verification;

namespace BusStop.Moderation.Evaluation;

/// <summary>
/// V7a validates human-attested review inputs and preregistration only.
/// It cannot grade model output, certify reviewers, expose holdout items, or
/// authorize participant-facing enforcement.
/// </summary>
public static class V7FreezeContract
{
    public const string ReviewVersion = "busstop-v7a-review-v1";
    public const string ThresholdVersion = "busstop-v7a-thresholds-v1";
    public const string ApprovalVersion = "busstop-v7a-freeze-approval-v1";
    // V7b will implement this separate owner-reviewed scoring contract;
    // never reuse V6a's provisional scorer for release readiness.
    public const string PlannedReviewedScorerVersion =
        "busstop-v7b-reviewed-scoring-v1";
    public const string LabelStatus = "owner-reviewed-attested";
    public const string ReviewMethod = "independent-human-adjudication";
    public const int MaxDocumentBytes = 512_000;
    public const int MaxCases = 512;
    public const int MaxEvidence = 32;
    public const string ApprovalDeclaration =
        "I reviewed the labels and evidence, kept the holdout unseen during model tuning, " +
        "and approved these criteria before observing holdout model results.";
}

public enum V7Partition { Development, Holdout }

public sealed record V7ReviewEvidence(
    string ReferenceId,
    EvidenceItemKind Kind,
    EvidenceAuthorityTier AuthorityTier,
    EvidenceVisibility Visibility,
    string Excerpt,
    bool IsSuperseded);

public sealed record V7ReviewedCase(
    string CaseId,
    string ScenarioGroup,
    ClaimCategory Category,
    ClaimRhetoricalMode RhetoricalMode,
    string UtteranceText,
    int ClaimStartUtf16,
    int ClaimLengthUtf16,
    bool ShouldExtractClaim,
    string ClaimText,
    IReadOnlyList<V7ReviewEvidence> Evidence,
    ClaimVerificationOutcome ReviewedOutcome,
    IReadOnlyList<string> SupportingReferences,
    IReadOnlyList<string> OpposingReferences,
    string ReviewRationale,
    string ReviewerId,
    DateTimeOffset ReviewedAtUtc);

public sealed record V7ReviewedCorpus(
    string Version,
    string LabelStatus,
    V7Partition Partition,
    string ReviewMethod,
    string SourceDeclaration,
    IReadOnlyList<V7ReviewedCase> Cases);

/// <summary>
/// Explicit owner-entered rules. No default numerical targets are chosen by
/// V7a. A lane with no confirmed-positive examples requires an explanation
/// and cannot claim precision or recall on such cases.
/// </summary>
public sealed record V7LaneCriterion(
    string Lane,
    int MinimumIndependentCases,
    int MinimumCompletedCases,
    int MinimumConfirmedPositiveCases,
    decimal MinimumSelectionCoverage,
    decimal MaximumConfirmedFalsePositiveRate,
    decimal? MinimumConfirmedPrecision,
    decimal? MinimumEndToEndConfirmedRecall,
    decimal MinimumEvidenceSupportRate,
    string? NotApplicableReason);

public sealed record V7IntegrityRequirements(
    bool NoUnauthorizedEvidence,
    bool NoUnchargedModelAttempts,
    bool NoSpeechOrParticipantContextMutation,
    bool NoCorruptRecoveryPromotion,
    bool NoPrivateHoldoutLeakage,
    bool NoNonlocalVerifierInference,
    bool NoEnforcement);

public sealed record V7AcceptanceCriteria(
    string Version,
    string ModelName,
    string ModelDigest,
    string VerifierPromptVersion,
    string VerificationSchemaVersion,
    string ScorerVersion,
    int MinimumIdenticalInputRuns,
    int MinimumNaturalSessions,
    int MinimumCrashScenarios,
    decimal MinimumClaimSpanPrecision,
    decimal MinimumClaimSpanRecall,
    decimal MinimumRhetoricalModeAccuracy,
    decimal MinimumInsufficientEvidenceRecall,
    decimal MinimumConflictingEvidenceRecall,
    decimal MaximumUnscorableRate,
    decimal MaximumQueueDropRate,
    decimal MaximumTechnicalFailureRate,
    int MaximumVerifierP95LatencyMilliseconds,
    decimal MaximumParticipantLatencyRegressionFraction,
    IReadOnlyList<V7LaneCriterion> Lanes,
    V7IntegrityRequirements HardGates,
    string HumanQualityReviewProcedure,
    string ThresholdRationale);

public sealed record V7OwnerFreezeApproval(
    string Version,
    string OwnerId,
    DateTimeOffset ApprovedAtUtc,
    string DevelopmentSha256,
    string HoldoutSha256,
    string CriteriaSha256,
    string Declaration);

/// <summary>Unsigned, pre-approval commitments only; no review acceptance.</summary>
public sealed record V7FreezePreparation(
    string ReviewVersion, string CriteriaVersion,
    string DevelopmentSha256, string HoldoutSha256, string CriteriaSha256,
    int DevelopmentCases, int HoldoutCases,
    bool HumanApprovalReceived,
    bool SemanticAcceptanceGranted,
    bool EnforcementAuthorized);

public sealed record V7FreezeReceipt(
    string Version,
    string DevelopmentSha256,
    string HoldoutSha256,
    string CriteriaSha256,
    string ApprovalSha256,
    int DevelopmentCases,
    int HoldoutCases,
    string FreezeState,
    bool SemanticAcceptanceGranted,
    bool EnforcementAuthorized,
    string Limitations);

/// <summary>
/// Approved private evaluation input. Only the V7a exact-byte validation
/// can create it: no public constructor or mutable collections are exposed.
/// An owner attestation is still NOT an independently verified signature.
/// </summary>
public sealed class V7ApprovedEvaluationInputs
{
    internal V7ApprovedEvaluationInputs(
        V7ReviewedCorpus development, V7ReviewedCorpus holdout,
        V7AcceptanceCriteria criteria, V7FreezeReceipt receipt)
    {
        Development = development;
        Holdout = holdout;
        Criteria = criteria;
        Receipt = receipt;
    }

    public V7ReviewedCorpus Development { get; }
    public V7ReviewedCorpus Holdout { get; }
    public V7AcceptanceCriteria Criteria { get; }
    public V7FreezeReceipt Receipt { get; }
}

public static class V7ReviewFreezeValidator
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    /// <summary>
    /// No V6a provisional conversion exists. Input must be explicitly
    /// human-adjudicated, with separate independently-authored holdout.
    /// This validates self-attested review records but cannot independently
    /// establish that a human actually reviewed them.
    /// </summary>
    public static V7FreezePreparation Prepare(
        string developmentJson, string holdoutJson, string criteriaJson)
    {
        var development = Read<V7ReviewedCorpus>(developmentJson,
            "version", "labelStatus", "partition", "reviewMethod",
            "sourceDeclaration", "cases");
        var holdout = Read<V7ReviewedCorpus>(holdoutJson,
            "version", "labelStatus", "partition", "reviewMethod",
            "sourceDeclaration", "cases");
        var criteria = Read<V7AcceptanceCriteria>(criteriaJson,
            "version", "modelName", "modelDigest", "verifierPromptVersion",
            "verificationSchemaVersion", "scorerVersion",
            "minimumIdenticalInputRuns", "minimumNaturalSessions",
            "minimumCrashScenarios", "minimumClaimSpanPrecision",
            "minimumClaimSpanRecall", "minimumRhetoricalModeAccuracy",
            "minimumInsufficientEvidenceRecall",
            "minimumConflictingEvidenceRecall",
            "maximumUnscorableRate", "maximumQueueDropRate",
            "maximumTechnicalFailureRate",
            "maximumVerifierP95LatencyMilliseconds",
            "maximumParticipantLatencyRegressionFraction",
            "lanes", "hardGates",
            "humanQualityReviewProcedure", "thresholdRationale");

        ValidateCorpus(development, V7Partition.Development);
        ValidateCorpus(holdout, V7Partition.Holdout);
        ValidateCriteria(criteria);
        RequireLaneCoverage(development, criteria, "development");
        RequireLaneCoverage(holdout, criteria, "holdout");

        var devIds = development.Cases.Select(c => c.CaseId)
            .ToHashSet(StringComparer.Ordinal);
        if (holdout.Cases.Any(c => devIds.Contains(c.CaseId)))
            throw new InvalidDataException(
                "Reviewed development and holdout case IDs overlap.");

        // Detect exact duplicate scenarios even when IDs were renamed.
        // A semantically near-identical paraphrase still needs manual review.
        var devFingerprints = development.Cases.Select(CaseFingerprint)
            .ToHashSet(StringComparer.Ordinal);
        if (holdout.Cases.Any(c =>
                devFingerprints.Contains(CaseFingerprint(c))))
            throw new InvalidDataException(
                "Identical claim/evidence examples appear in both partitions.");

        return new V7FreezePreparation(
            V7FreezeContract.ReviewVersion,
            V7FreezeContract.ThresholdVersion,
            Hash(developmentJson), Hash(holdoutJson), Hash(criteriaJson),
            development.Cases.Count, holdout.Cases.Count,
            HumanApprovalReceived: false,
            SemanticAcceptanceGranted: false,
            EnforcementAuthorized: false);
    }

    /// <summary>
    /// A self-attested approval binds exact bytes but is NOT a digital signature
    /// or proof of independent review or holdout secrecy.
    /// </summary>
    public static V7FreezeReceipt Validate(
        string developmentJson, string holdoutJson,
        string criteriaJson, string ownerApprovalJson)
    {
        var prepared = Prepare(
            developmentJson, holdoutJson, criteriaJson);
        var approval = Read<V7OwnerFreezeApproval>(ownerApprovalJson,
            "version", "ownerId", "approvedAtUtc", "developmentSha256",
            "holdoutSha256", "criteriaSha256", "declaration");
        ValidateApproval(approval);

        if (!SameHash(approval.DevelopmentSha256, prepared.DevelopmentSha256) ||
            !SameHash(approval.HoldoutSha256, prepared.HoldoutSha256) ||
            !SameHash(approval.CriteriaSha256, prepared.CriteriaSha256))
            throw new InvalidDataException(
                "Owner freeze approval does not pin exact source bytes.");

        var development = Read<V7ReviewedCorpus>(developmentJson,
            "version", "labelStatus", "partition", "reviewMethod",
            "sourceDeclaration", "cases");
        var holdout = Read<V7ReviewedCorpus>(holdoutJson,
            "version", "labelStatus", "partition", "reviewMethod",
            "sourceDeclaration", "cases");
        if (approval.ApprovedAtUtc <
                development.Cases.Max(c => c.ReviewedAtUtc) ||
            approval.ApprovedAtUtc <
                holdout.Cases.Max(c => c.ReviewedAtUtc))
            throw new InvalidDataException(
                "Owner approval predates a purported human adjudication.");

        return new V7FreezeReceipt(
            V7FreezeContract.ApprovalVersion,
            prepared.DevelopmentSha256,
            prepared.HoldoutSha256,
            prepared.CriteriaSha256,
            Hash(ownerApprovalJson),
            prepared.DevelopmentCases,
            prepared.HoldoutCases,
            "owner-attested-preregistration-validated",
            SemanticAcceptanceGranted: false,
            EnforcementAuthorized: false,
            "The approval is a human attestation, not cryptographic proof of " +
            "independent review or holdout secrecy. No holdout run, statistical " +
            "acceptance, natural conversation or crash/latency gate occurred.");
    }

    /// <summary>
    /// Creates a scoped, deep-read-only view only AFTER all four private file
    /// inputs passed V7a's exact SHA/attestation review. No holdout is exported.
    /// Never call this with data acquired from a public model or GitHub.
    /// </summary>
    public static V7ApprovedEvaluationInputs OpenApproved(
        string developmentJson, string holdoutJson, string criteriaJson,
        string ownerApprovalJson)
    {
        var receipt = Validate(
            developmentJson, holdoutJson, criteriaJson, ownerApprovalJson);
        static V7ReviewedCorpus Freeze(V7ReviewedCorpus original) =>
            original with
            {
                Cases = Array.AsReadOnly(original.Cases.Select(c => c with
                {
                    Evidence = Array.AsReadOnly(c.Evidence.ToArray()),
                    SupportingReferences = Array.AsReadOnly(
                        c.SupportingReferences.ToArray()),
                    OpposingReferences = Array.AsReadOnly(
                        c.OpposingReferences.ToArray())
                }).ToArray())
            };
        var development = Freeze(Read<V7ReviewedCorpus>(developmentJson,
            "version", "labelStatus", "partition", "reviewMethod",
            "sourceDeclaration", "cases"));
        var holdout = Freeze(Read<V7ReviewedCorpus>(holdoutJson,
            "version", "labelStatus", "partition", "reviewMethod",
            "sourceDeclaration", "cases"));
        var originalCriteria = Read<V7AcceptanceCriteria>(criteriaJson,
            "version", "modelName", "modelDigest", "verifierPromptVersion",
            "verificationSchemaVersion", "scorerVersion",
            "minimumIdenticalInputRuns", "minimumNaturalSessions",
            "minimumCrashScenarios", "minimumClaimSpanPrecision",
            "minimumClaimSpanRecall", "minimumRhetoricalModeAccuracy",
            "minimumInsufficientEvidenceRecall", "minimumConflictingEvidenceRecall",
            "maximumUnscorableRate", "maximumQueueDropRate",
            "maximumTechnicalFailureRate", "maximumVerifierP95LatencyMilliseconds",
            "maximumParticipantLatencyRegressionFraction",
            "lanes", "hardGates", "humanQualityReviewProcedure",
            "thresholdRationale");
        var criteria = originalCriteria with
        {
            Lanes = Array.AsReadOnly(originalCriteria.Lanes.ToArray())
        };
        return new V7ApprovedEvaluationInputs(development, holdout,
            criteria, receipt);
    }

    private static void ValidateCorpus(
        V7ReviewedCorpus corpus, V7Partition partition)
    {
        if (corpus.Version != V7FreezeContract.ReviewVersion ||
            corpus.LabelStatus != V7FreezeContract.LabelStatus ||
            corpus.Partition != partition ||
            corpus.ReviewMethod != V7FreezeContract.ReviewMethod ||
            !Valid(corpus.SourceDeclaration, 2000) ||
            corpus.Cases is null or { Count: < 1 or > V7FreezeContract.MaxCases })
            throw new InvalidDataException(
                "Missing human-attested reviewed corpus or wrong partition/version.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in corpus.Cases)
        {
            if (item is null || !ValidId(item.CaseId) ||
                !ValidId(item.ScenarioGroup) ||
                !ids.Add(item.CaseId) ||
                !Enum.IsDefined(item.Category) ||
                !Enum.IsDefined(item.RhetoricalMode) ||
                !Enum.IsDefined(item.ReviewedOutcome) ||
                !Valid(item.UtteranceText, 12_000) ||
                !Valid(item.ClaimText, 4000) ||
                item.ClaimStartUtf16 < 0 ||
                item.ClaimLengthUtf16 < 1 ||
                item.ClaimStartUtf16 > item.UtteranceText.Length ||
                item.ClaimLengthUtf16 >
                    item.UtteranceText.Length - item.ClaimStartUtf16 ||
                !string.Equals(item.UtteranceText.Substring(
                    item.ClaimStartUtf16, item.ClaimLengthUtf16),
                    item.ClaimText, StringComparison.Ordinal) ||
                !Valid(item.ReviewRationale, 3000) ||
                item.ReviewRationale.Length < 16 ||
                !ValidId(item.ReviewerId) ||
                item.ReviewedAtUtc.Offset != TimeSpan.Zero ||
                item.ReviewedAtUtc == default ||
                item.ReviewedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
                item.Evidence is null or { Count: > V7FreezeContract.MaxEvidence } ||
                item.SupportingReferences is null ||
                item.OpposingReferences is null)
                throw new InvalidDataException(
                    "Missing or malformed owner-review record or duplicate case.");

            // Negative extraction examples (jokes, questions, opinions)
            // remain in the claim-selection denominator; a verifier must not
            // automatically promote them to material contradictions.
            if ((item.ReviewedOutcome ==
                    ClaimVerificationOutcome.ConfirmedContradiction &&
                 (!item.ShouldExtractClaim ||
                  item.RhetoricalMode != ClaimRhetoricalMode.Asserted ||
                  item.Category == ClaimCategory.Other)) ||
                (!item.ShouldExtractClaim &&
                 (item.RhetoricalMode == ClaimRhetoricalMode.Asserted &&
                  item.Category != ClaimCategory.Other ||
                  item.ReviewedOutcome != ClaimVerificationOutcome.NoMaterialIssue ||
                  item.SupportingReferences.Count != 0 ||
                  item.OpposingReferences.Count != 0)))
                throw new InvalidDataException(
                    "Negative extraction example cannot be an asserted material verdict.");

            var refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var evidence in item.Evidence)
            {
                if (evidence is null ||
                    !ValidReferenceId(evidence.ReferenceId) ||
                    !refs.Add(evidence.ReferenceId) ||
                    !Enum.IsDefined(evidence.Kind) ||
                    !Enum.IsDefined(evidence.AuthorityTier) ||
                    !Enum.IsDefined(evidence.Visibility) ||
                    !Valid(evidence.Excerpt, 8000) ||
                    evidence.IsSuperseded !=
                        (evidence.AuthorityTier ==
                         EvidenceAuthorityTier.SupersededRecollection))
                    throw new InvalidDataException(
                        "Malformed or repeated reviewed evidence authority.");
            }
            var support = item.SupportingReferences;
            var oppose = item.OpposingReferences;
            if (support.Count > refs.Count || oppose.Count > refs.Count ||
                support.Count != support.Distinct(StringComparer.Ordinal).Count() ||
                oppose.Count != oppose.Distinct(StringComparer.Ordinal).Count() ||
                support.Intersect(oppose, StringComparer.Ordinal).Any() ||
                support.Concat(oppose).Any(x => !refs.Contains(x)) ||
                item.ReviewedOutcome == ClaimVerificationOutcome.ConfirmedContradiction &&
                    oppose.Count == 0 ||
                item.ReviewedOutcome == ClaimVerificationOutcome.ConflictingEvidence &&
                    (support.Count == 0 || oppose.Count == 0) ||
                item.ReviewedOutcome == ClaimVerificationOutcome.InsufficientEvidence &&
                    (support.Count > 0 || oppose.Count > 0) ||
                item.ReviewedOutcome == ClaimVerificationOutcome.NoMaterialIssue &&
                    oppose.Count > 0)
                throw new InvalidDataException(
                    "Human-adjudicated references cannot support the selected outcome.");

            if (!fingerprints.Add(CaseFingerprint(item)))
                throw new InvalidDataException(
                    "Reviewed corpus repeats identical claim and evidence under another ID.");
        }
    }

    private static void ValidateCriteria(V7AcceptanceCriteria plan)
    {
        if (plan.Version != V7FreezeContract.ThresholdVersion ||
            !Valid(plan.ModelName, 200) ||
            !Valid(plan.ModelDigest, 256) ||
            !IsHash(plan.ModelDigest) ||
            plan.VerifierPromptVersion !=
                EvidenceVerifierV4.VerificationPromptVersion ||
            plan.VerificationSchemaVersion !=
                EvidenceVerifierV4.VerificationSchemaVersion ||
            plan.ScorerVersion != V7FreezeContract.PlannedReviewedScorerVersion ||
            plan.MinimumIdenticalInputRuns < 2 ||
            plan.MinimumNaturalSessions < 1 ||
            plan.MinimumCrashScenarios < 1 ||
            !Fraction(plan.MinimumClaimSpanPrecision) ||
            !Fraction(plan.MinimumClaimSpanRecall) ||
            !Fraction(plan.MinimumRhetoricalModeAccuracy) ||
            !Fraction(plan.MinimumInsufficientEvidenceRecall) ||
            !Fraction(plan.MinimumConflictingEvidenceRecall) ||
            !Fraction(plan.MaximumUnscorableRate) ||
            !Fraction(plan.MaximumQueueDropRate) ||
            !Fraction(plan.MaximumTechnicalFailureRate) ||
            plan.MaximumVerifierP95LatencyMilliseconds < 1 ||
            !Fraction(plan.MaximumParticipantLatencyRegressionFraction) ||
            plan.Lanes is null ||
            !Valid(plan.HumanQualityReviewProcedure, 4000) ||
            !Valid(plan.ThresholdRationale, 4000) ||
            plan.ThresholdRationale.Length < 24 ||
            plan.HardGates is null ||
            !plan.HardGates.NoUnauthorizedEvidence ||
            !plan.HardGates.NoUnchargedModelAttempts ||
            !plan.HardGates.NoSpeechOrParticipantContextMutation ||
            !plan.HardGates.NoCorruptRecoveryPromotion ||
            !plan.HardGates.NoPrivateHoldoutLeakage ||
            !plan.HardGates.NoNonlocalVerifierInference ||
            !plan.HardGates.NoEnforcement)
            throw new InvalidDataException(
                "V7 release preregistration is incomplete or weakens a mandatory hard gate.");

        var required = Enum.GetNames<ClaimCategory>()
            .Select(x => "category:" + x)
            .Append("rhetoric:ProtectedNonAsserted")
            .ToHashSet(StringComparer.Ordinal);
        if (plan.Lanes.Count != required.Count ||
            plan.Lanes.Any(x => x is null || !required.Remove(x.Lane)) ||
            required.Count != 0)
            throw new InvalidDataException(
                "All independent semantic categories and protected rhetoric need frozen criteria.");

        foreach (var lane in plan.Lanes)
        {
            if (lane.Lane is not ("category:Other" or
                    "rhetoric:ProtectedNonAsserted") &&
                lane.MinimumConfirmedPositiveCases < 1)
                throw new InvalidDataException(
                    "Material fact/identity/history/dialogue lanes require " +
                    "positive examples and frozen precision/recall thresholds.");
            if (lane.Lane == "rhetoric:ProtectedNonAsserted" &&
                lane.MinimumConfirmedPositiveCases != 0)
                throw new InvalidDataException(
                    "Protected nonasserted speech cannot be preregistered " +
                    "as confirmed contradiction ground truth.");
            if (lane.MinimumIndependentCases < 1 ||
                lane.MinimumCompletedCases < 1 ||
                lane.MinimumCompletedCases > lane.MinimumIndependentCases ||
                lane.MinimumConfirmedPositiveCases < 0 ||
                lane.MinimumConfirmedPositiveCases > lane.MinimumIndependentCases ||
                !Fraction(lane.MinimumSelectionCoverage) ||
                !Fraction(lane.MaximumConfirmedFalsePositiveRate) ||
                !Fraction(lane.MinimumEvidenceSupportRate) ||
                lane.MinimumConfirmedPrecision is { } precision &&
                    !Fraction(precision) ||
                lane.MinimumEndToEndConfirmedRecall is { } recall &&
                    !Fraction(recall))
                throw new InvalidDataException(
                    "Invalid numeric per-lane preregistration or denominator.");

            if (lane.MinimumConfirmedPositiveCases > 0 &&
                    (lane.MinimumConfirmedPrecision is null ||
                     lane.MinimumEndToEndConfirmedRecall is null ||
                     lane.NotApplicableReason is not null) ||
                lane.MinimumConfirmedPositiveCases == 0 &&
                    (lane.MinimumConfirmedPrecision is not null ||
                     lane.MinimumEndToEndConfirmedRecall is not null ||
                     !Valid(lane.NotApplicableReason, 500)))
                throw new InvalidDataException(
                    "Absent contradiction class must be explicit; positive lanes require precision and recall criteria.");
        }
    }

    private static void RequireLaneCoverage(
        V7ReviewedCorpus corpus, V7AcceptanceCriteria criteria,
        string partitionName)
    {
        // The denominator is independent *case inputs*, never same-case
        // repeated runs. Each side must contain all four epistemic outcomes.
        foreach (var outcome in Enum.GetValues<ClaimVerificationOutcome>())
            if (!corpus.Cases.Any(c => c.ReviewedOutcome == outcome))
                throw new InvalidDataException(
                    partitionName + " lacks a reviewed " + outcome + " case.");

        foreach (var rule in criteria.Lanes)
        {
            var candidates = rule.Lane == "rhetoric:ProtectedNonAsserted"
                ? corpus.Cases.Where(c =>
                    c.RhetoricalMode != ClaimRhetoricalMode.Asserted)
                : corpus.Cases.Where(c =>
                    "category:" + c.Category == rule.Lane);

            var count = candidates.Count();
            var positives = candidates.Count(c =>
                c.ReviewedOutcome ==
                ClaimVerificationOutcome.ConfirmedContradiction);

            if (count < rule.MinimumIndependentCases ||
                positives < rule.MinimumConfirmedPositiveCases)
                throw new InvalidDataException(
                    partitionName + " has fewer independent reviewed lane cases " +
                    "or confirmed positives than preregistered.");
        }
    }

    private static void ValidateApproval(V7OwnerFreezeApproval owner)
    {
        if (owner.Version != V7FreezeContract.ApprovalVersion ||
            !ValidId(owner.OwnerId) ||
            owner.ApprovedAtUtc.Offset != TimeSpan.Zero ||
            owner.ApprovedAtUtc == default ||
            owner.ApprovedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
            owner.Declaration != V7FreezeContract.ApprovalDeclaration ||
            !IsHash(owner.DevelopmentSha256) ||
            !IsHash(owner.HoldoutSha256) ||
            !IsHash(owner.CriteriaSha256))
            throw new InvalidDataException(
                "Missing exact owner preregistration attestation or SHA-256 pins.");
    }

    private static T Read<T>(string json, params string[] fields)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) is
            < 2 or > V7FreezeContract.MaxDocumentBytes)
            throw new InvalidDataException("Missing or oversized V7a input document.");

        try
        {
            using var document = JsonDocument.Parse(json);
            ExactFields(document.RootElement, fields);
            if (typeof(T) == typeof(V7ReviewedCorpus))
            {
                foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
                {
                    ExactFields(item, "caseId", "scenarioGroup", "category",
                        "rhetoricalMode", "utteranceText", "claimStartUtf16",
                        "claimLengthUtf16", "shouldExtractClaim", "claimText",
                        "evidence", "reviewedOutcome",
                        "supportingReferences", "opposingReferences",
                        "reviewRationale", "reviewerId", "reviewedAtUtc");
                    foreach (var ev in item.GetProperty("evidence").EnumerateArray())
                        ExactFields(ev, "referenceId", "kind", "authorityTier",
                            "visibility", "excerpt", "isSuperseded");
                }
            }
            if (typeof(T) == typeof(V7AcceptanceCriteria))
            {
                foreach (var lane in document.RootElement.GetProperty("lanes").EnumerateArray())
                    ExactFields(lane, "lane", "minimumIndependentCases",
                        "minimumCompletedCases", "minimumConfirmedPositiveCases",
                        "minimumSelectionCoverage",
                        "maximumConfirmedFalsePositiveRate",
                        "minimumConfirmedPrecision",
                        "minimumEndToEndConfirmedRecall",
                        "minimumEvidenceSupportRate", "notApplicableReason");
                ExactFields(document.RootElement.GetProperty("hardGates"),
                    "noUnauthorizedEvidence", "noUnchargedModelAttempts",
                    "noSpeechOrParticipantContextMutation",
                    "noCorruptRecoveryPromotion", "noPrivateHoldoutLeakage",
                    "noNonlocalVerifierInference", "noEnforcement");
            }

            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new InvalidDataException("V7a input is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Malformed V7a JSON, unknown enum or unexpected field.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException(
                "V7a JSON has wrong array or object shape.", ex);
        }
    }

    private static void ExactFields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("V7a expects a JSON object.");
        var required = names.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!required.Contains(field.Name) || !seen.Add(field.Name))
                throw new InvalidDataException(
                    "V7a input has unknown or repeated JSON fields.");
        if (seen.Count != required.Count)
            throw new InvalidDataException("V7a input omits a required field.");
    }

    private static string CaseFingerprint(V7ReviewedCase item)
    {
        var serialized = JsonSerializer.Serialize(new
        {
            claim = item.ClaimText.Trim().Normalize(NormalizationForm.FormC),
            evidence = item.Evidence
                .Select(e => new
                {
                    e.Kind, e.AuthorityTier, e.Visibility,
                    excerpt = e.Excerpt.Trim().Normalize(NormalizationForm.FormC),
                    e.IsSuperseded
                })
                .OrderBy(e => e.Kind).ThenBy(e => e.AuthorityTier)
                .ThenBy(e => e.Visibility).ThenBy(e => e.excerpt,
                    StringComparer.Ordinal).ThenBy(e => e.IsSuperseded).ToArray()
        });
        return Hash(serialized);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static bool SameHash(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal);

    private static bool IsHash(string? value) =>
        value is { Length: 64 } &&
        Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    // Real V3 source reference IDs include namespaced colons and GUIDs,
    // unlike the simplified lowercase IDs of the synthetic V6a mini-corpus.
    private static bool ValidReferenceId(string? value) =>
        value is { Length: >= 3 and <= 256 } &&
        Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._:/~-]*$",
            RegexOptions.CultureInvariant);

    private static bool Fraction(decimal value) => value is >= 0m and <= 1m;
    private static bool Valid(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static bool ValidId(string? value) =>
        value is { Length: >= 3 and <= 80 } &&
        Regex.IsMatch(value, "^[a-z0-9][a-z0-9-]*$",
            RegexOptions.CultureInvariant);
}

using BusStop.Moderation.Evaluation;

namespace BusStop.Engine.Evaluation;

/// <summary>
/// Diagnostic only. A V7b safe report cannot show case IDs, reviewed labels,
/// reviewer notes, private evidence, raw prompts, or holdout examples.
/// </summary>
public sealed record V7bMetricDiagnostic(
    string Lane,
    string Metric,
    decimal? Observed,
    decimal Required,
    string Comparator,
    string Status);

public sealed record V7bSafeAggregate(
    string ContractVersion,
    string ScorerVersion,
    string FreezeApprovalSha256,
    string CorpusSha256,
    string CriteriaSha256,
    V7Partition Partition,
    string ModelName,
    string ModelDigest,
    string RunId,
    V7ReviewedLane Overall,
    IReadOnlyList<V7ReviewedLane> ByCategory,
    IReadOnlyList<V7ReviewedLane> ByRhetoricalMode,
    IReadOnlyList<V7bMetricDiagnostic> PredeclaredMetricDiagnostics,
    int HostValidatedResponses,
    int HostRejectedResponses,
    int ModelTransportFailures,
    int PromptPreflightFailures,
    int ModelCalls,
    bool OwnerQualityReviewComplete,
    bool NaturalSessionGateTested,
    bool FaultRecoveryGateTested,
    bool SemanticAcceptanceGranted,
    bool EnforcementAuthorized,
    string Interpretation);

public sealed record V7bSafeDrift(
    string FirstRunId, string SecondRunId, string CorpusSha256,
    int IndependentCases, int ComparableCompletedOracleCases,
    int ChangedOracleOutcomeCount,
    int ChangedExtractionOrTechnicalCount,
    bool AcceptanceGranted);

public static class V7bSafeExporter
{
    public static V7bSafeAggregate From(
        V7bLabRun lab, V7ApprovedEvaluationInputs approved)
    {
        ArgumentNullException.ThrowIfNull(lab);
        ArgumentNullException.ThrowIfNull(approved);
        var report = lab.Score;
        var privateAudit = lab.PrivateAudit;
        var corpusHash = report.Partition == V7Partition.Development
            ? approved.Receipt.DevelopmentSha256
            : approved.Receipt.HoldoutSha256;
        if (report.ApprovalSha256 != approved.Receipt.ApprovalSha256 ||
            report.CriteriaSha256 != approved.Receipt.CriteriaSha256 ||
            report.CorpusSha256 != corpusHash ||
            privateAudit.ApprovalSha256 != approved.Receipt.ApprovalSha256 ||
            privateAudit.CorpusSha256 != corpusHash ||
            privateAudit.CriteriaSha256 != approved.Receipt.CriteriaSha256 ||
            privateAudit.Partition != report.Partition ||
            privateAudit.RunId != report.RunId ||
            privateAudit.ModelName != approved.Criteria.ModelName ||
            privateAudit.ModelDigest != approved.Criteria.ModelDigest ||
            report.ModelName != approved.Criteria.ModelName ||
            report.ModelDigest != approved.Criteria.ModelDigest ||
            report.SemanticAcceptanceGranted ||
            report.EnforcementAuthorized)
            throw new InvalidDataException(
                "V7b safe export requires identical approved model, corpus, freeze and run pins.");

        var checks = new List<V7bMetricDiagnostic>();
        var overall = report.Overall;
        static bool? Good(decimal? measured, decimal minimum) =>
            measured.HasValue ? measured.Value >= minimum : null;
        static bool? Below(decimal? measured, decimal maximum) =>
            measured.HasValue ? measured.Value <= maximum : null;
        static void Add(List<V7bMetricDiagnostic> into, string lane,
            string metric, decimal? observed, decimal required,
            bool isMinimum)
        {
            var good = isMinimum ? Good(observed, required)
                : Below(observed, required);
            into.Add(new V7bMetricDiagnostic(lane, metric,
                observed, required, isMinimum ? ">=" : "<=",
                good is true ? "MetInComponentLab" :
                    good is false ? "NotMet" : "Unscorable"));
        }

        foreach (var criterion in approved.Criteria.Lanes)
        {
            var data = criterion.Lane == "rhetoric:ProtectedNonAsserted"
                ? BuildProtectedLane(report)
                : report.ByCategory.Single(x => x.Lane == criterion.Lane);

            Add(checks, criterion.Lane, "independent-cases",
                data.IndependentCases, criterion.MinimumIndependentCases, true);
            Add(checks, criterion.Lane, "oracle-completed-cases",
                data.OracleCompleted, criterion.MinimumCompletedCases, true);
            Add(checks, criterion.Lane, "reviewed-positive-cases",
                data.ReviewedContradictions,
                criterion.MinimumConfirmedPositiveCases, true);
            Add(checks, criterion.Lane, "extraction-recall",
                data.ExtractionRecall,
                criterion.MinimumSelectionCoverage, true);
            Add(checks, criterion.Lane, "oracle-confirmed-false-positive-rate",
                data.OracleConfirmedFalsePositiveRate,
                criterion.MaximumConfirmedFalsePositiveRate, false);
            Add(checks, criterion.Lane, "reviewed-citation-relevance",
                data.CitationRelevanceRate,
                criterion.MinimumEvidenceSupportRate, true);
            if (criterion.MinimumConfirmedPrecision is { } precision)
                Add(checks, criterion.Lane, "oracle-confirmed-precision",
                    data.OracleConfirmedPrecision, precision, true);
            if (criterion.MinimumEndToEndConfirmedRecall is { } recall)
                Add(checks, criterion.Lane, "end-to-end-confirmed-recall",
                    data.EndToEndConfirmedRecall, recall, true);
        }

        Add(checks, "overall", "claim-span-precision",
            overall.ExtractionPrecision,
            approved.Criteria.MinimumClaimSpanPrecision, true);
        Add(checks, "overall", "claim-span-recall",
            overall.ExtractionRecall,
            approved.Criteria.MinimumClaimSpanRecall, true);
        Add(checks, "overall", "rhetorical-accuracy",
            overall.RhetoricalAccuracy,
            approved.Criteria.MinimumRhetoricalModeAccuracy, true);
        Add(checks, "overall", "insufficient-evidence-recall",
            overall.InsufficientEvidenceRecall,
            approved.Criteria.MinimumInsufficientEvidenceRecall, true);
        Add(checks, "overall", "conflicting-evidence-recall",
            overall.ConflictingEvidenceRecall,
            approved.Criteria.MinimumConflictingEvidenceRecall, true);
        Add(checks, "overall", "oracle-unscorable-rate",
            overall.UnscorableRate,
            approved.Criteria.MaximumUnscorableRate, false);

        // Deliberately do NOT mark queue drops, crash scenarios, human
        // quality review, or participant latency as "Met". V7b's isolated
        // model lab has no authority to evaluate those future V7c gates.
        Add(checks, "v7c-pending", "queue-drop-rate", null,
            approved.Criteria.MaximumQueueDropRate, false);
        Add(checks, "v7c-pending", "technical-failure-rate", null,
            approved.Criteria.MaximumTechnicalFailureRate, false);
        Add(checks, "v7c-pending", "verifier-p95-latency-milliseconds", null,
            approved.Criteria.MaximumVerifierP95LatencyMilliseconds, false);
        Add(checks, "v7c-pending", "participant-latency-regression-fraction",
            null, approved.Criteria.MaximumParticipantLatencyRegressionFraction,
            false);

        var audits = privateAudit.Cases;
        return new V7bSafeAggregate(
            V7bLabContract.Version, report.ScorerVersion,
            approved.Receipt.ApprovalSha256,
            corpusHash, approved.Receipt.CriteriaSha256, report.Partition,
            report.ModelName, report.ModelDigest, report.RunId,
            overall, report.ByCategory, report.ByRhetoricalMode,
            Array.AsReadOnly(checks.ToArray()),
            audits.Count(x => x.Diagnostic == "v4-host-validated"),
            audits.Count(x => x.Diagnostic.StartsWith("v4-rejected-", StringComparison.Ordinal)),
            audits.Count(x => x.Status == BusStop.Moderation.Verification.VerificationOperationalStatus.TransportFailure),
            audits.Count(x => x.Diagnostic == "prompt-preflight-ceiling"),
            overall.ChargedLocalModelCalls,
            OwnerQualityReviewComplete: false,
            NaturalSessionGateTested: false,
            FaultRecoveryGateTested: false,
            SemanticAcceptanceGranted: false,
            EnforcementAuthorized: false,
            "V7b diagnostic only. Owner-attested labels cannot authenticate " +
            "independent review. Oracle V4 input is NOT host-extraction success; " +
            "unobserved and failed cases remain in denominators. " +
            "No full V7 natural-session, integrity, concurrency or release gate passed.");
    }

    public static V7bSafeDrift Drift(V7bLabRun first, V7bLabRun second)
    {
        var raw = V7ReviewedScorer.Compare(first.Score, second.Score);
        return new V7bSafeDrift(
            raw.FirstRunId, raw.SecondRunId, raw.CorpusSha256,
            raw.IndependentCases, raw.BothOracleCompleted,
            raw.ChangedVerdictCaseIds.Count,
            raw.ChangedSelectionOrStatusCaseIds.Count,
            false);
    }

    private static V7ReviewedLane BuildProtectedLane(V7ReviewedRunScore report)
    {
        // This is an aggregate lane, not a concatenation of independent cases
        // from prior runs. Derive it from THIS run's per-case observations.
        var rows = report.Cases.Where(x =>
            x.RhetoricalMode != BusStop.Moderation.Verification.ClaimRhetoricalMode.Asserted).ToArray();
        var completed = rows.Where(x =>
            x.Status == BusStop.Moderation.Verification.VerificationOperationalStatus.Completed).ToArray();
        return new V7ReviewedLane(
            "rhetoric:ProtectedNonAsserted",
            rows.Length, rows.Count(x => x.Observed),
            rows.Count(x => x.ShouldExtract),
            rows.Count(x => x.ShouldExtract && x.ExactSpanDetected),
            rows.Count(x => !x.ShouldExtract),
            rows.Count(x => x.FalseExtractionOnProtected),
            rows.Sum(x => x.ExtraDetectedCandidates),
            rows.Count(x => x.ShouldExtract && x.CategoryCorrect),
            rows.Count(x => x.ShouldExtract && x.RhetoricalModeCorrect),
            completed.Length,
            rows.Count(x => x.Observed && x.ShouldExtract &&
                x.Status is not (
                    BusStop.Moderation.Verification.VerificationOperationalStatus.Completed or
                    BusStop.Moderation.Verification.VerificationOperationalStatus.NotStarted)),
            completed.Count(x => x.OracleOutcomeMatch == true),
            rows.Count(x => x.ExpectedOutcome ==
                BusStop.Moderation.Verification.ClaimVerificationOutcome.ConfirmedContradiction),
            completed.Count(x => x.ExpectedOutcome ==
                BusStop.Moderation.Verification.ClaimVerificationOutcome.ConfirmedContradiction &&
                x.Outcome == BusStop.Moderation.Verification.ClaimVerificationOutcome.ConfirmedContradiction),
            completed.Count(x => x.ExpectedOutcome !=
                BusStop.Moderation.Verification.ClaimVerificationOutcome.ConfirmedContradiction &&
                x.Outcome == BusStop.Moderation.Verification.ClaimVerificationOutcome.ConfirmedContradiction),
            completed.Count(x => x.ExpectedOutcome ==
                BusStop.Moderation.Verification.ClaimVerificationOutcome.ConfirmedContradiction &&
                x.Outcome != BusStop.Moderation.Verification.ClaimVerificationOutcome.ConfirmedContradiction),
            completed.Count(x => x.ExpectedOutcome ==
                BusStop.Moderation.Verification.ClaimVerificationOutcome.ConfirmedContradiction &&
                x.Outcome == BusStop.Moderation.Verification.ClaimVerificationOutcome.ConfirmedContradiction &&
                x.ExactSpanDetected && x.CategoryCorrect && x.RhetoricalModeCorrect),
            completed.Count(x => x.ExpectedOutcome !=
                BusStop.Moderation.Verification.ClaimVerificationOutcome.ConfirmedContradiction),
            rows.Count(x => x.ExpectedOutcome ==
                BusStop.Moderation.Verification.ClaimVerificationOutcome.InsufficientEvidence),
            completed.Count(x => x.ExpectedOutcome ==
                BusStop.Moderation.Verification.ClaimVerificationOutcome.InsufficientEvidence &&
                x.Outcome == BusStop.Moderation.Verification.ClaimVerificationOutcome.InsufficientEvidence),
            rows.Count(x => x.ExpectedOutcome ==
                BusStop.Moderation.Verification.ClaimVerificationOutcome.ConflictingEvidence),
            completed.Count(x => x.ExpectedOutcome ==
                BusStop.Moderation.Verification.ClaimVerificationOutcome.ConflictingEvidence &&
                x.Outcome == BusStop.Moderation.Verification.ClaimVerificationOutcome.ConflictingEvidence),
            completed.Count(x => x.ReferenceScoringApplicable),
            completed.Count(x => x.ReferenceScoringApplicable && x.CitedReferencesAuthorized == true),
            completed.Count(x => x.ReferenceScoringApplicable && x.CitedReferencesRelevant == true),
            completed.Count(x => x.MaterialSpanValid == false),
            completed.Count(x => x.VerdictShapeValid == false),
            rows.Sum(x => x.ChargedLocalModelCalls));
    }
}

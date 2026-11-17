using BusStop.Moderation.Verification;

namespace BusStop.Moderation.Evaluation;

/// <summary>
/// Model-free reviewed-label scorecard. "Oracle" means an owner-supplied
/// claim span was passed to V4; extraction metrics always measure V3
/// independently and must never inherit an oracle's classification.
/// </summary>
public sealed record V7CaseObservation(
    string CaseId,
    int DetectedCandidateCount,
    bool ExactSpanDetected,
    bool CategoryCorrect,
    bool RhetoricalModeCorrect,
    VerificationOperationalStatus Status,
    ClaimVerificationOutcome? Outcome,
    IReadOnlyList<string> SupportingReferences,
    IReadOnlyList<string> OpposingReferences,
    string ProblematicText,
    int ChargedLocalModelCalls,
    long InferenceMilliseconds);

public sealed record V7ReviewedCaseScore(
    string CaseId,
    string Lane,
    ClaimRhetoricalMode RhetoricalMode,
    bool ShouldExtract,
    ClaimVerificationOutcome ExpectedOutcome,
    bool Observed,
    bool ExactSpanDetected,
    bool CategoryCorrect,
    bool RhetoricalModeCorrect,
    bool FalseExtractionOnProtected,
    int ExtraDetectedCandidates,
    VerificationOperationalStatus Status,
    ClaimVerificationOutcome? Outcome,
    bool? OracleOutcomeMatch,
    bool? CitedReferencesAuthorized,
    bool? CitedReferencesRelevant,
    bool ReferenceScoringApplicable,
    bool? MaterialSpanValid,
    bool? VerdictShapeValid,
    int ChargedLocalModelCalls,
    long InferenceMilliseconds);

public sealed record V7ReviewedLane(
    string Lane,
    int IndependentCases,
    int ObservedCases,
    int ExpectedExtractedCases,
    int ExactSpanHits,
    int ProtectedCases,
    int ProtectedExtractionFalsePositives,
    int ExtraDetectedCandidates,
    int CorrectCategories,
    int CorrectRhetoricalModes,
    int OracleCompleted,
    int OracleTechnicalFailures,
    int OracleOutcomeMatches,
    int ReviewedContradictions,
    int OracleContradictionTruePositives,
    int OracleContradictionFalsePositives,
    int OracleContradictionMissedOnCompleted,
    int EndToEndContradictionsFound,
    int OracleCompletedNonContradictions,
    int ReviewedInsufficient,
    int ObservedInsufficientCorrect,
    int ReviewedConflicting,
    int ObservedConflictingCorrect,
    int ReferenceScoringCases,
    int AuthorizedCitationCases,
    int RelevantCitationCases,
    int InvalidMaterialSpans,
    int InvalidVerdictShapes,
    int ChargedLocalModelCalls)
{
    private static decimal? Ratio(int n, int d) =>
        d == 0 ? null : (decimal)n / d;

    public decimal? ExtractionPrecision => Ratio(
        ExactSpanHits, ExactSpanHits + ExtraDetectedCandidates);
    public decimal? ExtractionRecall => Ratio(
        ExactSpanHits, ExpectedExtractedCases);
    public decimal? RhetoricalAccuracy => Ratio(
        CorrectRhetoricalModes, ExactSpanHits);
    public decimal? CategoryAccuracy => Ratio(
        CorrectCategories, ExactSpanHits);
    public decimal? OracleAccuracy => Ratio(
        OracleOutcomeMatches, OracleCompleted);
    public decimal? OracleConfirmedPrecision => Ratio(
        OracleContradictionTruePositives,
        OracleContradictionTruePositives + OracleContradictionFalsePositives);
    public decimal? OracleConfirmedFalsePositiveRate => Ratio(
        OracleContradictionFalsePositives, OracleCompletedNonContradictions);
    public decimal? EndToEndConfirmedRecall => Ratio(
        EndToEndContradictionsFound, ReviewedContradictions);
    public decimal? InsufficientEvidenceRecall => Ratio(
        ObservedInsufficientCorrect, ReviewedInsufficient);
    public decimal? ConflictingEvidenceRecall => Ratio(
        ObservedConflictingCorrect, ReviewedConflicting);
    public decimal? CitationAuthorizationRate => Ratio(
        AuthorizedCitationCases, ReferenceScoringCases);
    public decimal? CitationRelevanceRate => Ratio(
        RelevantCitationCases, ReferenceScoringCases);
    public decimal? UnscorableRate => Ratio(
        ExpectedExtractedCases - OracleCompleted, ExpectedExtractedCases);
}

/// <summary>
/// Private per-case diagnostics must stay in local private output. Never put
/// ReviewedCaseScore (case IDs and gold outcomes) in shareable summaries.
/// </summary>
public sealed record V7ReviewedRunScore(
    string ScorerVersion,
    string ApprovalSha256,
    string CorpusSha256,
    string CriteriaSha256,
    V7Partition Partition,
    string ModelName,
    string ModelDigest,
    string RunId,
    V7ReviewedLane Overall,
    IReadOnlyList<V7ReviewedLane> ByCategory,
    IReadOnlyList<V7ReviewedLane> ByRhetoricalMode,
    IReadOnlyList<V7ReviewedCaseScore> Cases,
    bool SemanticAcceptanceGranted,
    bool EnforcementAuthorized,
    string Interpretation);

public sealed record V7ReviewedRunDrift(
    string CorpusSha256,
    string FirstRunId,
    string SecondRunId,
    int IndependentCases,
    int BothOracleCompleted,
    IReadOnlyList<string> ChangedVerdictCaseIds,
    IReadOnlyList<string> ChangedSelectionOrStatusCaseIds,
    bool SemanticAcceptanceGranted);

public static class V7ReviewedScorer
{
    public static V7ReviewedRunScore Score(
        V7ApprovedEvaluationInputs frozen,
        V7Partition partition,
        string runId,
        string modelName,
        string modelDigest,
        IReadOnlyList<V7CaseObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(observations);
        if (!Enum.IsDefined(partition) ||
            string.IsNullOrWhiteSpace(runId) || runId.Length > 128 ||
            modelName != frozen.Criteria.ModelName ||
            modelDigest != frozen.Criteria.ModelDigest ||
            frozen.Receipt.SemanticAcceptanceGranted ||
            frozen.Receipt.EnforcementAuthorized ||
            frozen.Criteria.ScorerVersion != V7FreezeContract.PlannedReviewedScorerVersion)
            throw new InvalidDataException(
                "V7b refuses unfrozen, unpinned or acceptance-enabled model data.");

        var corpus = partition == V7Partition.Development
            ? frozen.Development : frozen.Holdout;
        var hash = partition == V7Partition.Development
            ? frozen.Receipt.DevelopmentSha256
            : frozen.Receipt.HoldoutSha256;
        var casesById = corpus.Cases.ToDictionary(x => x.CaseId,
            StringComparer.Ordinal);
        var seen = new Dictionary<string, V7CaseObservation>(
            StringComparer.Ordinal);
        foreach (var obs in observations)
        {
            if (obs is null || !casesById.TryGetValue(obs.CaseId, out var item) ||
                !seen.TryAdd(obs.CaseId, obs) ||
                !Enum.IsDefined(obs.Status) ||
                obs.DetectedCandidateCount is < 0 or > 24 ||
                obs.ExactSpanDetected && obs.DetectedCandidateCount == 0 ||
                (obs.CategoryCorrect || obs.RhetoricalModeCorrect) &&
                    !obs.ExactSpanDetected ||
                obs.ChargedLocalModelCalls is < 0 or > 1 ||
                obs.InferenceMilliseconds is < 0 or > 600_000 ||
                obs.SupportingReferences is null ||
                obs.OpposingReferences is null ||
                obs.SupportingReferences.Count > 32 ||
                obs.OpposingReferences.Count > 32 ||
                obs.SupportingReferences.Count !=
                    obs.SupportingReferences.Distinct(StringComparer.Ordinal).Count() ||
                obs.OpposingReferences.Count !=
                    obs.OpposingReferences.Distinct(StringComparer.Ordinal).Count() ||
                obs.ProblematicText is null or { Length: > 4000 } ||
                obs.Outcome.HasValue !=
                    (obs.Status == VerificationOperationalStatus.Completed) ||
                (obs.Outcome.HasValue && !Enum.IsDefined(obs.Outcome.Value)) ||
                (obs.Status == VerificationOperationalStatus.Completed &&
                    obs.ChargedLocalModelCalls != 1) ||
                (!item.ShouldExtractClaim &&
                    (obs.Outcome.HasValue || obs.ChargedLocalModelCalls != 0 ||
                     obs.Status != VerificationOperationalStatus.NotStarted)) ||
                (obs.Status == VerificationOperationalStatus.NotStarted &&
                    obs.ChargedLocalModelCalls != 0) ||
                (obs.Status != VerificationOperationalStatus.Completed &&
                    (obs.SupportingReferences.Count != 0 ||
                     obs.OpposingReferences.Count != 0 ||
                     obs.ProblematicText.Length != 0)))
                throw new InvalidDataException(
                    "V7b observation has conflicting identity, extraction, verdict, status or charging.");
        }

        var scored = new List<V7ReviewedCaseScore>(corpus.Cases.Count);
        foreach (var example in corpus.Cases)
        {
            if (!seen.TryGetValue(example.CaseId, out var obs))
            {
                scored.Add(new V7ReviewedCaseScore(
                    example.CaseId, "category:" + example.Category,
                    example.RhetoricalMode, example.ShouldExtractClaim,
                    example.ReviewedOutcome, false, false, false, false,
                    false, 0, VerificationOperationalStatus.NotStarted,
                    null, null, null, false, null, null, 0, 0));
                continue;
            }

            bool? citedAuthorized = null;
            bool? citedRelevant = null;
            bool? materialSpanValid = null;
            bool? verdictShape = null;
            if (obs.Status == VerificationOperationalStatus.Completed)
            {
                var allowed = example.Evidence.Select(x => x.ReferenceId)
                    .ToHashSet(StringComparer.Ordinal);
                citedAuthorized = obs.SupportingReferences
                    .Concat(obs.OpposingReferences).All(allowed.Contains);
                citedRelevant = citedAuthorized.Value &&
                    CitationFitsReviewedCase(example, obs);
                if (obs.Outcome == ClaimVerificationOutcome.ConfirmedContradiction)
                    materialSpanValid = !string.IsNullOrWhiteSpace(
                        obs.ProblematicText) &&
                        example.ClaimText.Contains(
                            obs.ProblematicText, StringComparison.Ordinal);
                verdictShape = ShapeValid(obs, materialSpanValid);
            }

            scored.Add(new V7ReviewedCaseScore(
                example.CaseId, "category:" + example.Category,
                example.RhetoricalMode, example.ShouldExtractClaim,
                example.ReviewedOutcome, true,
                obs.ExactSpanDetected,
                obs.CategoryCorrect, obs.RhetoricalModeCorrect,
                !example.ShouldExtractClaim &&
                    obs.DetectedCandidateCount > 0,
                Math.Max(0, obs.DetectedCandidateCount -
                    (example.ShouldExtractClaim && obs.ExactSpanDetected ? 1 : 0)),
                obs.Status, obs.Outcome,
                obs.Outcome.HasValue
                    ? obs.Outcome == example.ReviewedOutcome : null,
                citedAuthorized, citedRelevant,
                example.ReviewedOutcome is
                    ClaimVerificationOutcome.ConfirmedContradiction or
                    ClaimVerificationOutcome.ConflictingEvidence ||
                    example.SupportingReferences.Count > 0 ||
                    example.OpposingReferences.Count > 0 ||
                    obs.SupportingReferences.Count > 0 ||
                    obs.OpposingReferences.Count > 0,
                materialSpanValid, verdictShape, obs.ChargedLocalModelCalls,
                obs.InferenceMilliseconds));
        }

        var all = scored.ToArray();
        var categories = Enum.GetValues<ClaimCategory>()
            .Select(x => BuildLane("category:" + x,
                all.Where(c => c.Lane == "category:" + x))).ToArray();
        var rhetoric = Enum.GetValues<ClaimRhetoricalMode>()
            .Select(x => BuildLane("rhetoric:" + x,
                all.Where(c => c.RhetoricalMode == x))).ToArray();
        return new V7ReviewedRunScore(
            V7FreezeContract.PlannedReviewedScorerVersion,
            frozen.Receipt.ApprovalSha256, hash,
            frozen.Receipt.CriteriaSha256, partition,
            modelName, modelDigest, runId,
            BuildLane("overall", all),
            Array.AsReadOnly(categories), Array.AsReadOnly(rhetoric),
            Array.AsReadOnly(all), false, false,
            "Human attestation is not independently authenticated. Oracle V4 " +
            "judgments use owner-supplied exact claims; they cannot replace V3 " +
            "extraction recall. These are PRIVATE single-run development metrics, " +
            "not final V7 readiness or independent natural-session results.");
    }

    public static V7ReviewedRunDrift Compare(
        V7ReviewedRunScore first, V7ReviewedRunScore second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.RunId == second.RunId ||
            first.Partition != second.Partition ||
            first.CorpusSha256 != second.CorpusSha256 ||
            first.ApprovalSha256 != second.ApprovalSha256 ||
            first.CriteriaSha256 != second.CriteriaSha256 ||
            first.ModelName != second.ModelName ||
            first.ModelDigest != second.ModelDigest ||
            first.ScorerVersion != second.ScorerVersion ||
            first.Cases.Count != second.Cases.Count ||
            !first.Cases.Select(c => c.CaseId)
                .SequenceEqual(second.Cases.Select(c => c.CaseId)))
            throw new InvalidDataException(
                "V7b drift requires two distinct same-corpus, same-version, same-model runs.");

        var flips = new List<string>();
        var coverageChanges = new List<string>();
        int comparable = 0;
        for (int i = 0; i < first.Cases.Count; i++)
        {
            var a = first.Cases[i];
            var b = second.Cases[i];
            if (a.Observed != b.Observed ||
                a.Status != b.Status ||
                a.ExactSpanDetected != b.ExactSpanDetected ||
                a.CategoryCorrect != b.CategoryCorrect ||
                a.RhetoricalModeCorrect != b.RhetoricalModeCorrect)
                coverageChanges.Add(a.CaseId);
            if (a.Status == VerificationOperationalStatus.Completed &&
                b.Status == VerificationOperationalStatus.Completed)
            {
                comparable++;
                if (a.Outcome != b.Outcome)
                    flips.Add(a.CaseId);
            }
        }
        return new V7ReviewedRunDrift(
            first.CorpusSha256, first.RunId, second.RunId,
            first.Cases.Count, comparable,
            Array.AsReadOnly(flips.ToArray()),
            Array.AsReadOnly(coverageChanges.ToArray()), false);
    }

    private static bool CitationFitsReviewedCase(
        V7ReviewedCase expected, V7CaseObservation observed)
    {
        if (expected.ReviewedOutcome != observed.Outcome)
            return false;
        var expectedSupports = expected.SupportingReferences
            .ToHashSet(StringComparer.Ordinal);
        var expectedOpposes = expected.OpposingReferences
            .ToHashSet(StringComparer.Ordinal);
        if (!observed.SupportingReferences.All(expectedSupports.Contains) ||
            !observed.OpposingReferences.All(expectedOpposes.Contains))
            return false;
        return observed.Outcome switch
        {
            ClaimVerificationOutcome.ConfirmedContradiction =>
                observed.OpposingReferences.Any(expectedOpposes.Contains),
            ClaimVerificationOutcome.ConflictingEvidence =>
                observed.OpposingReferences.Any(expectedOpposes.Contains) &&
                observed.SupportingReferences.Any(expectedSupports.Contains),
            ClaimVerificationOutcome.InsufficientEvidence =>
                observed.SupportingReferences.Count == 0 &&
                observed.OpposingReferences.Count == 0,
            ClaimVerificationOutcome.NoMaterialIssue =>
                observed.OpposingReferences.Count == 0,
            _ => false
        };
    }

    private static bool ShapeValid(
        V7CaseObservation obs, bool? spanValid)
    {
        return obs.Outcome switch
        {
            ClaimVerificationOutcome.ConfirmedContradiction =>
                obs.OpposingReferences.Count > 0 && spanValid == true,
            ClaimVerificationOutcome.ConflictingEvidence =>
                obs.SupportingReferences.Count > 0 &&
                obs.OpposingReferences.Count > 0 &&
                string.IsNullOrEmpty(obs.ProblematicText),
            ClaimVerificationOutcome.InsufficientEvidence =>
                obs.SupportingReferences.Count == 0 &&
                obs.OpposingReferences.Count == 0 &&
                string.IsNullOrEmpty(obs.ProblematicText),
            ClaimVerificationOutcome.NoMaterialIssue =>
                obs.OpposingReferences.Count == 0 &&
                string.IsNullOrEmpty(obs.ProblematicText),
            _ => false
        };
    }

    private static V7ReviewedLane BuildLane(
        string lane, IEnumerable<V7ReviewedCaseScore> cases)
    {
        var rows = cases.ToArray();
        var complete = rows.Where(x =>
            x.Status == VerificationOperationalStatus.Completed).ToArray();
        var references = complete.Where(x =>
            x.ReferenceScoringApplicable).ToArray();
        return new V7ReviewedLane(
            lane, rows.Length, rows.Count(x => x.Observed),
            rows.Count(x => x.ShouldExtract),
            rows.Count(x => x.ShouldExtract && x.ExactSpanDetected),
            rows.Count(x => !x.ShouldExtract),
            rows.Count(x => x.FalseExtractionOnProtected),
            rows.Sum(x => x.ExtraDetectedCandidates),
            rows.Count(x => x.ShouldExtract && x.CategoryCorrect),
            rows.Count(x => x.ShouldExtract && x.RhetoricalModeCorrect),
            complete.Length,
            rows.Count(x => x.Observed && x.ShouldExtract &&
                x.Status is not (VerificationOperationalStatus.NotStarted or
                    VerificationOperationalStatus.Completed)),
            complete.Count(x => x.OracleOutcomeMatch == true),
            rows.Count(x => x.ExpectedOutcome ==
                ClaimVerificationOutcome.ConfirmedContradiction),
            complete.Count(x =>
                x.ExpectedOutcome == ClaimVerificationOutcome.ConfirmedContradiction &&
                x.Outcome == ClaimVerificationOutcome.ConfirmedContradiction),
            complete.Count(x =>
                x.ExpectedOutcome != ClaimVerificationOutcome.ConfirmedContradiction &&
                x.Outcome == ClaimVerificationOutcome.ConfirmedContradiction),
            complete.Count(x =>
                x.ExpectedOutcome == ClaimVerificationOutcome.ConfirmedContradiction &&
                x.Outcome != ClaimVerificationOutcome.ConfirmedContradiction),
            complete.Count(x =>
                x.ExpectedOutcome == ClaimVerificationOutcome.ConfirmedContradiction &&
                x.Outcome == ClaimVerificationOutcome.ConfirmedContradiction &&
                x.ExactSpanDetected && x.CategoryCorrect &&
                x.RhetoricalModeCorrect),
            complete.Count(x =>
                x.ExpectedOutcome != ClaimVerificationOutcome.ConfirmedContradiction),
            rows.Count(x => x.ExpectedOutcome ==
                ClaimVerificationOutcome.InsufficientEvidence),
            complete.Count(x =>
                x.ExpectedOutcome == ClaimVerificationOutcome.InsufficientEvidence &&
                x.Outcome == ClaimVerificationOutcome.InsufficientEvidence),
            rows.Count(x => x.ExpectedOutcome ==
                ClaimVerificationOutcome.ConflictingEvidence),
            complete.Count(x =>
                x.ExpectedOutcome == ClaimVerificationOutcome.ConflictingEvidence &&
                x.Outcome == ClaimVerificationOutcome.ConflictingEvidence),
            references.Length,
            references.Count(x => x.CitedReferencesAuthorized == true),
            references.Count(x => x.CitedReferencesRelevant == true),
            complete.Count(x => x.MaterialSpanValid == false),
            complete.Count(x => x.VerdictShapeValid == false),
            rows.Sum(x => x.ChargedLocalModelCalls));
    }
}

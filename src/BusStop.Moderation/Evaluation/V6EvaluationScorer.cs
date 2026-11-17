using BusStop.Moderation.Verification;

namespace BusStop.Moderation.Evaluation;

/// <summary>
/// Per-case audit with no claim text, source excerpts, private paths or raw LLM
/// output. Expected labels remain PROVISIONAL, not semantic ground truth.
/// </summary>
public sealed record V6CaseScore(
    string CaseId,
    string ScenarioGroup,
    ClaimCategory Category,
    ClaimRhetoricalMode RhetoricalMode,
    ClaimVerificationOutcome ExpectedOutcome,
    V6SelectionDisposition Selection,
    bool ExplicitlyObserved,
    VerificationOperationalStatus OperationalStatus,
    ClaimVerificationOutcome? ObservedOutcome,
    bool? OutcomeMatchesProvisionalLabel,
    bool? ReferencesAuthorized,
    bool? ReferencesMatchProvisionalRubric,
    bool? ContradictionSpanValid,
    bool? VerdictShapeValid,
    int CitedReferenceCount,
    int ChargedModelCalls);

public sealed record V6ConfusionCell(
    ClaimVerificationOutcome Expected,
    ClaimVerificationOutcome Observed,
    int Count);

public sealed record V6LaneScore(
    string Lane,
    int TotalCases,
    int ExplicitlyObserved,
    int Selected,
    int NotSelected,
    int Dropped,
    int Skipped,
    int Completed,
    int OperationalFailures,
    int InvalidResponses,
    int TransportFailures,
    int IncompatibleCheckpoints,
    int BudgetSkipped,
    int OtherErrors,
    int ExactOutcomeMatches,
    int ConfirmedTruePositives,
    int ConfirmedFalsePositives,
    int ConfirmedFalseNegativesCompleted,
    int ExpectedConfirmedCases,
    int CompletedNonConfirmedCases,
    int ReferencesAuthorizedCount,
    int ReferencesRelevantCount,
    int CitationCasesScored,
    int InvalidContradictionSpans,
    int InvalidVerdictShapes,
    int ChargedModelCalls)
{
    public double? SelectionCoverage => Ratio(Selected, TotalCases);
    public double? OutcomeAccuracyOnCompleted => Ratio(ExactOutcomeMatches, Completed);
    public double? ConfirmedPrecision =>
        Ratio(ConfirmedTruePositives,
            ConfirmedTruePositives + ConfirmedFalsePositives);
    public double? ConfirmedRecallOnCompleted =>
        Ratio(ConfirmedTruePositives,
            ConfirmedTruePositives + ConfirmedFalseNegativesCompleted);
    public double? EndToEndConfirmedRecall =>
        Ratio(ConfirmedTruePositives, ExpectedConfirmedCases);
    public double? ConfirmedFalsePositiveRate =>
        Ratio(ConfirmedFalsePositives, CompletedNonConfirmedCases);
    public double? CitationAuthorizationRate =>
        Ratio(ReferencesAuthorizedCount, CitationCasesScored);
    public double? ProvisionalCitationRelevanceRate =>
        Ratio(ReferencesRelevantCount, CitationCasesScored);

    private static double? Ratio(int numerator, int denominator) =>
        denominator == 0 ? null : (double)numerator / denominator;
}

public sealed record V6EvaluationReport(
    string ScorerVersion,
    string CorpusVersion,
    string CorpusSha256,
    string LabelStatus,
    string Partition,
    string RunId,
    bool AcceptanceAllowed,
    V6LaneScore Overall,
    IReadOnlyList<V6LaneScore> ByCategory,
    IReadOnlyList<V6LaneScore> ByRhetoricalMode,
    IReadOnlyList<V6LaneScore> ByScenarioGroup,
    IReadOnlyList<V6ConfusionCell> ConfusionMatrix,
    IReadOnlyList<V6CaseScore> Cases,
    string InterpretationNote);

public sealed record V6CaseFlip(
    string CaseId,
    ClaimVerificationOutcome First,
    ClaimVerificationOutcome Second);

public sealed record V6DriftReport(
    string CorpusSha256,
    string FirstRunId,
    string SecondRunId,
    int SameCases,
    int ComparableCompletedCases,
    IReadOnlyList<V6CaseFlip> OutcomeFlips,
    IReadOnlyList<string> OperationalOrSelectionChanges,
    string InterpretationNote);

/// <summary>
/// Deterministic metrics only. Callers must never promote the provisional
/// development fixture into an owner-reviewed corpus or unseen holdout.
/// </summary>
public static class V6EvaluationScorer
{
    public static V6EvaluationReport Score(
        V6CorpusFixture fixture,
        string runId,
        IReadOnlyList<V6CaseObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(observations);
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > 128 ||
            fixture.Document.LabelStatus != V6EvaluationContract.LabelStatus ||
            fixture.Document.Partition != V6EvaluationContract.Partition)
            throw new InvalidDataException("V6a cannot score an unlabeled, holdout or owner-certified run.");

        var items = fixture.Document.Cases;
        var known = items.Select(c => c.CaseId).ToHashSet(StringComparer.Ordinal);
        var observed = new Dictionary<string, V6CaseObservation>(StringComparer.Ordinal);
        foreach (var obs in observations)
        {
            if (obs is null || !known.Contains(obs.CaseId) ||
                !observed.TryAdd(obs.CaseId, obs) ||
                !Enum.IsDefined(obs.Selection) ||
                !Enum.IsDefined(obs.OperationalStatus) ||
                obs.ChargedModelCalls is < 0 or > EvidenceVerifierV4.MaxTotalCalls ||
                obs.SupportingReferences is null || obs.OpposingReferences is null ||
                obs.SupportingReferences.Count > 32 ||
                obs.OpposingReferences.Count > 32 ||
                obs.ProblematicText is null || obs.ProblematicText.Length > 4000 ||
                (obs.Outcome.HasValue && !Enum.IsDefined(obs.Outcome.Value)) ||
                (obs.OperationalStatus == VerificationOperationalStatus.Completed) !=
                obs.Outcome.HasValue ||
                (obs.Selection != V6SelectionDisposition.Selected &&
                    (obs.Outcome.HasValue ||
                     obs.OperationalStatus is not (
                         VerificationOperationalStatus.NotStarted or
                         VerificationOperationalStatus.SkippedForBudget))) ||
                (obs.Selection == V6SelectionDisposition.Skipped &&
                    obs.OperationalStatus != VerificationOperationalStatus.SkippedForBudget) ||
                (obs.Selection is V6SelectionDisposition.NotSelected or
                    V6SelectionDisposition.Dropped &&
                    obs.OperationalStatus != VerificationOperationalStatus.NotStarted) ||
                (obs.Selection != V6SelectionDisposition.Selected &&
                    obs.ChargedModelCalls != 0))
                throw new InvalidDataException(
                    "V6a observation has unknown case, conflicting status or invalid call accounting.");
        }

        var scores = new List<V6CaseScore>(items.Count);
        foreach (var item in items)
        {
            if (!observed.TryGetValue(item.CaseId, out var obs))
            {
                scores.Add(new V6CaseScore(
                    item.CaseId, item.ScenarioGroup, item.Category,
                    item.RhetoricalMode, item.ExpectedOutcome,
                    V6SelectionDisposition.NotSelected, false,
                    VerificationOperationalStatus.NotStarted, null,
                    null, null, null, null, null, 0, 0));
                continue;
            }

            bool? authorized = null;
            bool? rubricMatch = null;
            bool? spanValid = null;
            bool? shapeValid = null;
            if (obs.OperationalStatus == VerificationOperationalStatus.Completed)
            {
                var allowed = item.Evidence.Select(e => e.ReferenceId)
                    .ToHashSet(StringComparer.Ordinal);
                authorized = obs.SupportingReferences.Concat(obs.OpposingReferences)
                    .All(allowed.Contains);
                rubricMatch = IsReferenceFit(item, obs) && authorized.Value;
                if (obs.Outcome == ClaimVerificationOutcome.ConfirmedContradiction)
                    spanValid = !string.IsNullOrWhiteSpace(obs.ProblematicText) &&
                        item.ClaimText.Contains(obs.ProblematicText,
                            StringComparison.Ordinal);
                shapeValid = HasValidVerdictShape(obs, spanValid);
            }

            scores.Add(new V6CaseScore(
                item.CaseId, item.ScenarioGroup, item.Category,
                item.RhetoricalMode, item.ExpectedOutcome, obs.Selection,
                true, obs.OperationalStatus, obs.Outcome,
                obs.Outcome is null ? null : obs.Outcome == item.ExpectedOutcome,
                authorized, rubricMatch, spanValid, shapeValid,
                obs.SupportingReferences.Count + obs.OpposingReferences.Count,
                obs.ChargedModelCalls));
        }

        var all = scores.ToArray();
        var category = Enum.GetValues<ClaimCategory>()
            .Select(value => BuildLane(value.ToString(),
                all.Where(c => c.Category == value))).ToArray();
        var rhetorical = Enum.GetValues<ClaimRhetoricalMode>()
            .Select(value => BuildLane(value.ToString(),
                all.Where(c => c.RhetoricalMode == value))).ToArray();
        var groups = all.Select(x => x.ScenarioGroup)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(group => BuildLane(group,
                all.Where(c => c.ScenarioGroup == group))).ToArray();

        var matrix = (from expected in Enum.GetValues<ClaimVerificationOutcome>()
                      from predicted in Enum.GetValues<ClaimVerificationOutcome>()
                      select new V6ConfusionCell(expected, predicted,
                          all.Count(c => c.ExpectedOutcome == expected &&
                              c.ObservedOutcome == predicted))).ToArray();

        return new V6EvaluationReport(
            V6EvaluationContract.ScorerVersion,
            fixture.Document.CorpusVersion, fixture.Sha256,
            fixture.Document.LabelStatus, fixture.Document.Partition,
            runId, false, BuildLane("overall", all),
            Array.AsReadOnly(category), Array.AsReadOnly(rhetorical),
            Array.AsReadOnly(groups), Array.AsReadOnly(matrix),
            Array.AsReadOnly(all),
            "PROVISIONAL synthetic development labels only. No semantic acceptance, " +
            "holdout score, automatic enforcement, or generalization guarantee.");
    }

    /// <summary>
    /// Compare identical case IDs and corpus hash without treating repeated
    /// same-case runs as independent data points or averaging label accuracy.
    /// </summary>
    public static V6DriftReport Compare(
        V6EvaluationReport first, V6EvaluationReport second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.RunId == second.RunId ||
            first.CorpusSha256 != second.CorpusSha256 ||
            first.CorpusVersion != second.CorpusVersion ||
            first.ScorerVersion != second.ScorerVersion ||
            first.Cases.Count != second.Cases.Count ||
            !first.Cases.Select(c => c.CaseId)
                .SequenceEqual(second.Cases.Select(c => c.CaseId)))
            throw new InvalidDataException(
                "V6a drift compares two distinct runs of the exact same frozen input corpus.");

        var flips = new List<V6CaseFlip>();
        var statusChanges = new List<string>();
        var comparable = 0;
        for (int i = 0; i < first.Cases.Count; i++)
        {
            var a = first.Cases[i];
            var b = second.Cases[i];
            if (a.Selection != b.Selection ||
                a.OperationalStatus != b.OperationalStatus)
                statusChanges.Add(a.CaseId);

            if (a.ObservedOutcome is { } left &&
                b.ObservedOutcome is { } right &&
                a.OperationalStatus == VerificationOperationalStatus.Completed &&
                b.OperationalStatus == VerificationOperationalStatus.Completed)
            {
                comparable++;
                if (left != right)
                    flips.Add(new V6CaseFlip(a.CaseId, left, right));
            }
        }
        return new V6DriftReport(
            first.CorpusSha256, first.RunId, second.RunId,
            first.Cases.Count, comparable,
            Array.AsReadOnly(flips.ToArray()),
            Array.AsReadOnly(statusChanges.ToArray()),
            "Same-case flip analysis; repeated passes are NOT independent holdout examples.");
    }

    private static bool HasValidVerdictShape(
        V6CaseObservation observation, bool? spanValid)
    {
        if (observation.SupportingReferences.Count !=
                observation.SupportingReferences.Distinct(StringComparer.Ordinal).Count() ||
            observation.OpposingReferences.Count !=
                observation.OpposingReferences.Distinct(StringComparer.Ordinal).Count())
            return false;

        return observation.Outcome switch
        {
            ClaimVerificationOutcome.ConfirmedContradiction =>
                observation.OpposingReferences.Count > 0 && spanValid == true,
            ClaimVerificationOutcome.ConflictingEvidence =>
                observation.SupportingReferences.Count > 0 &&
                observation.OpposingReferences.Count > 0 &&
                string.IsNullOrEmpty(observation.ProblematicText),
            ClaimVerificationOutcome.NoMaterialIssue =>
                observation.OpposingReferences.Count == 0 &&
                string.IsNullOrEmpty(observation.ProblematicText),
            ClaimVerificationOutcome.InsufficientEvidence =>
                observation.SupportingReferences.Count == 0 &&
                observation.OpposingReferences.Count == 0 &&
                string.IsNullOrEmpty(observation.ProblematicText),
            _ => false
        };
    }

    private static bool IsReferenceFit(V6LabeledCase expected, V6CaseObservation observed)
    {
        if (observed.Outcome != expected.ExpectedOutcome)
            return false;
        var supports = expected.ExpectedSupportingRefs.ToHashSet(StringComparer.Ordinal);
        var opposes = expected.ExpectedOpposingRefs.ToHashSet(StringComparer.Ordinal);
        if (!observed.SupportingReferences.All(supports.Contains) ||
            !observed.OpposingReferences.All(opposes.Contains))
            return false;

        return observed.Outcome switch
        {
            ClaimVerificationOutcome.ConfirmedContradiction =>
                observed.OpposingReferences.Any(opposes.Contains),
            ClaimVerificationOutcome.ConflictingEvidence =>
                observed.OpposingReferences.Any(opposes.Contains) &&
                observed.SupportingReferences.Any(supports.Contains),
            ClaimVerificationOutcome.InsufficientEvidence =>
                observed.SupportingReferences.Count == 0 &&
                observed.OpposingReferences.Count == 0,
            ClaimVerificationOutcome.NoMaterialIssue =>
                observed.OpposingReferences.Count == 0,
            _ => false
        };
    }

    private static V6LaneScore BuildLane(
        string name, IEnumerable<V6CaseScore> candidates)
    {
        var rows = candidates.ToArray();
        var completed = rows.Where(r =>
            r.OperationalStatus == VerificationOperationalStatus.Completed).ToArray();
        // Do not inflate citation accuracy with legitimate uncited jokes,
        // opinions or insufficient-evidence results. A confirmed or
        // conflicting label still enters the denominator if the model
        // wrongly cites nothing.
        var citationCases = completed.Where(r => r.CitedReferenceCount > 0 ||
            r.ExpectedOutcome is ClaimVerificationOutcome.ConfirmedContradiction or
                ClaimVerificationOutcome.ConflictingEvidence ||
            r.ObservedOutcome is ClaimVerificationOutcome.ConfirmedContradiction or
                ClaimVerificationOutcome.ConflictingEvidence).ToArray();
        int truePositives = completed.Count(c =>
            c.ExpectedOutcome == ClaimVerificationOutcome.ConfirmedContradiction &&
            c.ObservedOutcome == ClaimVerificationOutcome.ConfirmedContradiction);
        int falsePositives = completed.Count(c =>
            c.ExpectedOutcome != ClaimVerificationOutcome.ConfirmedContradiction &&
            c.ObservedOutcome == ClaimVerificationOutcome.ConfirmedContradiction);

        return new V6LaneScore(
            name,
            rows.Length,
            rows.Count(r => r.ExplicitlyObserved),
            rows.Count(r => r.Selection == V6SelectionDisposition.Selected),
            rows.Count(r => r.Selection == V6SelectionDisposition.NotSelected),
            rows.Count(r => r.Selection == V6SelectionDisposition.Dropped),
            rows.Count(r => r.Selection == V6SelectionDisposition.Skipped),
            completed.Length,
            rows.Count(r => r.ExplicitlyObserved &&
                r.Selection == V6SelectionDisposition.Selected &&
                r.OperationalStatus != VerificationOperationalStatus.Completed),
            rows.Count(r => r.OperationalStatus == VerificationOperationalStatus.InvalidResponse),
            rows.Count(r => r.OperationalStatus == VerificationOperationalStatus.TransportFailure),
            rows.Count(r => r.OperationalStatus == VerificationOperationalStatus.IncompatibleCheckpoint),
            rows.Count(r => r.OperationalStatus == VerificationOperationalStatus.SkippedForBudget),
            rows.Count(r => r.OperationalStatus == VerificationOperationalStatus.Error),
            completed.Count(r => r.OutcomeMatchesProvisionalLabel == true),
            truePositives,
            falsePositives,
            completed.Count(c =>
                c.ExpectedOutcome == ClaimVerificationOutcome.ConfirmedContradiction &&
                c.ObservedOutcome != ClaimVerificationOutcome.ConfirmedContradiction),
            rows.Count(c =>
                c.ExpectedOutcome == ClaimVerificationOutcome.ConfirmedContradiction),
            completed.Count(c =>
                c.ExpectedOutcome != ClaimVerificationOutcome.ConfirmedContradiction),
            citationCases.Count(c => c.ReferencesAuthorized == true),
            citationCases.Count(c => c.ReferencesMatchProvisionalRubric == true),
            citationCases.Length,
            completed.Count(c => c.ContradictionSpanValid == false),
            completed.Count(c => c.VerdictShapeValid == false),
            rows.Sum(c => c.ChargedModelCalls));
    }
}

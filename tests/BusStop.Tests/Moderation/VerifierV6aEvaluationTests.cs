using System.Text.Json;
using BusStop.Moderation.Evaluation;
using BusStop.Moderation.Verification;
using Xunit;

namespace BusStop.Tests.Moderation;

public sealed class VerifierV6aEvaluationTests
{
    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "moderator",
                "corpus", "v6a-synthetic-provisional-v1.json");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Missing committed V6a synthetic corpus; test must run in BusStop repository.");
    }

    private static V6CorpusFixture Fixture() =>
        V6CorpusReader.LoadProvisionalSynthetic(File.ReadAllText(FixturePath()));

    private static V6CaseObservation Completed(
        V6LabeledCase c, ClaimVerificationOutcome? outcome = null,
        IReadOnlyList<string>? support = null,
        IReadOnlyList<string>? oppose = null, int calls = 1) =>
        new(c.CaseId, V6SelectionDisposition.Selected,
            VerificationOperationalStatus.Completed, outcome ?? c.ExpectedOutcome,
            support ?? c.ExpectedSupportingRefs,
            oppose ?? c.ExpectedOpposingRefs,
            (outcome ?? c.ExpectedOutcome) ==
                ClaimVerificationOutcome.ConfirmedContradiction ? c.ClaimText : "",
            calls);

    [Fact]
    public void Versioned_fixture_is_synthetic_provisional_and_covers_all_outcomes()
    {
        var fixture = Fixture();
        Assert.Equal(V6EvaluationContract.CorpusVersion,
            fixture.Document.CorpusVersion);
        Assert.Equal(V6EvaluationContract.LabelStatus,
            fixture.Document.LabelStatus);
        Assert.Equal(V6EvaluationContract.Partition,
            fixture.Document.Partition);
        Assert.Equal(20, fixture.Document.Cases.Count);
        Assert.Equal(64, fixture.Sha256.Length);
        Assert.Equal(20, fixture.Document.Cases.Select(c => c.CaseId)
            .Distinct(StringComparer.Ordinal).Count());

        foreach (var outcome in Enum.GetValues<ClaimVerificationOutcome>())
            Assert.Contains(fixture.Document.Cases, c => c.ExpectedOutcome == outcome);
        foreach (var category in Enum.GetValues<ClaimCategory>())
            Assert.Contains(fixture.Document.Cases, c => c.Category == category);
        foreach (var mode in new[] { ClaimRhetoricalMode.Humorous,
                     ClaimRhetoricalMode.Quoted,
                     ClaimRhetoricalMode.Question,
                     ClaimRhetoricalMode.Conditional })
            Assert.Contains(fixture.Document.Cases, c => c.RhetoricalMode == mode);
    }

    [Fact]
    public void Perfectly_matching_fake_observations_are_still_not_accepted_ground_truth()
    {
        var f = Fixture();
        var observations = f.Document.Cases.Select(c => Completed(c)).ToArray();
        var result = V6EvaluationScorer.Score(f, "synthetic-run-01", observations);

        Assert.False(result.AcceptanceAllowed);
        Assert.Equal(f.Sha256, result.CorpusSha256);
        Assert.Equal(20, result.Overall.TotalCases);
        Assert.Equal(20, result.Overall.Completed);
        Assert.Equal(20, result.Overall.ExactOutcomeMatches);
        Assert.Equal(20, result.Overall.ReferencesAuthorizedCount);
        Assert.Equal(20, result.Overall.ReferencesRelevantCount);
        Assert.Equal(0, result.Overall.ConfirmedFalsePositives);
        Assert.Equal(1d, result.Overall.ConfirmedPrecision);
        Assert.Equal(1d, result.Overall.EndToEndConfirmedRecall);
        Assert.Equal(1d, result.Overall.OutcomeAccuracyOnCompleted);
        Assert.Contains("PROVISIONAL", result.InterpretationNote);
        Assert.Equal(5, result.ByCategory.Count);
        Assert.Equal(Enum.GetValues<ClaimRhetoricalMode>().Length,
            result.ByRhetoricalMode.Count);
        Assert.Equal(16, result.ConfusionMatrix.Count);
        Assert.Equal(20, result.ConfusionMatrix.Sum(c => c.Count));
    }

    [Fact]
    public void Missing_and_dropped_claims_remain_in_end_to_end_recall_denominator()
    {
        var f = Fixture();
        var positives = f.Document.Cases.Where(c =>
            c.ExpectedOutcome == ClaimVerificationOutcome.ConfirmedContradiction)
            .ToArray();
        var observed = new[]
        {
            Completed(positives[0]),
            new V6CaseObservation(
                positives[1].CaseId, V6SelectionDisposition.Dropped,
                VerificationOperationalStatus.NotStarted, null,
                Array.Empty<string>(), Array.Empty<string>(), "", 0),
            new V6CaseObservation(
                positives[2].CaseId, V6SelectionDisposition.Skipped,
                VerificationOperationalStatus.SkippedForBudget, null,
                Array.Empty<string>(), Array.Empty<string>(), "", 0)
        };
        var result = V6EvaluationScorer.Score(f, "coverage-run", observed);
        Assert.Equal(f.Document.Cases.Count - observed.Length,
            result.Overall.TotalCases - result.Overall.ExplicitlyObserved);
        Assert.Equal(1, result.Overall.Completed);
        Assert.Equal(1, result.Overall.Dropped);
        Assert.Equal(1, result.Overall.Skipped);
        Assert.Equal(1, result.Overall.ConfirmedTruePositives);
        Assert.Equal(1d, result.Overall.ConfirmedPrecision);
        Assert.Equal(1d, result.Overall.ConfirmedRecallOnCompleted);
        Assert.Equal(1d / positives.Length, result.Overall.EndToEndConfirmedRecall);
        Assert.Equal(1d / f.Document.Cases.Count, result.Overall.SelectionCoverage);
        Assert.True(result.Overall.EndToEndConfirmedRecall <
                    result.Overall.ConfirmedRecallOnCompleted);
    }

    [Fact]
    public void Unsupported_contradiction_on_a_humorous_line_counts_as_false_positive()
    {
        var f = Fixture();
        var humor = f.Document.Cases.Single(c => c.CaseId == "other-humor");
        var inappropriate = Completed(humor,
            ClaimVerificationOutcome.ConfirmedContradiction,
            oppose: new[] { "src-1" });
        var result = V6EvaluationScorer.Score(f, "false-positive-run",
            new[] { inappropriate });
        var lane = result.ByRhetoricalMode.Single(c =>
            c.Lane == ClaimRhetoricalMode.Humorous.ToString());
        Assert.Equal(1, lane.ConfirmedFalsePositives);
        Assert.Equal(1d, lane.ConfirmedFalsePositiveRate);
        Assert.Equal(0, lane.ExactOutcomeMatches);
        Assert.Equal(false, result.Cases.Single(c =>
            c.CaseId == humor.CaseId).ReferencesMatchProvisionalRubric);
        Assert.Equal(0d, lane.ConfirmedPrecision);
    }

    [Fact]
    public void Authorized_but_irrelevant_citations_are_not_semantic_support()
    {
        var f = Fixture();
        var c = f.Document.Cases.Single(c =>
            c.CaseId == "history-superseded-correction");
        var wrongSource = Completed(c, support: new[] { "history-1" });
        var result = V6EvaluationScorer.Score(f, "irrelevant-citation",
            new[] { wrongSource });
        var score = result.Cases.Single(x => x.CaseId == c.CaseId);
        Assert.True(score.OutcomeMatchesProvisionalLabel);
        Assert.True(score.ReferencesAuthorized);
        Assert.False(score.ReferencesMatchProvisionalRubric);
        Assert.Equal(1, result.Overall.ReferencesAuthorizedCount);
        Assert.Equal(0, result.Overall.ReferencesRelevantCount);
    }

    [Fact]
    public void Invented_reference_is_counted_not_silently_promoted_to_authorized()
    {
        var f = Fixture();
        var c = f.Document.Cases.Single(x =>
            x.CaseId == "source-exact-contradiction");
        var observed = Completed(c, oppose: new[] { "private-never-authorized" });
        var report = V6EvaluationScorer.Score(f, "invented-reference",
            new[] { observed });
        var score = report.Cases.Single(x => x.CaseId == c.CaseId);
        Assert.True(score.OutcomeMatchesProvisionalLabel);
        Assert.False(score.ReferencesAuthorized);
        Assert.False(score.ReferencesMatchProvisionalRubric);
        Assert.Equal(0, report.Overall.ReferencesAuthorizedCount);
        Assert.Equal(0, report.Overall.ReferencesRelevantCount);
    }

    [Fact]
    public void Technical_failure_is_not_counted_as_an_epistemic_judgment()
    {
        var f = Fixture();
        var c = f.Document.Cases[0];
        var report = V6EvaluationScorer.Score(f, "transport-failure",
            new[]
            {
                new V6CaseObservation(c.CaseId, V6SelectionDisposition.Selected,
                    VerificationOperationalStatus.TransportFailure, null,
                    Array.Empty<string>(), Array.Empty<string>(), "", 1)
            });
        Assert.Equal(1, report.Overall.OperationalFailures);
        Assert.Equal(0, report.Overall.Completed);
        Assert.Equal(0, report.ConfusionMatrix.Sum(x => x.Count));
        Assert.Null(report.Overall.OutcomeAccuracyOnCompleted);
        Assert.Null(report.Overall.ConfirmedPrecision);
    }

    [Theory]
    [InlineData("source-exact-contradiction", V6SelectionDisposition.Selected,
        VerificationOperationalStatus.NotStarted, true)]
    [InlineData("source-exact-contradiction", V6SelectionDisposition.Dropped,
        VerificationOperationalStatus.Completed, true)]
    [InlineData("source-exact-contradiction", V6SelectionDisposition.Skipped,
        VerificationOperationalStatus.NotStarted, true)]
    public void Invalid_selection_and_operational_pairs_fail_closed(
        string caseId, V6SelectionDisposition selection,
        VerificationOperationalStatus status, bool hasOutcome)
    {
        var f = Fixture();
        var c = f.Document.Cases.Single(x => x.CaseId == caseId);
        var invalid = new V6CaseObservation(
            c.CaseId, selection, status,
            hasOutcome ? c.ExpectedOutcome : null,
            Array.Empty<string>(), Array.Empty<string>(), "", 0);
        Assert.Throws<InvalidDataException>(
            () => V6EvaluationScorer.Score(f, "invalid-pair", new[] { invalid }));
    }

    [Fact]
    public void Duplicate_or_unknown_case_ids_fail_closed()
    {
        var f = Fixture();
        var obs = Completed(f.Document.Cases[0]);
        Assert.Throws<InvalidDataException>(() =>
            V6EvaluationScorer.Score(f, "duplicate", new[] { obs, obs }));
        Assert.Throws<InvalidDataException>(() =>
            V6EvaluationScorer.Score(f, "unknown",
                new[] { obs with { CaseId = "unknown-case" } }));
    }

    [Fact]
    public void Corpus_loader_rejects_mutated_labels_unknown_properties_and_holdout()
    {
        var raw = File.ReadAllText(FixturePath());
        Assert.Throws<InvalidDataException>(() =>
            V6CorpusReader.LoadProvisionalSynthetic(raw.Replace(
                "provisional-owner-review-required", "owner-reviewed-frozen",
                StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() =>
            V6CorpusReader.LoadProvisionalSynthetic(raw.Replace(
                "\"partition\": \"development\"", "\"partition\": \"holdout\"",
                StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() =>
            V6CorpusReader.LoadProvisionalSynthetic(raw.Replace(
                "\"caseId\": \"source-exact-contradiction\"",
                "\"caseId\": \"source-exact-contradiction\", \"injected\": true",
                StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() =>
            V6CorpusReader.LoadProvisionalSynthetic(raw.Replace(
                "\"referenceId\": \"src-1\"",
                "\"referenceId\": \"forged-authorization\"",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void Drift_counts_exact_case_flips_without_inflating_independent_sample_size()
    {
        var f = Fixture();
        var first = f.Document.Cases.Select(c => Completed(c)).ToArray();
        var second = f.Document.Cases.Select(c =>
            c.CaseId == "source-exact-contradiction"
            ? Completed(c, ClaimVerificationOutcome.InsufficientEvidence,
                oppose: Array.Empty<string>())
            : Completed(c)).ToArray();
        var firstReport = V6EvaluationScorer.Score(f, "identical-input-A", first);
        var secondReport = V6EvaluationScorer.Score(f, "identical-input-B", second);
        var drift = V6EvaluationScorer.Compare(firstReport, secondReport);
        Assert.Equal(f.Document.Cases.Count, drift.SameCases);
        Assert.Equal(f.Document.Cases.Count, drift.ComparableCompletedCases);
        Assert.Equal("source-exact-contradiction",
            Assert.Single(drift.OutcomeFlips).CaseId);
        Assert.Empty(drift.OperationalOrSelectionChanges);
        Assert.Contains("NOT independent", drift.InterpretationNote);
        Assert.Throws<InvalidDataException>(() =>
            V6EvaluationScorer.Compare(firstReport, firstReport));
    }

    [Fact]
    public void Status_changes_and_case_flips_are_independent_metrics()
    {
        var f = Fixture();
        var case1 = f.Document.Cases[0];
        var pass = V6EvaluationScorer.Score(f, "A",
            new[] { Completed(case1) });
        var interrupted = V6EvaluationScorer.Score(f, "B",
            new[] { new V6CaseObservation(case1.CaseId,
                V6SelectionDisposition.Selected,
                VerificationOperationalStatus.TransportFailure, null,
                Array.Empty<string>(), Array.Empty<string>(), "", 1) });
        var drift = V6EvaluationScorer.Compare(pass, interrupted);
        Assert.Equal(0, drift.ComparableCompletedCases);
        Assert.Empty(drift.OutcomeFlips);
        Assert.Equal(case1.CaseId,
            Assert.Single(drift.OperationalOrSelectionChanges));
    }
}

using BusStop.Engine.Evaluation;
using Xunit;

namespace BusStop.Tests.Moderation;

/// <summary>
/// All V7d tests are synthetic, model-free and repository-safe.
/// The real 35-turn V7c transcript stays local and is identified by hash only.
/// </summary>
public sealed class VerifierV7dProgressLabTests
{
    private static readonly string FakeTranscriptSha = new('a', 64);

    private static V7dInputTurn[] Turns(params string[] text) =>
        text.Select((t, i) => new V7dInputTurn(
            i + 1, "speaker-" + (i % 3), t)).ToArray();

    [Fact]
    public void Exact_copy_across_different_speakers_is_a_review_candidate()
    {
        var rows = Turns(
            "Some workers need training before this factory can reopen.",
            "I hope those workers get fair wages and practical lessons.",
            "Some WORKERS need training before this factory can reopen!");
        var analysis = V7dProgressAnalyzer.Analyze(rows, FakeTranscriptSha);
        var copied = analysis.Signals[2];
        Assert.True(copied.ExactPriorUtterance);
        Assert.Equal(1L, copied.ExactReferenceSequence);
        Assert.False(copied.LexicalOverlapCandidate);
        Assert.False(copied.LoopWindowCandidate);

        var safe = V7dProgressAnalyzer.ExportSafe(analysis, rows);
        Assert.Equal(1, safe.ExactCopyCandidates);
        Assert.Equal(3, safe.DistinctSpeakers);
        Assert.False(safe.HumanReviewCompleted);
        Assert.False(safe.BehavioralInterventionAuthorized);
        Assert.False(safe.SemanticAccuracyAccepted);

        var json = System.Text.Json.JsonSerializer.Serialize(safe);
        Assert.DoesNotContain("factory", json);
        Assert.DoesNotContain("speaker-", json);
        Assert.DoesNotContain("ExactReferenceSequence", json);
    }

    [Fact]
    public void Bounded_overlap_is_a_signal_not_an_authoritative_semantic_judgment()
    {
        var rows = Turns(
            "These workers must learn the new machines and earn better wages at the factory.",
            "These workers must learn the newer machines and earn better wages at the factory.");
        var analysis = V7dProgressAnalyzer.Analyze(rows, FakeTranscriptSha);
        Assert.False(analysis.Signals[1].ExactPriorUtterance);
        Assert.True(analysis.Signals[1].LexicalOverlapCandidate);
        Assert.Equal(1L, analysis.Signals[1].LexicalReferenceSequence);
        Assert.False(V7dProgressAnalyzer.ExportSafe(analysis, rows)
            .SemanticAccuracyAccepted);
    }

    [Fact]
    public void Broad_echo_and_sustained_loop_pressure_are_review_cues_not_verdicts()
    {
        var rows = Turns(
            "Factory workers need training and fair wages when the plant reopens next year.",
            "The factory may reopen next year but workers deserve wages and practical retraining.",
            "Those workers need real training at the factory and reliable wages next year.",
            "If the factory reopens, the workers will need training and better wages.",
            "The workers might receive training, yet wages at that factory remain uncertain.",
            "Training workers for the reopening factory must not disguise lower wages.");
        var analysis = V7dProgressAnalyzer.Analyze(rows, FakeTranscriptSha);
        var safe = V7dProgressAnalyzer.ExportSafe(analysis, rows);

        Assert.True(safe.BroadLexicalEchoCandidates > 0);
        Assert.True(safe.TurnsInsideCandidateLoopWindows > 0);
        Assert.False(safe.SemanticAccuracyAccepted);
        Assert.False(safe.BehavioralInterventionAuthorized);
        Assert.All(analysis.Signals, x =>
            Assert.False(x.ExactPriorUtterance));

        // Shared terminology on its own does NOT prove unproductive speech.
        Assert.Contains("REVIEW CANDIDATES", safe.Warning);
    }

    [Fact]
    public void Healthy_callback_and_deliberate_quote_are_not_auto_labeled_failures()
    {
        var rows = Turns(
            "These workers must learn the new machines and earn better wages at the factory.",
            "These workers must learn the new machines and earn better wages at the factory.");
        var analysis = V7dProgressAnalyzer.Analyze(rows, FakeTranscriptSha);
        var worksheet = V7dProgressCalibration.CreateBlank(analysis);
        var review = Reviewed(worksheet, index =>
            worksheet.Turns[index] with
            {
                UnproductiveLoop = false,
                HealthyDisagreementOrCallback = index == 1,
                MeaningfulNewMove = index == 0,
                InventedSharedHistory = false,
                Notes = "Second line is a deliberately quoted callback."
            });

        var score = V7dProgressCalibration.CalibrateDevelopment(
            analysis, review);
        Assert.Equal(1, score.FalsePositiveCandidates);
        Assert.Equal(0, score.TruePositiveCandidates);
        Assert.Equal(1.0, score.HealthyDisagreementFalseAlarmRate);
        Assert.Equal(0.0, score.CandidatePrecision);
        Assert.Null(score.CandidateRecall);
        Assert.False(score.SemanticAccuracyAccepted);
        Assert.False(score.MinimumDevelopmentSampleReached);
    }

    [Fact]
    public void Blank_worksheet_cannot_be_self_promoted_to_human_review()
    {
        var rows = Turns("This is a new discussion.", "We disagree about wages.");
        var analysis = V7dProgressAnalyzer.Analyze(rows, FakeTranscriptSha);
        var worksheet = V7dProgressCalibration.CreateBlank(analysis);
        Assert.Equal(2, worksheet.Turns.Count);
        Assert.All(worksheet.Turns, row => Assert.Null(row.UnproductiveLoop));
        Assert.True(V7dProgressCalibration.CreateBlank(
            V7dProgressAnalyzer.Analyze(
                Turns("An original thought", "An original thought"),
                FakeTranscriptSha)).Turns[1].ExactCopyCandidate);
        Assert.Throws<InvalidDataException>(() =>
            V7dProgressCalibration.CalibrateDevelopment(
                analysis, worksheet));
    }

    [Fact]
    public void Complete_self_review_reports_denominators_but_cannot_accept_semantics()
    {
        var words = Enumerable.Range(0, 40)
            .Select(n => "A different hypothetical statement number " + n + ".")
            .ToArray();
        var turns = Turns(words);
        var analysis = V7dProgressAnalyzer.Analyze(turns, FakeTranscriptSha);
        var worksheet = V7dProgressCalibration.CreateBlank(analysis);
        var review = Reviewed(worksheet, i =>
            worksheet.Turns[i] with
            {
                UnproductiveLoop = i < 20,
                HealthyDisagreementOrCallback = i >= 20 && i < 30,
                MeaningfulNewMove = i >= 20,
                InventedSharedHistory = false,
                Notes = null
            });
        var safe = V7dProgressCalibration.CalibrateDevelopment(
            analysis, review);
        Assert.Equal(40, safe.ReviewedTurns);
        Assert.Equal(20, safe.HumanLabeledLoopTurns);
        Assert.Equal(20, safe.HumanLabeledNonloopTurns);
        Assert.Equal(10, safe.HumanLabeledHealthyDisagreementOrCallbacks);
        Assert.True(safe.MinimumDevelopmentSampleReached);
        Assert.True(safe.OwnerSelfAttested);
        Assert.False(safe.IndependentReviewAuthenticated);
        Assert.False(safe.HoldoutEvaluated);
        Assert.False(safe.ThresholdsFrozen);
        Assert.False(safe.SemanticAccuracyAccepted);
        Assert.False(safe.BehavioralInterventionAuthorized);
    }

    [Fact]
    public void Calibration_rejects_text_binding_tampering_and_conflicting_labels()
    {
        var rows = Turns("First observation", "A second observation");
        var analysis = V7dProgressAnalyzer.Analyze(rows, FakeTranscriptSha);
        var worksheet = V7dProgressCalibration.CreateBlank(analysis);
        var review = Reviewed(worksheet, i =>
            worksheet.Turns[i] with
            {
                UnproductiveLoop = false,
                HealthyDisagreementOrCallback = false,
                MeaningfulNewMove = true,
                InventedSharedHistory = false,
                Notes = null
            });

        Assert.Throws<InvalidDataException>(() =>
            V7dProgressCalibration.CalibrateDevelopment(analysis,
                review with { TranscriptSha256 = new string('b', 64) }));

        var corrupted = review.Turns.ToArray();
        corrupted[0] = corrupted[0] with { TextSha256 = new string('f', 64) };
        Assert.Throws<InvalidDataException>(() =>
            V7dProgressCalibration.CalibrateDevelopment(analysis,
                review with { Turns = corrupted }));

        corrupted[0] = review.Turns[0] with
        {
            BroadLexicalEchoCandidate =
                !review.Turns[0].BroadLexicalEchoCandidate
        };
        Assert.Throws<InvalidDataException>(() =>
            V7dProgressCalibration.CalibrateDevelopment(analysis,
                review with { Turns = corrupted }));

        corrupted[0] = review.Turns[0] with
        {
            UnproductiveLoop = true,
            HealthyDisagreementOrCallback = true
        };
        Assert.Throws<InvalidDataException>(() =>
            V7dProgressCalibration.CalibrateDevelopment(analysis,
                review with { Turns = corrupted }));
    }

    [Fact]
    public void Empty_invalid_or_overlong_transcript_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            V7dProgressAnalyzer.Analyze(Array.Empty<V7dInputTurn>(),
                FakeTranscriptSha));
        Assert.Throws<InvalidDataException>(() =>
            V7dProgressAnalyzer.Analyze(Turns("Speech"), "not-a-sha"));
        Assert.Throws<InvalidDataException>(() =>
            V7dProgressAnalyzer.Analyze(
                new[] { new V7dInputTurn(2, "first", "Speech") },
                FakeTranscriptSha));
        Assert.Throws<InvalidDataException>(() =>
            V7dProgressAnalyzer.Analyze(
                new[] { new V7dInputTurn(1, "first", new string('x', 20001)) },
                FakeTranscriptSha));
    }

    private static V7dProgressReviewWorksheet Reviewed(
        V7dProgressReviewWorksheet template,
        Func<int, V7dProgressTurnReview> makeTurn) =>
        template with
        {
            ReviewerAlias = "owner-test",
            ReviewedAtUtc = DateTimeOffset.UtcNow,
            Turns = Enumerable.Range(0, template.Turns.Count)
                .Select(makeTurn).ToArray()
        };
}

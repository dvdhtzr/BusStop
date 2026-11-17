using System.Text.RegularExpressions;

namespace BusStop.Engine.Evaluation;

/// <summary>
/// PRIVATE, manually completed annotations. There is no automatic labeling,
/// independent-review authentication, holdout unlock or enforcement path.
/// </summary>
public sealed record V7dProgressTurnReview(
    long Sequence,
    string TextSha256,
    bool? UnproductiveLoop,
    bool? HealthyDisagreementOrCallback,
    bool? MeaningfulNewMove,
    bool? InventedSharedHistory,
    string? Notes);

public sealed record V7dProgressReviewWorksheet(
    string ContractVersion,
    string TranscriptSha256,
    string Partition,
    string? ReviewerAlias,
    DateTimeOffset? ReviewedAtUtc,
    string Attestation,
    IReadOnlyList<V7dProgressTurnReview> Turns);

/// <summary>
/// SAFE, descriptive development-only calibration. No text, speaker, sequence,
/// private case IDs or individual reviewer annotations are exported.
/// </summary>
public sealed record V7dSafeCalibrationReport(
    string ContractVersion,
    string TranscriptSha256,
    string Partition,
    int ReviewedTurns,
    int HumanLabeledLoopTurns,
    int HumanLabeledNonloopTurns,
    int HumanLabeledHealthyDisagreementOrCallbacks,
    int HumanLabeledMeaningfulNewMoves,
    int HumanLabeledInventedSharedHistories,
    int TruePositiveCandidates,
    int FalsePositiveCandidates,
    int FalseNegativeCandidates,
    int TrueNegativeCandidates,
    double? CandidatePrecision,
    double? CandidateRecall,
    double? HealthyDisagreementFalseAlarmRate,
    bool MinimumDevelopmentSampleReached,
    bool OwnerSelfAttested,
    bool IndependentReviewAuthenticated,
    bool HoldoutEvaluated,
    bool ThresholdsFrozen,
    bool SemanticAccuracyAccepted,
    bool BehavioralInterventionAuthorized,
    string Interpretation);

public static class V7dProgressCalibration
{
    public const string Version = "busstop-v7d-review-v1";
    public const string DevelopmentPartition = "Development";
    public const string Attestation =
        "I personally read and labeled every turn of this private BusStop " +
        "conversation. These annotations are my own judgments, not model outputs.";

    public const int MinimumLabeledLoopTurns = 20;
    public const int MinimumLabeledNonloopTurns = 20;
    public const int MinimumHealthyControls = 10;

    public static V7dProgressReviewWorksheet CreateBlank(
        V7dPrivateAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.ContractVersion != V7dProgressContract.Version)
            throw new InvalidDataException("Unsupported V7d analysis contract.");

        return new V7dProgressReviewWorksheet(
            Version, analysis.TranscriptSha256, DevelopmentPartition,
            null, null, Attestation,
            Array.AsReadOnly(analysis.Signals.Select(s =>
                new V7dProgressTurnReview(s.Sequence, s.TextSha256,
                    null, null, null, null, null)).ToArray()));
    }

    public static V7dSafeCalibrationReport CalibrateDevelopment(
        V7dPrivateAnalysis analysis, V7dProgressReviewWorksheet reviewed)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(reviewed);
        if (analysis.ContractVersion != V7dProgressContract.Version ||
            reviewed.ContractVersion != Version ||
            reviewed.Partition != DevelopmentPartition ||
            reviewed.TranscriptSha256 != analysis.TranscriptSha256 ||
            reviewed.Attestation != Attestation ||
            reviewed.Turns is null ||
            reviewed.Turns.Count != analysis.Signals.Count ||
            string.IsNullOrWhiteSpace(reviewed.ReviewerAlias) ||
            reviewed.ReviewerAlias.Length > 80 ||
            !Regex.IsMatch(reviewed.ReviewerAlias,
                "^[a-zA-Z0-9][a-zA-Z0-9-]*$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)) ||
            reviewed.ReviewedAtUtc is null ||
            reviewed.ReviewedAtUtc.Value.Offset != TimeSpan.Zero ||
            reviewed.ReviewedAtUtc.Value >
                DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException(
                "V7d development review is missing attestation or binding.");

        int tp = 0, fp = 0, fn = 0, tn = 0;
        int positive = 0, negative = 0, healthy = 0;
        int flaggedHealthy = 0, novel = 0, invented = 0;

        for (int index = 0; index < reviewed.Turns.Count; index++)
        {
            var label = reviewed.Turns[index];
            var signal = analysis.Signals[index];
            if (label is null || label.Sequence != signal.Sequence ||
                label.TextSha256 != signal.TextSha256 ||
                label.UnproductiveLoop is null ||
                label.HealthyDisagreementOrCallback is null ||
                label.MeaningfulNewMove is null ||
                label.InventedSharedHistory is null ||
                label.Notes is { Length: > 2000 } ||
                label.UnproductiveLoop == true &&
                label.HealthyDisagreementOrCallback == true)
                throw new InvalidDataException(
                    "Unreviewed, altered or contradictory V7d labels.");

            var predicted = signal.ExactPriorUtterance ||
                signal.LexicalOverlapCandidate ||
                signal.LoopWindowCandidate;
            var truth = label.UnproductiveLoop.Value;
            if (truth) positive++; else negative++;
            if (label.HealthyDisagreementOrCallback.Value)
            {
                healthy++;
                if (predicted) flaggedHealthy++;
            }
            if (label.MeaningfulNewMove.Value) novel++;
            if (label.InventedSharedHistory.Value) invented++;

            if (predicted && truth) tp++;
            else if (predicted) fp++;
            else if (truth) fn++;
            else tn++;
        }

        // Undefined denominators remain null, never presented as perfect.
        var precision = tp + fp > 0 ? (double?)Math.Round(
            (double)tp / (tp + fp), 4) : null;
        var recall = tp + fn > 0 ? (double?)Math.Round(
            (double)tp / (tp + fn), 4) : null;
        var healthyFalseAlarm = healthy > 0 ? (double?)Math.Round(
            (double)flaggedHealthy / healthy, 4) : null;
        var adequate = positive >= MinimumLabeledLoopTurns &&
            negative >= MinimumLabeledNonloopTurns &&
            healthy >= MinimumHealthyControls;

        return new V7dSafeCalibrationReport(
            Version, analysis.TranscriptSha256, DevelopmentPartition,
            reviewed.Turns.Count, positive, negative, healthy, novel,
            invented, tp, fp, fn, tn,
            precision, recall, healthyFalseAlarm, adequate,
            OwnerSelfAttested: true,
            IndependentReviewAuthenticated: false,
            HoldoutEvaluated: false,
            ThresholdsFrozen: false,
            SemanticAccuracyAccepted: false,
            BehavioralInterventionAuthorized: false,
            adequate
                ? "Development-only candidate measurements; formal thresholds " +
                  "and independent holdout remain separately required. " +
                  "No semantic or behavioral acceptance is conferred."
                : "Development review is below minimum lane denominators. " +
                  "Do NOT interpret candidate precision/recall as accepted " +
                  "performance, tune on a holdout, or authorize live nudges.");
    }
}

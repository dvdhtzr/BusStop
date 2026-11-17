using BusStop.Moderation.Evaluation;

namespace BusStop.Engine.Evaluation;

/// <summary>
/// Safe to share as DEVELOPMENT diagnostics: NO prompts, excerpts, text spans,
/// raw responses, per-case IDs, private paths, owner annotations or holdouts.
/// Do not mistake it for owner-reviewed semantic acceptance.
/// </summary>
public sealed record V6bSafeRunSummary(
    string ContractVersion,
    string CorpusVersion,
    string CorpusSha256,
    string LabelStatus,
    string Partition,
    string RunId,
    string ModelName,
    string ModelDigest,
    bool AcceptanceAllowed,
    V6LaneScore Overall,
    IReadOnlyList<V6LaneScore> ByCategory,
    IReadOnlyList<V6LaneScore> ByRhetoricalMode,
    IReadOnlyList<V6LaneScore> ByScenarioGroup,
    IReadOnlyList<V6ConfusionCell> ConfusionMatrix,
    string InterpretationNote);

public sealed record V6bSafeDriftSummary(
    string CorpusSha256, string FirstRunId, string SecondRunId,
    int SameCases, int ComparableCompletedCases,
    int OutcomeFlipCount, int SelectionOrOperationalChangeCount,
    bool AcceptanceAllowed, string InterpretationNote);

public static class V6bSafeExporter
{
    public static V6bSafeRunSummary From(V6bLabRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var score = run.Score;
        var audit = run.PrivateAudit;
        if (score.AcceptanceAllowed ||
            audit.LabelStatus != V6EvaluationContract.LabelStatus ||
            score.LabelStatus != V6EvaluationContract.LabelStatus ||
            score.Partition != V6EvaluationContract.Partition ||
            score.CorpusSha256 != audit.CorpusSha256 ||
            score.RunId != audit.RunId ||
            audit.ContractVersion != V6bLabContract.Version)
            throw new InvalidDataException(
                "V6b safe export refuses incompatible or acceptance-enabled audit.");

        return new V6bSafeRunSummary(
            V6bLabContract.Version, score.CorpusVersion,
            score.CorpusSha256, score.LabelStatus, score.Partition,
            score.RunId, audit.ModelName, audit.ModelDigest, false,
            score.Overall, score.ByCategory, score.ByRhetoricalMode,
            score.ByScenarioGroup, score.ConfusionMatrix,
            "Synthetic provisional DEVELOPMENT metrics only; case-level audits stay private. " +
            "This does not estimate live selection coverage or certify semantic readiness.");
    }

    public static V6bSafeDriftSummary Compare(
        V6bLabRun first, V6bLabRun second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.PrivateAudit.ModelName != second.PrivateAudit.ModelName ||
            first.PrivateAudit.ModelDigest != second.PrivateAudit.ModelDigest)
            throw new InvalidDataException(
                "V6b only compares runs pinned to the exact same local model digest.");
        _ = From(first);
        _ = From(second);
        var drift = V6EvaluationScorer.Compare(first.Score, second.Score);
        return new V6bSafeDriftSummary(
            drift.CorpusSha256, drift.FirstRunId, drift.SecondRunId,
            drift.SameCases, drift.ComparableCompletedCases,
            drift.OutcomeFlips.Count, drift.OperationalOrSelectionChanges.Count,
            false,
            "Same-case drift on synthetic provisional development fixtures; " +
            "repeated runs are not independent holdout cases.");
    }
}

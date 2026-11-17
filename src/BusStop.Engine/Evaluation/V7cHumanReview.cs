using System.Text.RegularExpressions;

namespace BusStop.Engine.Evaluation;

/// <summary>
/// PRIVATE worksheet: full natural utterances, possible hallucinations and
/// human notes. None of this belongs in an aggregate/public artifact.
/// </summary>
public sealed record V7cHumanTurnReview(
    long Sequence,
    string SpeakerId,
    string UtteranceText,
    bool? InventedPersonalHistory,
    bool? DialogueMisattribution,
    bool? UnsupportedFactualCertainty,
    bool? UnnaturalRepetition,
    bool? TimidOrFormulaicSpeech,
    bool? HealthyCharacterDevelopment,
    string? Notes);

public sealed record V7cHumanReviewWorksheet(
    string Version,
    string TranscriptSha256,
    string? ReviewerAlias,
    DateTimeOffset? ReviewedAtUtc,
    string ReviewAttestation,
    int? NaturalConversationScore,
    int? CharacterDistinctnessScore,
    int? HumorAndSpontaneityScore,
    int? ConstructiveDisagreementScore,
    int? HistoryCoherenceScore,
    int? ModeratorNonIntrusionScore,
    IReadOnlyList<V7cHumanTurnReview> Turns);

/// <summary>
/// Human review aggregation is descriptive. It cannot be used to bypass
/// approved V7a thresholds, reviewer independence or V7c fault testing.
/// </summary>
public sealed record V7cHumanReviewAggregate(
    string ContractVersion,
    string TranscriptSha256,
    int ReviewedTurns,
    int FlaggedInventedHistories,
    int FlaggedDialogueMisattributions,
    int FlaggedUnsupportedCertainty,
    int FlaggedUnnaturalRepetitions,
    int FlaggedTimidOrFormulaicSpeech,
    int PositiveCharacterDevelopments,
    int NaturalConversationScore,
    int CharacterDistinctnessScore,
    int HumorAndSpontaneityScore,
    int ConstructiveDisagreementScore,
    int HistoryCoherenceScore,
    int ModeratorNonIntrusionScore,
    bool OwnerAttestedReviewCompleted,
    bool IndependentHumanReviewAuthenticated,
    bool SemanticAcceptanceGranted,
    bool EnforcementAuthorized,
    string Caveat);

public static class V7cHumanReview
{
    public const string Version = "busstop-v7c-human-review-v1";
    public const string ReviewStatement =
        "I manually read this entire private BusStop transcript and " +
        "reviewed every turn. These labels and ratings are my own judgments.";

    public static V7cHumanReviewWorksheet CreateBlankWorksheet(
        V7cPrivateAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return new V7cHumanReviewWorksheet(
            Version, audit.TranscriptSha256, null, null,
            ReviewStatement,
            null, null, null, null, null, null,
            Array.AsReadOnly(audit.Turns.Select(turn =>
                new V7cHumanTurnReview(
                    turn.Sequence, turn.SpeakerId, turn.Text,
                    null, null, null, null, null, null, null)).ToArray()));
    }

    public static V7cHumanReviewAggregate ValidateCompleted(
        V7cPrivateAudit audit, V7cHumanReviewWorksheet reviewed)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(reviewed);
        if (reviewed.Version != Version ||
            reviewed.TranscriptSha256 != audit.TranscriptSha256 ||
            reviewed.ReviewAttestation != ReviewStatement ||
            string.IsNullOrWhiteSpace(reviewed.ReviewerAlias) ||
            reviewed.ReviewerAlias.Length > 80 ||
            !Regex.IsMatch(reviewed.ReviewerAlias,
                "^[a-zA-Z0-9][a-zA-Z0-9-]*$",
                RegexOptions.CultureInvariant) ||
            reviewed.ReviewedAtUtc is not { Offset: var offset } ||
            offset != TimeSpan.Zero ||
            reviewed.ReviewedAtUtc.Value > DateTimeOffset.UtcNow.AddMinutes(5) ||
            reviewed.Turns is null ||
            reviewed.Turns.Count != audit.Turns.Count ||
            !ValidRating(reviewed.NaturalConversationScore) ||
            !ValidRating(reviewed.CharacterDistinctnessScore) ||
            !ValidRating(reviewed.HumorAndSpontaneityScore) ||
            !ValidRating(reviewed.ConstructiveDisagreementScore) ||
            !ValidRating(reviewed.HistoryCoherenceScore) ||
            !ValidRating(reviewed.ModeratorNonIntrusionScore))
            throw new InvalidDataException(
                "V7c human review is incomplete, misbound or lacks explicit attestation.");

        for (int i = 0; i < reviewed.Turns.Count; i++)
        {
            var a = audit.Turns[i];
            var b = reviewed.Turns[i];
            if (b is null || b.Sequence != a.Sequence ||
                b.SpeakerId != a.SpeakerId ||
                b.UtteranceText != a.Text ||
                b.InventedPersonalHistory is null ||
                b.DialogueMisattribution is null ||
                b.UnsupportedFactualCertainty is null ||
                b.UnnaturalRepetition is null ||
                b.TimidOrFormulaicSpeech is null ||
                b.HealthyCharacterDevelopment is null ||
                b.Notes is { Length: > 2000 })
                throw new InvalidDataException(
                    "V7c human annotation is missing or alters committed speech.");
        }

        return new V7cHumanReviewAggregate(
            Version, audit.TranscriptSha256, reviewed.Turns.Count,
            reviewed.Turns.Count(x => x.InventedPersonalHistory == true),
            reviewed.Turns.Count(x => x.DialogueMisattribution == true),
            reviewed.Turns.Count(x => x.UnsupportedFactualCertainty == true),
            reviewed.Turns.Count(x => x.UnnaturalRepetition == true),
            reviewed.Turns.Count(x => x.TimidOrFormulaicSpeech == true),
            reviewed.Turns.Count(x => x.HealthyCharacterDevelopment == true),
            reviewed.NaturalConversationScore!.Value,
            reviewed.CharacterDistinctnessScore!.Value,
            reviewed.HumorAndSpontaneityScore!.Value,
            reviewed.ConstructiveDisagreementScore!.Value,
            reviewed.HistoryCoherenceScore!.Value,
            reviewed.ModeratorNonIntrusionScore!.Value,
            OwnerAttestedReviewCompleted: true,
            IndependentHumanReviewAuthenticated: false,
            SemanticAcceptanceGranted: false,
            EnforcementAuthorized: false,
            "This is a self-attested personal reading, not an independently " +
            "authenticated human judgment. It does not establish causal latency " +
            "impact, final V7 criteria, or release authorization.");
    }

    private static bool ValidRating(int? number) => number is >= 1 and <= 5;
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BusStop.Context;
using BusStop.Domain;

namespace BusStop.Engine.Evaluation;

/// <summary>
/// PRIVATE turn-level A/B/C selection trace. Contains speaker/turn references
/// and MUST NOT be exported to an aggregate-safe location.
/// </summary>
public sealed record V7d4aPrivateVariantTrace(
    string Variant,
    IReadOnlyList<long> SelectedSequences,
    IReadOnlyList<V7d4aAnchor> Anchors,
    bool ReplyReferenceVisible,
    int SelectedUtteranceCharacters,
    int SelectedSpeakerCount,
    string SelectionSha256);

public sealed record V7d4aPrivateTurnTrace(
    long ObservedTurnSequence,
    string SpeakerId,
    long? ExistingReplyReferenceSequence,
    IReadOnlyList<V7d4aPrivateVariantTrace> Variants);

public sealed record V7d4aPrivateComparison(
    string ContractVersion,
    string TranscriptSha256,
    int ObservedCommittedTurns,
    IReadOnlyList<V7d4aPrivateTurnTrace> Turns,
    string TraceSha256,
    bool LiveBehaviorChanged,
    bool HumanQualityReviewed,
    bool ModelInferencePerformed);

public sealed record V7d4aSafeVariantSummary(
    string Variant,
    int ComparedContexts,
    long TotalSelectedUtterances,
    long TotalSelectedUtteranceCharacters,
    long TotalAnchors,
    int MissingKnownReplyReferences,
    long ContextsWithAllThreeSpeakers);

public sealed record V7d4aSafeComparison(
    string ContractVersion,
    string TranscriptSha256,
    int ObservedCommittedTurns,
    IReadOnlyList<V7d4aSafeVariantSummary> Variants,
    bool LiveBehaviorChanged,
    bool ModelInferencePerformed,
    bool ConversationalImprovementEstablished,
    bool SemanticAcceptanceGranted,
    bool BehavioralRolloutAuthorized,
    string Limitation);

public static class V7d4aOfflineComparison
{
    public const string ContractVersion = "busstop-v7d-4a-compare-v1";
    private static readonly V7d4aContextVariant[] Variants =
    {
        V7d4aContextVariant.LegacyRecent20,
        V7d4aContextVariant.Recent8,
        V7d4aContextVariant.Selective6Plus2
    };

    public static V7d4aPrivateComparison Compare(
        IReadOnlyList<Utterance> committedTranscript, string transcriptSha256)
    {
        ArgumentNullException.ThrowIfNull(committedTranscript);
        if (committedTranscript.Count is < 1 or > 2000 ||
            transcriptSha256 is not { Length: 64 } ||
            transcriptSha256.Any(x =>
                !((x >= '0' && x <= '9') || (x >= 'a' && x <= 'f'))))
            throw new InvalidDataException("Invalid V7d-4a transcript binding.");

        for (int i = 0; i < committedTranscript.Count; i++)
        {
            var turn = committedTranscript[i];
            if (turn is null || turn.Sequence != i + 1 ||
                turn.Session != committedTranscript[0].Session ||
                turn.Text is null || turn.Text.Length > 20000)
                throw new InvalidDataException("Invalid committed transcript order.");
        }

        var sequenceById = committedTranscript.ToDictionary(x => x.Id, x => x.Sequence);
        var turns = new List<V7d4aPrivateTurnTrace>();

        for (int i = 0; i < committedTranscript.Count; i++)
        {
            var observed = committedTranscript[i];
            var prior = committedTranscript.Take(i).ToArray();
            long? replySequence = null;
            if (observed.RepliesTo is { } target)
            {
                if (!sequenceById.TryGetValue(target, out var targetSequence) ||
                    targetSequence >= observed.Sequence)
                    throw new InvalidDataException(
                        "Committed reply reference is not earlier speech.");
                replySequence = targetSequence;
            }

            var variantTraces = new List<V7d4aPrivateVariantTrace>();
            foreach (var variant in Variants)
            {
                var selection = V7d4aOfflineContextSelector.Select(
                    prior, observed.RepliesTo, variant);
                var trace = new V7d4aPrivateVariantTrace(
                    variant.ToString(),
                    Array.AsReadOnly(selection.Utterances.Select(x => x.Sequence).ToArray()),
                    selection.Anchors,
                    selection.ReplyReferenceVisible,
                    selection.Utterances.Sum(x => x.Text.Length),
                    selection.Utterances.Select(x => x.Speaker).Distinct().Count(),
                    selection.SelectionSha256);
                variantTraces.Add(trace);
            }
            turns.Add(new V7d4aPrivateTurnTrace(
                observed.Sequence, observed.Speaker.ToString(),
                replySequence, Array.AsReadOnly(variantTraces.ToArray())));
        }

        var immutableTurns = Array.AsReadOnly(turns.ToArray());
        return new V7d4aPrivateComparison(
            ContractVersion, transcriptSha256, turns.Count,
            immutableTurns, ComputeTraceSha(transcriptSha256, immutableTurns),
            LiveBehaviorChanged: false, HumanQualityReviewed: false,
            ModelInferencePerformed: false);
    }

    public static V7d4aSafeComparison ExportSafe(
        V7d4aPrivateComparison privateComparison)
    {
        ArgumentNullException.ThrowIfNull(privateComparison);
        if (privateComparison.ContractVersion != ContractVersion ||
            privateComparison.ObservedCommittedTurns != privateComparison.Turns.Count ||
            privateComparison.LiveBehaviorChanged ||
            privateComparison.ModelInferencePerformed ||
            privateComparison.HumanQualityReviewed ||
            privateComparison.TraceSha256 != ComputeTraceSha(
                privateComparison.TranscriptSha256, privateComparison.Turns) ||
            privateComparison.Turns.Where((turn, index) =>
                turn.ObservedTurnSequence != index + 1 ||
                turn.Variants.Count != Variants.Length).Any())
            throw new InvalidDataException("Invalid private context-comparison trace.");

        var summaries = new List<V7d4aSafeVariantSummary>();
        foreach (var variant in Variants)
        {
            var selected = privateComparison.Turns.Select(
                t => t.Variants.Single(x => x.Variant == variant.ToString())).ToArray();
            summaries.Add(new V7d4aSafeVariantSummary(
                variant.ToString(), selected.Length,
                selected.Sum(x => (long)x.SelectedSequences.Count),
                selected.Sum(x => (long)x.SelectedUtteranceCharacters),
                selected.Sum(x => (long)x.Anchors.Count),
                privateComparison.Turns.Count(t =>
                    t.ExistingReplyReferenceSequence is not null &&
                    !t.Variants.Single(x => x.Variant == variant.ToString())
                        .ReplyReferenceVisible),
                selected.LongCount(x => x.SelectedSpeakerCount == 3)));
        }

        return new V7d4aSafeComparison(
            ContractVersion, privateComparison.TranscriptSha256,
            privateComparison.ObservedCommittedTurns,
            Array.AsReadOnly(summaries.ToArray()),
            LiveBehaviorChanged: false,
            ModelInferencePerformed: false,
            ConversationalImprovementEstablished: false,
            SemanticAcceptanceGranted: false,
            BehavioralRolloutAuthorized: false,
            "OFFLINE comparison of contexts before COMMITTED utterances, " +
            "not every offered/pass/failed opportunity. Selected text character " +
            "counts exclude profile, briefing and prompt instructions. " +
            "No model responses were generated, no conversation quality " +
            "was assessed, and the known V7c transcript is DEVELOPMENT only.");
    }

    /// <summary>
    /// A canonical digest binds the fingerprint, turn speaker/reply, selected
    /// sequences, selected-text hashes, anchor reasons and context counts.
    /// It is read-only evidence, not a signed or authenticated owner judgment.
    /// </summary>
    private static string ComputeTraceSha(
        string transcriptSha256,
        IReadOnlyList<V7d4aPrivateTurnTrace> turns)
    {
        var serialized = new StringBuilder(transcriptSha256).Append('\n');
        foreach (var turn in turns)
        {
            serialized.Append(turn.ObservedTurnSequence.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(turn.SpeakerId)
                .Append('|').Append(turn.ExistingReplyReferenceSequence?.ToString(
                    CultureInfo.InvariantCulture) ?? "-").Append('\n');
            foreach (var v in turn.Variants)
            {
                serialized.Append(v.Variant).Append('|')
                    .Append(v.SelectionSha256).Append('|')
                    .Append(v.ReplyReferenceVisible ? "1" : "0").Append('|')
                    .Append(v.SelectedUtteranceCharacters.ToString(
                        CultureInfo.InvariantCulture)).Append('|')
                    .Append(v.SelectedSpeakerCount.ToString(
                        CultureInfo.InvariantCulture)).Append('|');
                foreach (var seq in v.SelectedSequences)
                    serialized.Append(seq.ToString(CultureInfo.InvariantCulture)).Append(',');
                serialized.Append('|');
                foreach (var anchor in v.Anchors)
                    serialized.Append(anchor.Sequence.ToString(CultureInfo.InvariantCulture))
                        .Append(':').Append(anchor.Reason).Append(',');
                serialized.Append('\n');
            }
        }
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(serialized.ToString()))).ToLowerInvariant();
    }

}

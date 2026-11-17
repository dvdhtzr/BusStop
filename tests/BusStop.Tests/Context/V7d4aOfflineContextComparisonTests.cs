using BusStop.Context;
using BusStop.Domain;
using BusStop.Engine.Evaluation;

namespace BusStop.Tests.Context;

/// <summary>
/// All V7d-4a tests are synthetic and model-free. There is no private
/// transcript fixture, Ollama call, production context policy or file output.
/// </summary>
public sealed class V7d4aOfflineContextComparisonTests
{
    private readonly SessionId _session = SessionId.New();
    private readonly CharacterId _a = CharacterId.New();
    private readonly CharacterId _b = CharacterId.New();
    private readonly CharacterId _c = CharacterId.New();

    private Utterance Turn(int sequence, CharacterId speaker,
        string? text = null, UtteranceId? repliesTo = null) =>
        new(UtteranceId.New(), _session, OpportunityId.New(),
            speaker, sequence,
            text ?? $"Example{sequence} viewpoint{sequence} reason{sequence} " +
                $"detail{sequence} factory workers wages and training.",
            null, repliesTo, DateTimeOffset.UtcNow);

    private Utterance[] History(int count) =>
        Enumerable.Range(1, count).Select(i =>
            Turn(i, i > count - 6
                ? i % 2 == 0 ? _a : _b
                : i % 3 == 0 ? _c : i % 2 == 0 ? _a : _b))
            .ToArray();

    [Fact]
    public void A_is_byte_for_byte_identical_to_original_recent20_prompt()
    {
        var history = History(27);
        var self = new CharacterProfile(
            _a, 1, "Arthur", "PRIVATE_ARTHUR_IDENTITY",
            new[] { "direct" });
        var others = new[]
        {
            new ParticipantHandle(_a, "Arthur"),
            new ParticipantHandle(_b, "Maya"),
            new ParticipantHandle(_c, "Daniel")
        };
        var inputs = new ContextInputs(
            self, others,
            new Briefing(BriefingId.New(), "Factory reopening",
                "The Wire", "https://example.invalid/story",
                "FACTORY_BRIEFING"),
            history,
            new SpeakingOpportunity(OpportunityId.New(), _session,
                _a, OpportunityReason.FairOrder, 27,
                OpportunityStatus.Offered));
        var builder = new ParticipantContextBuilder();
        var original = builder.Build(inputs);

        var selected = V7d4aOfflineContextSelector.Select(history,
            null, V7d4aContextVariant.LegacyRecent20);
        var preview = V7d4aOfflineContextSelector.RenderPreview(
            inputs, selected, builder);

        Assert.Equal(original.Prompt, preview.Prompt);
        Assert.True(original.VisibleUtteranceIds.SetEquals(
            preview.VisibleUtteranceIds));
        Assert.Equal(20, selected.Utterances.Count);
        Assert.Empty(selected.Anchors);
        Assert.Equal(8, selected.Utterances[0].Sequence);
        Assert.Equal(27, selected.Utterances[^1].Sequence);
    }

    [Fact]
    public void B_is_exactly_latest_eight_original_committed_turns()
    {
        var history = History(25);
        var s = V7d4aOfflineContextSelector.Select(history,
            null, V7d4aContextVariant.Recent8);
        Assert.Equal(Enumerable.Range(18, 8).Select(x => (long)x),
            s.Utterances.Select(x => x.Sequence));
        Assert.Empty(s.Anchors);
        Assert.True(s.ReplyReferenceVisible);
    }

    [Fact]
    public void C_preserves_six_latest_and_earlier_diverse_anchors()
    {
        var history = History(25);
        var s = V7d4aOfflineContextSelector.Select(history,
            null, V7d4aContextVariant.Selective6Plus2);
        Assert.InRange(s.Utterances.Count, 6, 8);
        Assert.All(history.TakeLast(6),
            x => Assert.Contains(s.Utterances, y => y.Id == x.Id));
        Assert.Equal(s.Utterances.Count - 6, s.Anchors.Count);
        Assert.Contains(s.Anchors, x =>
            x.Reason == "UnderrepresentedSpeaker");
        Assert.Equal(s.Utterances.Count,
            s.Utterances.Select(x => x.Id).Distinct().Count());
        Assert.True(s.Utterances.Zip(s.Utterances.Skip(1),
            (a, b) => a.Sequence < b.Sequence).All(x => x));
    }

    [Fact]
    public void C_reserves_an_anchor_for_a_distant_committed_reply()
    {
        var history = History(35);
        var reply = history[1];
        var a = V7d4aOfflineContextSelector.Select(history,
            reply.Id, V7d4aContextVariant.LegacyRecent20);
        var b = V7d4aOfflineContextSelector.Select(history,
            reply.Id, V7d4aContextVariant.Recent8);
        var c = V7d4aOfflineContextSelector.Select(history,
            reply.Id, V7d4aContextVariant.Selective6Plus2);

        Assert.False(a.ReplyReferenceVisible);
        Assert.False(b.ReplyReferenceVisible);
        Assert.True(c.ReplyReferenceVisible);
        Assert.Contains(c.Anchors, x =>
            x.Sequence == reply.Sequence &&
            x.Reason == "RequiredCommittedReplyReference");
        Assert.InRange(c.Utterances.Count, 7, 8);
        Assert.All(history.TakeLast(6),
            x => Assert.Contains(c.Utterances, y => y.Id == x.Id));
    }

    [Fact]
    public void All_variants_refuse_uncommitted_or_foreign_reply_references()
    {
        var history = History(15);
        foreach (var variant in Enum.GetValues<V7d4aContextVariant>())
            Assert.Throws<InvalidDataException>(() =>
                V7d4aOfflineContextSelector.Select(
                    history, UtteranceId.New(), variant));

        var corrupted = history.ToArray();
        corrupted[8] = new Utterance(
            UtteranceId.New(), SessionId.New(), OpportunityId.New(),
            _a, 9, "FOREIGN_SESSION_TEXT", null, null,
            DateTimeOffset.UtcNow);
        Assert.Throws<InvalidDataException>(() =>
            V7d4aOfflineContextSelector.Select(
                corrupted, null, V7d4aContextVariant.Selective6Plus2));

        var future = history.Skip(1).ToArray();
        Assert.Throws<InvalidDataException>(() =>
            V7d4aOfflineContextSelector.Select(
                future, null, V7d4aContextVariant.LegacyRecent20));
    }

    [Fact]
    public void Selectors_and_trace_hashes_are_deterministic_and_bound_to_text()
    {
        var history = History(28);
        var first = V7d4aOfflineContextSelector.Select(history,
            null, V7d4aContextVariant.Selective6Plus2);
        var second = V7d4aOfflineContextSelector.Select(history,
            null, V7d4aContextVariant.Selective6Plus2);
        Assert.Equal(first.SelectionSha256, second.SelectionSha256);
        Assert.Equal(first.Utterances.Select(x => x.Id),
            second.Utterances.Select(x => x.Id));

        var edited = history.ToArray();
        edited[^1] = edited[^1] with { Text = "MUTATED_COMMITTED_SPEECH" };
        var different = V7d4aOfflineContextSelector.Select(edited,
            null, V7d4aContextVariant.Selective6Plus2);
        Assert.NotEqual(first.SelectionSha256, different.SelectionSha256);
    }

    [Fact]
    public void Offline_comparison_does_not_claim_quality_or_intervention()
    {
        var original = History(35);
        var analysis = V7d4aOfflineComparison.Compare(original,
            new string('a', 64));
        var again = V7d4aOfflineComparison.Compare(original,
            new string('a', 64));
        var safe = V7d4aOfflineComparison.ExportSafe(analysis);

        Assert.Equal(35, analysis.ObservedCommittedTurns);
        Assert.Equal(analysis.TraceSha256, again.TraceSha256);
        Assert.Equal(3, safe.Variants.Count);
        Assert.All(safe.Variants,
            x => Assert.Equal(35, x.ComparedContexts));
        Assert.False(safe.LiveBehaviorChanged);
        Assert.False(safe.ModelInferencePerformed);
        Assert.False(safe.ConversationalImprovementEstablished);
        Assert.False(safe.SemanticAcceptanceGranted);
        Assert.False(safe.BehavioralRolloutAuthorized);

        var json = System.Text.Json.JsonSerializer.Serialize(safe);
        Assert.DoesNotContain("factory workers wages and training", json);
        Assert.DoesNotContain("SelectedSequences", json);
        Assert.DoesNotContain("SpeakerId", json);
    }

    [Fact]
    public void Comparison_reports_missing_known_reply_references()
    {
        var history = History(30);
        var last = history[^1] with { RepliesTo = history[0].Id };
        history[^1] = last;
        var analysis = V7d4aOfflineComparison.Compare(history,
            new string('b', 64));
        var safe = V7d4aOfflineComparison.ExportSafe(analysis);
        Assert.Equal(1, safe.Variants.Single(
            x => x.Variant == "LegacyRecent20").MissingKnownReplyReferences);
        Assert.Equal(1, safe.Variants.Single(
            x => x.Variant == "Recent8").MissingKnownReplyReferences);
        Assert.Equal(0, safe.Variants.Single(
            x => x.Variant == "Selective6Plus2").MissingKnownReplyReferences);
    }

    [Fact]
    public void Safe_export_rejects_edited_private_selection_trace()
    {
        var analysis = V7d4aOfflineComparison.Compare(
            History(15), new string('d', 64));
        var changed = analysis.Turns.ToArray();
        var variants = changed[8].Variants.ToArray();
        variants[1] = variants[1] with
        {
            SelectedSequences = Array.AsReadOnly(new long[] { 999 })
        };
        changed[8] = changed[8] with
        {
            Variants = Array.AsReadOnly(variants)
        };
        Assert.Throws<InvalidDataException>(() =>
            V7d4aOfflineComparison.ExportSafe(analysis with
            {
                Turns = Array.AsReadOnly(changed)
            }));
    }

    [Fact]
    public void Comparison_rejects_future_reply_reference()
    {
        var history = History(4);
        history[0] = history[0] with { RepliesTo = history[3].Id };
        Assert.Throws<InvalidDataException>(() =>
            V7d4aOfflineComparison.Compare(history,
                new string('c', 64)));
    }
}

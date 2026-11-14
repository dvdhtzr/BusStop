using BusStop.Context;
using BusStop.Domain;

namespace BusStop.Tests.Context;

public class ParticipantContextBuilderTests
{
    private static readonly CharacterId ArthurId = CharacterId.New();
    private static readonly CharacterId MayaId = CharacterId.New();
    private static readonly CharacterId DanielId = CharacterId.New();

    private static readonly CharacterProfile Arthur = new(
        ArthurId, 1, "Arthur", "A retired machinist who distrusts ARTHUR_SECRET_CREED easy answers.", new[] { "ornery" });

    private static readonly CharacterProfile Maya = new(
        MayaId, 1, "Maya", "A nurse whose MAYA_SECRET_CREED is that people deserve patience.", new[] { "empathetic" });

    private static readonly IReadOnlyList<ParticipantHandle> Room = new[]
    {
        new ParticipantHandle(ArthurId, "Arthur"),
        new ParticipantHandle(MayaId, "Maya"),
        new ParticipantHandle(DanielId, "Daniel")
    };

    private static readonly Briefing Story = new(BriefingId.New(), "Factory to reopen", "The Wire", "https://x", "A closed plant will reopen.");

    private static Utterance Committed(CharacterId speaker, long seq, string text, CharacterId? addressee = null) =>
        new(UtteranceId.New(), SessionId.New(), OpportunityId.New(), speaker, seq, text, addressee, null, DateTimeOffset.UtcNow);

    private static SpeakingOpportunity Opp(CharacterId who, OpportunityReason reason) =>
        new(OpportunityId.New(), SessionId.New(), who, reason, 0, OpportunityStatus.Offered);

    [Fact]
    public void Context_includes_self_identity_briefing_and_competence()
    {
        var built = new ParticipantContextBuilder().Build(new ContextInputs(
            Arthur, Room, Story, Array.Empty<Utterance>(), Opp(ArthurId, OpportunityReason.Opening)));

        Assert.Contains("ARTHUR_SECRET_CREED", built.Prompt);
        Assert.Contains("Factory to reopen", built.Prompt);
        Assert.Contains("one natural conversational turn", built.Prompt);
        Assert.Contains("No one has spoken yet.", built.Prompt);
    }

    [Fact]
    public void Context_excludes_other_participants_private_identity()
    {
        var built = new ParticipantContextBuilder().Build(new ContextInputs(
            Arthur, Room, Story, Array.Empty<Utterance>(), Opp(ArthurId, OpportunityReason.Opening)));

        // Maya's private persona must never appear in Arthur's context...
        Assert.DoesNotContain("MAYA_SECRET_CREED", built.Prompt);
        // ...but her public name, as someone in the room he can address, does.
        Assert.Contains("Maya", built.Prompt);
    }

    [Fact]
    public void Context_shows_committed_dialogue_by_speaker_name()
    {
        var history = new[]
        {
            Committed(MayaId, 1, "The plant closing broke this town."),
            Committed(DanielId, 2, "Did it, though?", MayaId)
        };

        var built = new ParticipantContextBuilder().Build(new ContextInputs(
            Arthur, Room, Story, history, Opp(ArthurId, OpportunityReason.FairOrder)));

        Assert.Contains("Maya: The plant closing broke this town.", built.Prompt);
        Assert.Contains("Daniel (to Maya): Did it, though?", built.Prompt);
        Assert.Equal(2, built.VisibleUtteranceIds.Count);
    }

    [Fact]
    public void Context_excludes_utterances_not_in_the_committed_history_given()
    {
        var included = Committed(MayaId, 1, "INCLUDED_LINE");
        var notGiven = Committed(DanielId, 2, "UNCOMMITTED_LINE");

        var built = new ParticipantContextBuilder().Build(new ContextInputs(
            Arthur, Room, Story, new[] { included }, Opp(ArthurId, OpportunityReason.FairOrder)));

        Assert.Contains("INCLUDED_LINE", built.Prompt);
        Assert.DoesNotContain("UNCOMMITTED_LINE", built.Prompt);
        Assert.DoesNotContain(notGiven.Id, built.VisibleUtteranceIds);
    }

    [Fact]
    public void Recent_window_bounds_the_rendered_history()
    {
        var history = Enumerable.Range(1, 30)
            .Select(i => Committed(MayaId, i, $"LINE_{i}"))
            .ToArray();

        var built = new ParticipantContextBuilder(recentWindow: 5).Build(new ContextInputs(
            Arthur, Room, Story, history, Opp(ArthurId, OpportunityReason.FairOrder)));

        Assert.DoesNotContain("LINE_25", built.Prompt);   // older than the window
        Assert.Contains("LINE_26", built.Prompt);          // within the last 5
        Assert.Contains("LINE_30", built.Prompt);
        Assert.Equal(5, built.VisibleUtteranceIds.Count);
    }

    [Fact]
    public void Addressable_participants_exclude_self()
    {
        var built = new ParticipantContextBuilder().Build(new ContextInputs(
            Arthur, Room, Story, Array.Empty<Utterance>(), Opp(ArthurId, OpportunityReason.Opening)));

        Assert.DoesNotContain(built.AddressableParticipants, p => p.Id == ArthurId);
        Assert.Equal(2, built.AddressableParticipants.Count);
    }
}

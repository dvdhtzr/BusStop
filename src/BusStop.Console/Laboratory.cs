using BusStop.Context;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Models;
using BusStop.Persistence;

namespace BusStop.Console;

/// <summary>The fixed Phase 1 laboratory: three approved characters, one frozen briefing, and the
/// deterministic demo script used for an offline, Ollama-free smoke run. Ids are fixed so runs are
/// reproducible.</summary>
public static class Laboratory
{
    public static readonly CharacterProfile Arthur = new(
        new CharacterId(Guid.Parse("11111111-1111-1111-1111-111111111111")), 1, "Arthur",
        "A retired machinist in his seventies who spent forty years on a factory floor before it closed. " +
        "He is wary of promises and speaks plainly, but he listens.",
        new[] { "older adult", "skeptical", "direct", "guarded" });

    public static readonly CharacterProfile Maya = new(
        new CharacterId(Guid.Parse("22222222-2222-2222-2222-222222222222")), 2, "Maya",
        "A hospice nurse in her thirties who grew up in a mill town and watched her own father retrain " +
        "after the mill closed. She is warm, but she holds a real view: people can remake themselves, and " +
        "a reopening like this is worth cautious hope. She says so, pushes back when she disagrees, and " +
        "draws on what she has actually seen — she does not just ask questions.",
        new[] { "young adult", "empathetic", "hopeful", "willing to disagree" });

    public static readonly CharacterProfile Daniel = new(
        new CharacterId(Guid.Parse("33333333-3333-3333-3333-333333333333")), 2, "Daniel",
        "A high-school shop teacher in his fifties who thinks about his students by name and whether this " +
        "plant will give them a future. He argues from concrete things he has seen in his classroom, is " +
        "skeptical of easy talk about 'opportunity,' and tests a claim against the real kids in front of " +
        "him rather than against abstractions.",
        new[] { "middle-aged", "reflective", "concrete", "skeptical" });

    public static IReadOnlyList<CharacterProfile> Cast => new[] { Arthur, Maya, Daniel };

    public const string ProtocolVersion = "floor-2";
    public const string ContextVersion = "ctx-3-ending";

    public static readonly Briefing Story = new(
        new BriefingId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
        "Shuttered auto-parts plant to reopen under new owner",
        "The Riverside Wire",
        "https://example.com/riverside/plant-reopens",
        "A car-parts factory that closed six years ago, cutting about 900 jobs, will reopen next year " +
        "under a new owner that makes components for electric vehicles. The company says it will hire " +
        "roughly 400 workers initially and train them for automated lines. Local officials welcomed the " +
        "news; a former union representative said the wages and the number of jobs are both lower than before.");

    /// <summary>A second, deliberately unrelated briefing, used for the cross-topic memory test: if a
    /// continuing conversation about the bridge brings up the factory, the pay, or the retraining, that
    /// can only have come from remembering the prior conversation, not from the topic.</summary>
    public static readonly Briefing SecondStory = new(
        new BriefingId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
        "City to close aging Main Street bridge for a two-year replacement",
        "The Riverside Wire",
        "https://example.com/riverside/bridge-replacement",
        "The city council voted to replace the 80-year-old Main Street bridge, the main route across the " +
        "river, starting in spring. The bridge will be closed for about two years while a wider span is " +
        "built, and drivers will be detoured several miles around. Officials say the old bridge is no " +
        "longer safe; some residents worry the detour will hurt downtown shops and lengthen the commute " +
        "for people on the east side.");

    /// <summary>Builds the laboratory session plan without changing the engine. The C1b
    /// bridge-only control uses the same frozen bridge story as continuing sessions,
    /// but starts fresh and therefore receives no prior-session recollections.</summary>
    public static (SessionMode Mode, string Label, Briefing Briefing)[] BuildRunPlan(
        int runs, bool continuing = false, bool freshBridge = false)
    {
        if (runs < 1) throw new ArgumentOutOfRangeException(nameof(runs));
        if (continuing && freshBridge)
            throw new ArgumentException("--fresh-bridge cannot be combined with --continuing.");

        if (continuing)
            return new[]
            {
                (Mode: SessionMode.FreshEncounter, Label: "Prior conversation", Briefing: Story),
                (Mode: SessionMode.Continuing, Label: "Continuing conversation", Briefing: SecondStory)
            };

        var briefing = freshBridge ? SecondStory : Story;
        return Enumerable.Range(1, runs)
            .Select(i => (Mode: SessionMode.FreshEncounter, Label: $"Run {i}", Briefing: briefing))
            .ToArray();
    }

    public static SessionId Seed(
        ConversationStore store,
        SessionMode mode = SessionMode.FreshEncounter,
        string modelConfigVersion = "stub/stub-digest",
        Briefing? briefing = null)
    {
        var story = briefing ?? Story;
        foreach (var c in Cast) store.SaveCharacter(c);
        store.SaveBriefing(story);
        var id = SessionId.New();
        store.CreateSession(new Session(
            id,
            Cast.Select(c => c.Id).ToList(),
            mode,
            story.Id,
            ProtocolVersion: ProtocolVersion,
            ModelConfigVersion: modelConfigVersion,
            ContextVersion: ContextVersion,
            Status: SessionStatus.Active,
            CreatedAt: DateTimeOffset.UtcNow,
            EndedAt: null));
        return id;
    }

    public static ConversationEngine BuildEngine(ConversationStore store, SessionId session, IModelClient model) =>
        new(store, model, new ParticipantContextBuilder(), session, Cast, Story);

    /// <summary>A deterministic three-way exchange for the offline smoke run. It is aligned to the
    /// coordinator's offer order and exercises the opening, fair order, directed replies in both
    /// directions, a decline, and a natural ending.</summary>
    public static IModelClient DemoModel() => new StubModelClient(new[]
    {
        StubResponses.Speak("Forty years I ran a lathe in a place just like that. They told us it would never close, right up until it did."),
        StubResponses.Speak("That sounds like it still stings. I keep thinking about the people who had to start over."),
        StubResponses.Speak("Four hundred jobs where there were nine hundred. I wonder what we tell the kids that means."),
        StubResponses.Speak("Maya, you can call it hope. I call it a headline. We have been promised reopenings before.", addressee: "Maya"),
        StubResponses.Speak("I hear the caution, Arthur. But some of my patients have waited years for any work to come back. That is not nothing.", addressee: "Arthur"),
        StubResponses.Pass(),
        StubResponses.Speak("Lower wages, automated lines. Maybe the real question is what kind of work we are reopening, not just how much."),
        StubResponses.Pass(),
        StubResponses.Pass(),
        StubResponses.Pass(),
        StubResponses.Pass(),
        StubResponses.Pass(),
        StubResponses.Pass()
    });
}

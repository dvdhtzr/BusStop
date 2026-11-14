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
        new CharacterId(Guid.Parse("22222222-2222-2222-2222-222222222222")), 1, "Maya",
        "A nurse in her thirties who sees how work and its loss shape the people she cares for. " +
        "She is warm and asks questions, and she does not mistake hope for naivety.",
        new[] { "young adult", "empathetic", "curious", "patient" });

    public static readonly CharacterProfile Daniel = new(
        new CharacterId(Guid.Parse("33333333-3333-3333-3333-333333333333")), 1, "Daniel",
        "A middle-aged schoolteacher who thinks in terms of what the next generation will inherit. " +
        "He is reflective and a little contrarian, and he likes to test an idea by turning it over.",
        new[] { "middle-aged", "analytical", "reflective", "argumentative" });

    public static IReadOnlyList<CharacterProfile> Cast => new[] { Arthur, Maya, Daniel };

    public static readonly Briefing Story = new(
        new BriefingId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
        "Shuttered auto-parts plant to reopen under new owner",
        "The Riverside Wire",
        "https://example.com/riverside/plant-reopens",
        "A car-parts factory that closed six years ago, cutting about 900 jobs, will reopen next year " +
        "under a new owner that makes components for electric vehicles. The company says it will hire " +
        "roughly 400 workers initially and train them for automated lines. Local officials welcomed the " +
        "news; a former union representative said the wages and the number of jobs are both lower than before.");

    public static SessionId Seed(ConversationStore store, SessionMode mode = SessionMode.FreshEncounter)
    {
        foreach (var c in Cast) store.SaveCharacter(c);
        store.SaveBriefing(Story);
        var id = SessionId.New();
        store.CreateSession(new Session(
            id,
            Cast.Select(c => c.Id).ToList(),
            mode,
            Story.Id,
            ProtocolVersion: "floor-1",
            ModelConfigVersion: "stub/stub-digest",
            ContextVersion: "ctx-1",
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

using BusStop.Context;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Memory;
using BusStop.Models;
using BusStop.Persistence;
using Microsoft.Data.Sqlite;

namespace BusStop.Tests.Memory;

public sealed class RecollectionEngineTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"busstop-recall-engine-{Guid.NewGuid():N}.db");

    private static readonly CharacterProfile Arthur = new(CharacterId.New(), 1, "Arthur", "A machinist.", new[] { "wary" });
    private static readonly CharacterProfile Maya = new(CharacterId.New(), 2, "Maya", "A nurse.", new[] { "hopeful" });
    private static readonly CharacterProfile Daniel = new(CharacterId.New(), 2, "Daniel", "A teacher.", new[] { "concrete" });
    private static readonly IReadOnlyList<CharacterProfile> Cast = new[] { Arthur, Maya, Daniel };
    private static readonly Briefing Story = new(BriefingId.New(), "Plant reopens", "Wire", "https://x", "A plant reopens.");

    private sealed class CapturingClient : IModelClient
    {
        public List<string> Prompts { get; } = new();
        public string ModelName => "capture";
        public string ModelDigest => "capture";
        public ModelResponse Decide(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Prompts.Add(request.Prompt);
            return StubResponses.Pass();
        }
    }

    private ConversationStore Open()
    {
        var store = ConversationStore.OpenFile(_path);
        store.InitializeSchema();
        return store;
    }

    private SessionId SeedSession(ConversationStore store, SessionMode mode, SessionStatus status)
    {
        foreach (var c in Cast) store.SaveCharacter(c);
        store.SaveBriefing(Story);
        var id = SessionId.New();
        store.CreateSession(new Session(
            id, Cast.Select(c => c.Id).ToList(), mode, Story.Id,
            "floor-2", "capture", "ctx", status, DateTimeOffset.UtcNow, status == SessionStatus.Ended ? DateTimeOffset.UtcNow : null));
        return id;
    }

    private void SeedPriorConversation(ConversationStore store)
    {
        var prior = SeedSession(store, SessionMode.FreshEncounter, SessionStatus.Ended);
        store.CommitUtterance(OpportunityId.New(), prior, Maya.Id, "MEMORABLE_PRIOR_LINE about my father.", null, null, DateTimeOffset.UtcNow);
        store.CommitUtterance(OpportunityId.New(), prior, Arthur.Id, "I remember the shutdown.", null, null, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Continuing_session_surfaces_a_prior_line_in_the_prompt()
    {
        using var store = Open();
        SeedPriorConversation(store);
        var session = SeedSession(store, SessionMode.Continuing, SessionStatus.Active);

        var client = new CapturingClient();
        new ConversationEngine(
            store, client, new ParticipantContextBuilder(), session, Cast, Story,
            recollections: new RecollectionService(store)).Run();

        Assert.Contains(client.Prompts, p => p.Contains("MEMORABLE_PRIOR_LINE"));
        Assert.Contains(client.Prompts, p => p.Contains("When you last talked with them"));
    }

    [Fact]
    public void Fresh_session_does_not_surface_prior_lines()
    {
        using var store = Open();
        SeedPriorConversation(store);
        var session = SeedSession(store, SessionMode.FreshEncounter, SessionStatus.Active);

        var client = new CapturingClient();
        new ConversationEngine(
            store, client, new ParticipantContextBuilder(), session, Cast, Story,
            recollections: new RecollectionService(store)).Run();

        Assert.DoesNotContain(client.Prompts, p => p.Contains("MEMORABLE_PRIOR_LINE"));
        Assert.DoesNotContain(client.Prompts, p => p.Contains("When you last talked with them"));
    }

    [Fact]
    public void Continuing_session_loads_private_impressions_without_leaking_them_to_the_subject()
    {
        using var store = Open();
        var prior = SeedSession(store, SessionMode.FreshEncounter, SessionStatus.Ended);
        // Arthur's private impression of Maya.
        store.SaveImpression(new Impression(Arthur.Id, Maya.Id, prior, "Maya is relentlessly hopeful.", DateTimeOffset.UtcNow));
        var session = SeedSession(store, SessionMode.Continuing, SessionStatus.Active);

        var client = new CapturingClient();
        new ConversationEngine(
            store, client, new ParticipantContextBuilder(), session, Cast, Story,
            impressions: new ImpressionService(store)).Run();

        // Arthur's own prompts carry his private read of Maya...
        Assert.Contains(client.Prompts, p => p.Contains("Arthur. A machinist.")
            && p.Contains("What you make of them") && p.Contains("Maya is relentlessly hopeful."));

        // ...but it never appears in Maya's prompts (it is private to Arthur).
        Assert.DoesNotContain(client.Prompts, p => p.Contains("Maya. A nurse.") && p.Contains("Maya is relentlessly hopeful."));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}

using System.Text.Json;
using BusStop.Domain;
using BusStop.Memory;
using BusStop.Models;
using BusStop.Persistence;
using Microsoft.Data.Sqlite;

namespace BusStop.Tests.Memory;

public sealed class ImpressionServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"busstop-impr-{Guid.NewGuid():N}.db");

    private static readonly CharacterProfile Arthur = new(CharacterId.New(), 1, "Arthur", "A machinist.", new[] { "wary" });
    private static readonly CharacterProfile Maya = new(CharacterId.New(), 2, "Maya", "A nurse.", new[] { "hopeful" });
    private static readonly CharacterProfile Daniel = new(CharacterId.New(), 2, "Daniel", "A teacher.", new[] { "concrete" });
    private static readonly IReadOnlyList<CharacterProfile> Cast = new[] { Arthur, Maya, Daniel };

    private ConversationStore Open()
    {
        var store = ConversationStore.OpenFile(_path);
        store.InitializeSchema();
        return store;
    }

    private SessionId SeedEndedSession(ConversationStore store)
    {
        var briefing = new Briefing(BriefingId.New(), "A story", "Src", "url", "Body.");
        store.SaveBriefing(briefing);
        foreach (var c in Cast) store.SaveCharacter(c);
        var id = SessionId.New();
        store.CreateSession(new Session(
            id, Cast.Select(c => c.Id).ToList(), SessionMode.FreshEncounter, briefing.Id,
            "p", "m", "c", SessionStatus.Ended, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        store.CommitUtterance(OpportunityId.New(), id, Arthur.Id, "I don't trust it.", null, null, DateTimeOffset.UtcNow);
        store.CommitUtterance(OpportunityId.New(), id, Maya.Id, "People can rebuild.", null, null, DateTimeOffset.UtcNow);
        return id;
    }

    private static ModelResponse Impressions(params (string Of, string Impression)[] items)
    {
        var payload = new { impressions = items.Select(i => new { of = i.Of, impression = i.Impression }).ToArray() };
        return ModelResponse.Responded(JsonSerializer.Serialize(payload));
    }

    [Fact]
    public void Reflection_stores_each_participants_impressions_of_the_others()
    {
        using var store = Open();
        var session = SeedEndedSession(store);

        // One scripted reflection per participant, in cast order (Arthur, Maya, Daniel).
        var model = new StubModelClient(new[]
        {
            Impressions(("Maya", "Hopeful, maybe naive."), ("Daniel", "Thinks about his students."), ("Arthur", "ignored — self")),
            Impressions(("Arthur", "Guarded but fair."), ("Daniel", "Practical.")),
            Impressions(("Arthur", "Set in his ways."), ("Maya", "An optimist."))
        });

        new ImpressionService(store).Reflect(session, model, Cast);

        var arthursView = store.GetImpressions(Arthur.Id);
        Assert.Equal(2, arthursView.Count);                              // of Maya and Daniel, not self
        Assert.DoesNotContain(arthursView, i => i.Subject == Arthur.Id); // never about oneself
        Assert.Contains(arthursView, i => i.Subject == Maya.Id && i.Content == "Hopeful, maybe naive.");
        Assert.All(arthursView, i => Assert.Equal(Arthur.Id, i.Owner));  // private to the owner
        Assert.All(arthursView, i => Assert.Equal(session, i.SourceSession));

        Assert.Equal(2, store.GetImpressions(Maya.Id).Count);
        Assert.Equal(2, store.GetImpressions(Daniel.Id).Count);
    }

    [Fact]
    public void A_malformed_reflection_stores_nothing_for_that_participant()
    {
        using var store = Open();
        var session = SeedEndedSession(store);

        // Arthur's reflection is a speak, not an impressions object; Maya's and Daniel's are empty objects.
        var model = new StubModelClient(new[]
        {
            StubResponses.Speak("this is not a reflection"),
            ModelResponse.Responded("{}"),
            ModelResponse.Responded("{}")
        });

        new ImpressionService(store).Reflect(session, model, Cast);

        Assert.Empty(store.GetImpressions(Arthur.Id));
        Assert.Empty(store.GetImpressions(Maya.Id));
        Assert.Empty(store.GetImpressions(Daniel.Id));
    }

    [Fact]
    public void ImpressionsFor_returns_the_most_recent_per_present_subject()
    {
        using var store = Open();
        var s1 = SessionId.New();
        var s2 = SessionId.New();
        store.SaveImpression(new Impression(Arthur.Id, Maya.Id, s1, "older view of Maya", DateTimeOffset.UtcNow.AddHours(-2)));
        store.SaveImpression(new Impression(Arthur.Id, Maya.Id, s2, "newer view of Maya", DateTimeOffset.UtcNow.AddHours(-1)));
        store.SaveImpression(new Impression(Arthur.Id, Daniel.Id, s1, "view of Daniel", DateTimeOffset.UtcNow.AddHours(-2)));

        var service = new ImpressionService(store);

        var both = service.ImpressionsFor(Arthur.Id, new[] { Maya.Id, Daniel.Id });
        Assert.Equal(2, both.Count);
        Assert.Contains(both, i => i.Subject == Maya.Id && i.Content == "newer view of Maya"); // most recent wins

        var onlyMaya = service.ImpressionsFor(Arthur.Id, new[] { Maya.Id });
        Assert.Single(onlyMaya);
        Assert.Equal(Maya.Id, onlyMaya[0].Subject);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}

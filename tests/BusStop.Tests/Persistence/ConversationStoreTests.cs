using BusStop.Domain;
using BusStop.Persistence;
using Microsoft.Data.Sqlite;

namespace BusStop.Tests.Persistence;

public sealed class ConversationStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"busstop-{Guid.NewGuid():N}.db");

    private ConversationStore Open()
    {
        var store = ConversationStore.OpenFile(_path);
        store.InitializeSchema();
        return store;
    }

    private static Session MinimalSession(SessionId id, IReadOnlyList<CharacterId> ps, BriefingId briefing) =>
        new(id, ps, SessionMode.FreshEncounter, briefing, "proto-1", "model-1", "ctx-1",
            SessionStatus.Active, DateTimeOffset.UtcNow, null);

    [Fact]
    public void Round_trips_session_character_briefing_and_utterance()
    {
        var charId = CharacterId.New();
        var sessionId = SessionId.New();
        var briefingId = BriefingId.New();
        var oppId = OpportunityId.New();
        var now = DateTimeOffset.UtcNow;

        using (var store = Open())
        {
            store.SaveCharacter(new CharacterProfile(charId, 1, "Arthur", "A retired machinist.", new[] { "ornery", "curious" }));
            store.SaveBriefing(new Briefing(briefingId, "Factory reopens", "The Wire", "https://example.com/a", "Body."));
            store.CreateSession(MinimalSession(sessionId, new[] { charId }, briefingId));
            store.SaveOpportunity(new SpeakingOpportunity(oppId, sessionId, charId, OpportunityReason.Opening, 0, OpportunityStatus.Decided));
            store.CommitUtterance(oppId, sessionId, charId, "I remember that plant.", null, null, now);
        }

        using (var reopened = Open())
        {
            var character = reopened.GetCharacter(charId, 1);
            Assert.NotNull(character);
            Assert.Equal("Arthur", character!.Name);
            Assert.Equal(new[] { "ornery", "curious" }, character.Traits);

            var session = reopened.GetSession(sessionId);
            Assert.NotNull(session);
            Assert.Equal(new[] { charId }, session!.Participants);
            Assert.Equal(SessionMode.FreshEncounter, session.Mode);

            var utterances = reopened.GetCommittedUtterances(sessionId);
            Assert.Single(utterances);
            Assert.Equal("I remember that plant.", utterances[0].Text);
            Assert.Equal(oppId, utterances[0].Opportunity);
            Assert.Equal(1, utterances[0].Sequence);
        }
    }

    [Fact]
    public void Sequence_is_strictly_monotonic_within_a_session()
    {
        var charId = CharacterId.New();
        var sessionId = SessionId.New();
        var briefingId = BriefingId.New();

        using var store = Open();
        store.SaveBriefing(new Briefing(briefingId, "t", "s", "u", "b"));
        store.CreateSession(MinimalSession(sessionId, new[] { charId }, briefingId));

        var sequences = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var u = store.CommitUtterance(OpportunityId.New(), sessionId, charId, $"line {i}", null, null, DateTimeOffset.UtcNow);
            sequences.Add(u.Sequence);
        }

        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, sequences);
    }

    [Fact]
    public void Sequence_continues_after_reopen()
    {
        var charId = CharacterId.New();
        var sessionId = SessionId.New();
        var briefingId = BriefingId.New();

        using (var store = Open())
        {
            store.SaveBriefing(new Briefing(briefingId, "t", "s", "u", "b"));
            store.CreateSession(MinimalSession(sessionId, new[] { charId }, briefingId));
            store.CommitUtterance(OpportunityId.New(), sessionId, charId, "first", null, null, DateTimeOffset.UtcNow);
            store.CommitUtterance(OpportunityId.New(), sessionId, charId, "second", null, null, DateTimeOffset.UtcNow);
        }

        using (var reopened = Open())
        {
            Assert.Equal(2, reopened.MaxSequence(sessionId));
            var u = reopened.CommitUtterance(OpportunityId.New(), sessionId, charId, "third", null, null, DateTimeOffset.UtcNow);
            Assert.Equal(3, u.Sequence);
        }
    }

    [Fact]
    public void Second_utterance_for_the_same_opportunity_is_rejected()
    {
        var charId = CharacterId.New();
        var sessionId = SessionId.New();
        var briefingId = BriefingId.New();
        var oppId = OpportunityId.New();

        using var store = Open();
        store.SaveBriefing(new Briefing(briefingId, "t", "s", "u", "b"));
        store.CreateSession(MinimalSession(sessionId, new[] { charId }, briefingId));
        store.CommitUtterance(oppId, sessionId, charId, "once", null, null, DateTimeOffset.UtcNow);

        Assert.Throws<SqliteException>(() =>
            store.CommitUtterance(oppId, sessionId, charId, "twice", null, null, DateTimeOffset.UtcNow));

        // The rejected second commit left no extra row behind.
        Assert.Single(store.GetCommittedUtterances(sessionId));
        Assert.Equal(1, store.MaxSequence(sessionId));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}

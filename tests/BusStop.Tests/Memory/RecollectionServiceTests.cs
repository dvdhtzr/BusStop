using BusStop.Domain;
using BusStop.Memory;
using BusStop.Persistence;
using Microsoft.Data.Sqlite;

namespace BusStop.Tests.Memory;

public sealed class RecollectionServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"busstop-recall-{Guid.NewGuid():N}.db");

    private static readonly CharacterId A = CharacterId.New();
    private static readonly CharacterId B = CharacterId.New();
    private static readonly CharacterId C = CharacterId.New();
    private static readonly CharacterId D = CharacterId.New();

    private ConversationStore Open()
    {
        var store = ConversationStore.OpenFile(_path);
        store.InitializeSchema();
        return store;
    }

    private static SessionId SeedEndedSession(
        ConversationStore store, IReadOnlyList<CharacterId> participants,
        (CharacterId Speaker, string Text)[] lines, DateTimeOffset createdAt)
    {
        var briefing = new Briefing(BriefingId.New(), "t", "s", "u", "b");
        store.SaveBriefing(briefing);
        var id = SessionId.New();
        store.CreateSession(new Session(
            id, participants, SessionMode.FreshEncounter, briefing.Id,
            "p", "m", "c", SessionStatus.Ended, createdAt, createdAt));
        foreach (var (speaker, text) in lines)
            store.CommitUtterance(OpportunityId.New(), id, speaker, text, null, null, createdAt);
        return id;
    }

    [Fact]
    public void SelectSpread_returns_all_when_within_max()
    {
        var items = new[] { 1, 2, 3 };
        Assert.Equal(items, RecollectionService.SelectSpread(items, 4, 0));
    }

    [Fact]
    public void SelectSpread_spreads_and_rotates()
    {
        var items = Enumerable.Range(0, 10).ToArray();
        var first = RecollectionService.SelectSpread(items, 4, 0);
        var rotated = RecollectionService.SelectSpread(items, 4, 1);

        Assert.Equal(4, first.Count);
        Assert.Equal(4, rotated.Count);
        Assert.Equal(new[] { 0, 2, 4, 6 }, first);      // even spread
        Assert.Equal(new[] { 1, 3, 5, 7 }, rotated);    // rotation picks different lines
    }

    [Fact]
    public void No_prior_sessions_yields_no_recollections()
    {
        using var store = Open();
        var service = new RecollectionService(store);
        Assert.Empty(service.Recall(A, new[] { A, B, C }, SessionId.New()));
    }

    [Fact]
    public void Recalls_evidence_linked_lines_from_a_prior_session()
    {
        using var store = Open();
        var prior = SeedEndedSession(store, new[] { A, B, C }, new[]
        {
            (A, "A line one"), (B, "B line two"), (C, "C line three")
        }, DateTimeOffset.UtcNow.AddHours(-1));

        var service = new RecollectionService(store);
        var recollections = service.Recall(A, new[] { A, B, C }, SessionId.New());

        Assert.NotEmpty(recollections);
        Assert.All(recollections, r =>
        {
            Assert.Equal(A, r.Owner);                 // participant-specific
            Assert.Equal(prior, r.SourceSession);     // evidence-linked to the prior session
            Assert.NotEqual(default, r.SourceUtterance);
            Assert.False(string.IsNullOrEmpty(r.Content));
        });
    }

    [Fact]
    public void Does_not_recall_sessions_with_a_different_participant_set()
    {
        using var store = Open();
        // A prior session among a DIFFERENT set of people (A, B, D) — not this room.
        SeedEndedSession(store, new[] { A, B, D }, new[] { (A, "OTHER_ROOM_LINE") }, DateTimeOffset.UtcNow.AddHours(-1));

        var service = new RecollectionService(store);
        var recollections = service.Recall(A, new[] { A, B, C }, SessionId.New());

        Assert.Empty(recollections);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}

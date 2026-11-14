using BusStop.Context;
using BusStop.Coordinator;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Models;
using BusStop.Persistence;

namespace BusStop.Tests.Engine;

public sealed class ConversationEngineTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"busstop-engine-{Guid.NewGuid():N}.db");

    private static readonly CharacterProfile Arthur = new(CharacterId.New(), 1, "Arthur", "A machinist.", new[] { "ornery" });
    private static readonly CharacterProfile Maya = new(CharacterId.New(), 1, "Maya", "A nurse.", new[] { "empathetic" });
    private static readonly CharacterProfile Daniel = new(CharacterId.New(), 1, "Daniel", "A teacher.", new[] { "curious" });
    private static readonly IReadOnlyList<CharacterProfile> Cast = new[] { Arthur, Maya, Daniel };
    private static readonly Briefing Story = new(BriefingId.New(), "Factory to reopen", "The Wire", "https://x", "A closed plant will reopen.");

    private ConversationStore OpenStore()
    {
        var store = ConversationStore.OpenFile(_path);
        store.InitializeSchema();
        return store;
    }

    private SessionId SeedSession(ConversationStore store)
    {
        foreach (var c in Cast) store.SaveCharacter(c);
        store.SaveBriefing(Story);
        var id = SessionId.New();
        store.CreateSession(new Session(
            id, Cast.Select(c => c.Id).ToList(), SessionMode.FreshEncounter, Story.Id,
            "proto-1", "stub/stub-digest", "ctx-1", SessionStatus.Active, DateTimeOffset.UtcNow, null));
        return id;
    }

    private static ConversationEngine Engine(ConversationStore store, SessionId session, IModelClient model, EngineOptions? options = null) =>
        new(store, model, new ParticipantContextBuilder(), session, Cast, Story, options);

    [Fact]
    public void Full_run_commits_utterances_in_order_and_reaches_natural_end()
    {
        // Opening: three speak; then two full silent rounds end the conversation.
        var script = new[]
        {
            StubResponses.Speak("The plant mattered."),
            StubResponses.Speak("It did."),
            StubResponses.Speak("Did it, really?"),
            StubResponses.Pass(), StubResponses.Pass(), StubResponses.Pass(),
            StubResponses.Pass(), StubResponses.Pass(), StubResponses.Pass()
        };

        using var store = OpenStore();
        var session = SeedSession(store);
        var result = Engine(store, session, new StubModelClient(script)).Run();

        Assert.Equal(TerminationReason.NaturalEnd, result.Reason);

        var committed = store.GetCommittedUtterances(session);
        Assert.Equal(new[] { "The plant mattered.", "It did.", "Did it, really?" }, committed.Select(u => u.Text));
        Assert.Equal(new long[] { 1, 2, 3 }, committed.Select(u => u.Sequence));
    }

    [Fact]
    public void Failure_is_retried_then_succeeds_without_delivering_the_failure()
    {
        var script = new[]
        {
            StubResponses.Malformed(),              // first attempt fails
            StubResponses.Speak("Recovered."),      // retry succeeds
            StubResponses.Pass(), StubResponses.Pass(), StubResponses.Pass(),
            StubResponses.Pass(), StubResponses.Pass(), StubResponses.Pass()
        };

        using var store = OpenStore();
        var session = SeedSession(store);
        var result = Engine(store, session, new StubModelClient(script), new EngineOptions(MaxAttemptsPerOpportunity: 2)).Run();

        Assert.Equal(TerminationReason.NaturalEnd, result.Reason);

        var committed = store.GetCommittedUtterances(session);
        Assert.Equal("Recovered.", committed.Single().Text);

        var attempts = store.GetAttempts(session);
        Assert.Contains(attempts, a => a.Outcome == AttemptOutcome.InvalidSchema);
        Assert.Contains(attempts, a => a.Outcome == AttemptOutcome.ValidSpeak);
    }

    [Fact]
    public void Failure_exhausting_retries_commits_no_utterance_and_continues()
    {
        var script = new[]
        {
            StubResponses.Malformed(), StubResponses.Malformed(),   // Arthur's opening: both attempts fail
            StubResponses.Pass(), StubResponses.Pass(),             // Maya, Daniel opening pass
            StubResponses.Pass(), StubResponses.Pass(), StubResponses.Pass(),
            StubResponses.Pass(), StubResponses.Pass(), StubResponses.Pass()
        };

        using var store = OpenStore();
        var session = SeedSession(store);
        var result = Engine(store, session, new StubModelClient(script), new EngineOptions(MaxAttemptsPerOpportunity: 2)).Run();

        Assert.Equal(TerminationReason.NaturalEnd, result.Reason);
        Assert.Empty(store.GetCommittedUtterances(session));
    }

    [Fact]
    public void Crash_between_inference_and_commit_leaves_no_utterance_and_is_never_delivered()
    {
        using var store = OpenStore();
        var session = SeedSession(store);

        // Simulate a crash: an opportunity and a ValidSpeak attempt were durably saved, but the
        // commit never ran (process died between inference and commit). No utterance exists.
        var oppId = OpportunityId.New();
        store.SaveOpportunity(
            new SpeakingOpportunity(oppId, session, Arthur.Id, OpportunityReason.Opening, 0, OpportunityStatus.Offered),
            ordinal: 0);
        store.SaveAttempt(new ModelAttempt(
            AttemptId.New(), oppId, Arthur.Id, "stub", "stub-digest", 1, AttemptOutcome.ValidSpeak, null, DateTimeOffset.UtcNow));

        Assert.Empty(store.GetCommittedUtterances(session));

        // Resume: the would-be speak is treated as failed and never delivered; the run continues and ends.
        var passes = Enumerable.Repeat(StubResponses.Pass(), 20).ToArray();
        var result = Engine(store, session, new StubModelClient(passes)).Run();

        Assert.Equal(TerminationReason.NaturalEnd, result.Reason);
        Assert.Empty(store.GetCommittedUtterances(session));
    }

    [Fact]
    public void Resume_continues_from_durable_state_identically_to_an_uninterrupted_run()
    {
        var script = new Func<ModelResponse[]>(() => new[]
        {
            StubResponses.Speak("The plant mattered."),
            StubResponses.Speak("It did."),
            StubResponses.Speak("Did it, really?"),
            StubResponses.Pass(), StubResponses.Pass(), StubResponses.Pass(),
            StubResponses.Pass(), StubResponses.Pass(), StubResponses.Pass()
        });

        // Uninterrupted baseline.
        string[] baseline;
        using (var store = OpenStore())
        {
            var session = SeedSession(store);
            Engine(store, session, new StubModelClient(script())).Run();
            baseline = store.GetCommittedUtterances(session).Select(u => u.Text).ToArray();
        }

        File.Delete(_path);

        // Split run: stop after 4 opportunities via budget, then resume on the same store and stub.
        using (var store = OpenStore())
        {
            var session = SeedSession(store);
            var stub = new StubModelClient(script());

            var first = Engine(store, session, stub, new EngineOptions(MaxOpportunities: 4)).Run();
            Assert.Equal(TerminationReason.BudgetReached, first.Reason);

            // A brand-new engine over the same store rebuilds coordinator state from durable records.
            var second = Engine(store, session, stub).Run();
            Assert.Equal(TerminationReason.NaturalEnd, second.Reason);

            var resumed = store.GetCommittedUtterances(session);
            Assert.Equal(baseline, resumed.Select(u => u.Text));
            Assert.Equal(new long[] { 1, 2, 3 }, resumed.Select(u => u.Sequence));
        }
    }

    private SessionId SeedSolo(ConversationStore store, CharacterProfile solo)
    {
        store.SaveCharacter(solo);
        store.SaveBriefing(Story);
        var id = SessionId.New();
        store.CreateSession(new Session(
            id, new[] { solo.Id }, SessionMode.FreshEncounter, Story.Id,
            "proto-1", "stub/stub-digest", "ctx-1", SessionStatus.Active, DateTimeOffset.UtcNow, null));
        return id;
    }

    [Fact]
    public void Self_echo_is_suppressed_and_treated_as_silence()
    {
        var solo = new CharacterProfile(CharacterId.New(), 1, "Solo", "A person.", new[] { "terse" });
        using var store = OpenStore();
        var session = SeedSolo(store, solo);

        var script = new[]
        {
            StubResponses.Speak("The plant mattered to this whole town."),
            StubResponses.Speak("The plant mattered to this whole town.") // echo of own line
        };
        var result = new ConversationEngine(
            store, new StubModelClient(script), new ParticipantContextBuilder(), session,
            new[] { solo }, Story,
            new EngineOptions(
                MaxAttemptsPerOpportunity: 1,
                SuppressSelfEcho: true,
                Coordinator: new CoordinatorOptions(EndAfterConsecutivePasses: 1))).Run();

        // Only the first line committed; the echo was suppressed, not delivered.
        Assert.Single(store.GetCommittedUtterances(session));

        // The echo was recorded honestly as its own outcome, and treated as silence (natural end).
        Assert.Contains(store.GetAttempts(session), a => a.Outcome == AttemptOutcome.EchoedSelf);
        Assert.Equal(TerminationReason.NaturalEnd, result.Reason);
    }

    [Fact]
    public void Without_suppression_a_repeat_is_committed()
    {
        var solo = new CharacterProfile(CharacterId.New(), 1, "Solo", "A person.", new[] { "terse" });
        using var store = OpenStore();
        var session = SeedSolo(store, solo);

        var script = new[]
        {
            StubResponses.Speak("The plant mattered to this whole town."),
            StubResponses.Speak("The plant mattered to this whole town.")
        };
        new ConversationEngine(
            store, new StubModelClient(script), new ParticipantContextBuilder(), session,
            new[] { solo }, Story,
            new EngineOptions(MaxOpportunities: 2, SuppressSelfEcho: false)).Run();

        // With suppression off (the baseline), the repeat is committed as a second utterance.
        Assert.Equal(2, store.GetCommittedUtterances(session).Count);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}

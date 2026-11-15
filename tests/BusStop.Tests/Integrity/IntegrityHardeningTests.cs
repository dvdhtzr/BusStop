using System.Text.Json;
using BusStop.Console;
using BusStop.Context;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Memory;
using BusStop.Models;
using BusStop.Persistence;
using Microsoft.Data.Sqlite;

namespace BusStop.Tests.Integrity;

public sealed class IntegrityHardeningTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"busstop-h1-{Guid.NewGuid():N}.db");
    private readonly CharacterProfile _a = new(CharacterId.New(), 1, "A", "First person", new[] { "skeptical", "direct" });
    private readonly CharacterProfile _b = new(CharacterId.New(), 1, "B", "Second person", new[] { "warm" });
    private readonly CharacterProfile _c = new(CharacterId.New(), 1, "C", "Third person", new[] { "practical" });
    private readonly Briefing _briefing = new(BriefingId.New(), "Story", "Source", "https://example.com/story", "Body");

    private ConversationStore Open()
    {
        var store = ConversationStore.OpenFile(_file);
        store.InitializeSchema();
        return store;
    }

    private SessionId Seed(ConversationStore store, DateTimeOffset? created = null,
        SessionMode mode = SessionMode.FreshEncounter, SessionStatus status = SessionStatus.Active,
        DateTimeOffset? ended = null)
    {
        foreach (var c in new[] { _a, _b, _c }) store.SaveCharacter(c);
        store.SaveBriefing(_briefing);
        var id = SessionId.New();
        store.CreateSession(new Session(id, new[] { _a.Id, _b.Id, _c.Id }, mode, _briefing.Id,
            "floor-test", "stub/stub-digest", "context-test", status,
            created ?? DateTimeOffset.UtcNow, ended));
        return id;
    }

    private ConversationEngine Engine(ConversationStore store, SessionId id, IModelClient client, EngineOptions? options = null) =>
        new(store, client, new ParticipantContextBuilder(), id,
            new[] { _a, _b, _c }, _briefing, options);

    [Fact]
    public void Character_version_is_append_only_and_survives_reopen()
    {
        using (var store = Open())
        {
            store.SaveCharacter(_a);
            store.SaveCharacter(_a with { Traits = new[] { "skeptical", "direct" } });
            Assert.Throws<InvalidOperationException>(() =>
                store.SaveCharacter(_a with { Persona = "Changed" }));
            Assert.Throws<InvalidOperationException>(() =>
                store.SaveCharacter(_a with { Traits = new[] { "direct", "skeptical" } }));
            store.SaveCharacter(_a with { Version = 2, Persona = "Approved new profile" });
        }
        using var reopened = Open();
        Assert.Equal("First person", reopened.GetCharacter(_a.Id, 1)!.Persona);
        Assert.Equal(new[] { "skeptical", "direct" }, reopened.GetCharacter(_a.Id, 1)!.Traits);
        Assert.Equal("Approved new profile", reopened.GetCharacter(_a.Id, 2)!.Persona);
    }

    [Fact]
    public void Briefing_id_is_immutable_and_survives_reopen()
    {
        using (var store = Open())
        {
            store.SaveBriefing(_briefing);
            store.SaveBriefing(_briefing with { });
            Assert.Throws<InvalidOperationException>(() =>
                store.SaveBriefing(_briefing with { Body = "New facts" }));
            Assert.Throws<InvalidOperationException>(() =>
                store.SaveBriefing(_briefing with { Url = "https://example.com/other" }));
        }
        using var reopened = Open();
        Assert.Equal("Body", reopened.GetBriefing(_briefing.Id)!.Body);
        Assert.Equal(_briefing.Url, reopened.GetBriefing(_briefing.Id)!.Url);
    }

    [Fact]
    public void Model_call_limit_applies_to_every_retry()
    {
        using var store = Open();
        var id = Seed(store);
        var model = new StubModelClient(new[] { StubResponses.Malformed(), StubResponses.Speak("UNEXPECTED") });
        var result = Engine(store, id, model,
            new EngineOptions(MaxModelCalls: 1, MaxAttemptsPerOpportunity: 2)).Run();
        Assert.Equal(TerminationReason.BudgetReached, result.Reason);
        Assert.Equal(1, result.ModelCalls);
        Assert.Single(store.GetAttempts(id));
        Assert.Equal(1, model.Remaining);
        Assert.Empty(store.GetCommittedUtterances(id));
    }

    [Fact]
    public void Resume_counts_durable_attempts_against_the_same_budget()
    {
        using var store = Open();
        var id = Seed(store);
        var first = OpportunityId.New();
        store.SaveOpportunity(new SpeakingOpportunity(first, id, _a.Id, OpportunityReason.Opening,
            0, OpportunityStatus.Offered), 0);
        store.SaveAttempt(new ModelAttempt(AttemptId.New(), first, _a.Id,
            "stub", "stub-digest", 1, AttemptOutcome.InvalidSchema, null, DateTimeOffset.UtcNow));

        // One call remains for the resumed session; the second response must not be consumed.
        var model = new StubModelClient(new[] { StubResponses.Pass(), StubResponses.Speak("UNEXPECTED") });
        var result = Engine(store, id, model, new EngineOptions(MaxModelCalls: 2)).Run();
        Assert.Equal(TerminationReason.BudgetReached, result.Reason);
        Assert.Equal(2, result.ModelCalls);
        Assert.Equal(2, store.GetAttempts(id).Count);
        Assert.Equal(1, model.Remaining);
        Assert.Empty(store.GetCommittedUtterances(id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replay_rejects_wrong_speaker_or_reason(bool wrongReason)
    {
        using var store = Open();
        var id = Seed(store);
        var opp = OpportunityId.New();
        store.SaveOpportunity(new SpeakingOpportunity(opp, id,
            wrongReason ? _a.Id : _c.Id,
            wrongReason ? OpportunityReason.FairOrder : OpportunityReason.Opening,
            0, OpportunityStatus.Offered), 0);
        Assert.Throws<InvalidOperationException>(() =>
            Engine(store, id, new StubModelClient(Array.Empty<ModelResponse>())));
    }

    [Fact]
    public void Replay_rejects_noncontiguous_opportunity_ordinals()
    {
        using var store = Open();
        var id = Seed(store);
        store.SaveOpportunity(new SpeakingOpportunity(OpportunityId.New(), id, _a.Id,
            OpportunityReason.Opening, 0, OpportunityStatus.Offered), ordinal: 1);
        Assert.Throws<InvalidOperationException>(() =>
            Engine(store, id, new StubModelClient(Array.Empty<ModelResponse>())));
    }

    [Fact]
    public void Recollection_does_not_read_a_later_ended_session()
    {
        using var store = Open();
        var now = DateTimeOffset.UtcNow;
        var current = Seed(store, now.AddHours(-1), SessionMode.Continuing);
        var later = Seed(store, now, SessionMode.FreshEncounter, SessionStatus.Ended, now.AddMinutes(10));
        store.CommitUtterance(OpportunityId.New(), later, _b.Id,
            "FUTURE_SECRET", null, null, now.AddMinutes(5));

        var overlapping = Seed(store, now.AddHours(-2), SessionMode.FreshEncounter,
            SessionStatus.Ended, now.AddMinutes(1));
        store.CommitUtterance(OpportunityId.New(), overlapping, _b.Id,
            "NOT_YET_FINISHED", null, null, now.AddMinutes(1));

        var memory = new RecollectionService(store);
        Assert.Empty(memory.Recall(_a.Id, new[] { _a.Id, _b.Id, _c.Id }, current));
        Assert.Empty(memory.Recall(_a.Id, new[] { _a.Id, _b.Id, _c.Id }, SessionId.New()));
    }

    [Fact]
    public void Recollection_of_ended_prior_session_is_available()
    {
        using var store = Open();
        var now = DateTimeOffset.UtcNow;
        var earlier = Seed(store, now.AddHours(-2), SessionMode.FreshEncounter,
            SessionStatus.Ended, now.AddHours(-1));
        store.CommitUtterance(OpportunityId.New(), earlier, _b.Id,
            "HISTORICAL_LINE", null, null, now.AddHours(-1));
        var current = Seed(store, now, SessionMode.Continuing);

        var memory = new RecollectionService(store);
        Assert.Contains(memory.Recall(_a.Id, new[] { _a.Id, _b.Id, _c.Id }, current),
            m => m.Content == "HISTORICAL_LINE" && m.SourceSession == earlier);
    }

    [Fact]
    public void Every_run_writes_its_own_transcript_and_diagnostics()
    {
        var output = Path.Combine(Path.GetTempPath(), $"busstop-h1-runs-{Guid.NewGuid():N}");
        try
        {
            using var store = Open();
            for (var i = 1; i <= 3; i++)
            {
                var id = Seed(store);
                var result = Engine(store, id, new StubModelClient(Enumerable.Repeat(
                    StubResponses.Pass(), 6))).Run();
                var transcript = TranscriptRenderer.RenderTranscript(store, id, new[] { _a, _b, _c });
                var diagnostics = TranscriptRenderer.RenderDiagnostics(store, id, result,
                    "stub", "stub-digest");
                RunArtifactWriter.WriteRun(output, i, transcript, diagnostics);
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(output,
                    $"run-{i:00}.diagnostics.json")));
                Assert.Equal(id.ToString(), json.RootElement.GetProperty("session").GetString());
                Assert.Equal("NoEngagement", json.RootElement.GetProperty("termination").GetString());
                Assert.Contains("# BusStop transcript", File.ReadAllText(Path.Combine(output,
                    $"run-{i:00}.transcript.md")));
            }
            Assert.Equal(3, Directory.GetFiles(output, "*.diagnostics.json").Length);
            Assert.Equal(3, Directory.GetFiles(output, "*.transcript.md").Length);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_file)) File.Delete(_file);
    }
}

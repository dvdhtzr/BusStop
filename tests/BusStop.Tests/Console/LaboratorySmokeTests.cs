using System.Text.Json;
using BusStop.Console;
using BusStop.Context;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Persistence;

namespace BusStop.Tests.Console;

public sealed class LaboratorySmokeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"busstop-lab-{Guid.NewGuid():N}.db");

    [Fact]
    public void Demo_run_produces_a_readable_transcript_and_clean_diagnostics()
    {
        using var store = ConversationStore.OpenFile(_path);
        store.InitializeSchema();
        var session = Laboratory.Seed(store);

        var result = Laboratory.BuildEngine(store, session, Laboratory.DemoModel()).Run();

        Assert.Equal(TerminationReason.NaturalEnd, result.Reason);

        var transcript = TranscriptRenderer.RenderTranscript(store, session, Laboratory.Cast);
        Assert.Contains("# BusStop transcript", transcript);
        Assert.Contains("Shuttered auto-parts plant to reopen", transcript);
        Assert.Contains("**Arthur**", transcript);
        Assert.Contains("**Maya**", transcript);
        Assert.Contains("**Daniel**", transcript);
        // A directed reply should render its addressee.
        Assert.Contains("_(to Maya)_", transcript);
        Assert.Contains("_(to Arthur)_", transcript);

        var diagnostics = TranscriptRenderer.RenderDiagnostics(store, session, result, "stub", "stub-digest");
        using var doc = JsonDocument.Parse(diagnostics);
        var root = doc.RootElement;
        Assert.Equal("NaturalEnd", root.GetProperty("termination").GetString());
        Assert.True(root.GetProperty("sequenceContiguous").GetBoolean());
        Assert.Equal(6, root.GetProperty("committedUtterances").GetInt32());
        Assert.True(root.GetProperty("config").GetProperty("mode").GetString() == "FreshEncounter");
    }

    [Fact]
    public void Each_demo_run_is_independent_and_reproducible()
    {
        string[] First()
        {
            var p = Path.Combine(Path.GetTempPath(), $"busstop-lab-{Guid.NewGuid():N}.db");
            try
            {
                using var store = ConversationStore.OpenFile(p);
                store.InitializeSchema();
                var session = Laboratory.Seed(store);
                Laboratory.BuildEngine(store, session, Laboratory.DemoModel()).Run();
                return store.GetCommittedUtterances(session).Select(u => u.Text).ToArray();
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(p)) File.Delete(p);
            }
        }

        Assert.Equal(First(), First());
    }

    [Fact]
    public void Default_and_continuing_run_plans_remain_unchanged()
    {
        var standard = Laboratory.BuildRunPlan(3);
        Assert.Equal(3, standard.Length);
        Assert.Equal(new[] { "Run 1", "Run 2", "Run 3" }, standard.Select(p => p.Label));
        Assert.All(standard, p =>
        {
            Assert.Equal(SessionMode.FreshEncounter, p.Mode);
            Assert.Equal(Laboratory.Story.Id, p.Briefing.Id);
        });

        // --continuing still creates its fixed factory -> bridge sequence,
        // irrespective of the independent-run count.
        var continuing = Laboratory.BuildRunPlan(3, continuing: true);
        Assert.Equal(2, continuing.Length);
        Assert.Equal(SessionMode.FreshEncounter, continuing[0].Mode);
        Assert.Equal(Laboratory.Story.Id, continuing[0].Briefing.Id);
        Assert.Equal(SessionMode.Continuing, continuing[1].Mode);
        Assert.Equal(Laboratory.SecondStory.Id, continuing[1].Briefing.Id);
    }

    [Fact]
    public void C1b_bridge_control_creates_only_fresh_bridge_sessions()
    {
        using var store = ConversationStore.OpenFile(_path);
        store.InitializeSchema();

        var plan = Laboratory.BuildRunPlan(3, freshBridge: true);
        Assert.Equal(3, plan.Length);
        var sessionIds = new List<SessionId>();
        foreach (var (mode, label, briefing) in plan)
        {
            Assert.Equal(SessionMode.FreshEncounter, mode);
            Assert.Equal(Laboratory.SecondStory.Id, briefing.Id);
            var session = Laboratory.Seed(store, mode, briefing: briefing);
            sessionIds.Add(session);
            var persisted = store.GetSession(session);
            Assert.NotNull(persisted);
            Assert.Equal(SessionMode.FreshEncounter, persisted.Mode);
            Assert.Equal(Laboratory.SecondStory.Id, persisted.Briefing);

            // The existing runner passes recollections only for Continuing;
            // passing null here preserves the fresh-session isolation.
            var result = new ConversationEngine(
                store, Laboratory.DemoModel(), new ParticipantContextBuilder(),
                session, Laboratory.Cast, briefing, recollections: null).Run();
            Assert.Equal(TerminationReason.NaturalEnd, result.Reason);
            var transcript = TranscriptRenderer.RenderTranscript(store, session, Laboratory.Cast);
            Assert.Contains("City to close aging Main Street bridge", transcript);
            Assert.DoesNotContain("Shuttered auto-parts plant to reopen", transcript);
        }
        Assert.Equal(3, sessionIds.Distinct().Count());
    }

    [Fact]
    public void Bridge_only_control_and_continuing_mode_are_mutually_exclusive()
    {
        Assert.Throws<ArgumentException>(() =>
            Laboratory.BuildRunPlan(3, continuing: true, freshBridge: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => Laboratory.BuildRunPlan(0));
    }

    [Fact]
    public void C2_attribution_is_opt_in_and_default_prompt_is_unchanged()
    {
        var handles = Laboratory.Cast.Select(c => new ParticipantHandle(c.Id, c.Name)).ToArray();
        var opening = new SpeakingOpportunity(
            OpportunityId.New(), SessionId.New(), Laboratory.Arthur.Id,
            OpportunityReason.Opening, 0, OpportunityStatus.Offered);
        var inputs = new ContextInputs(
            Laboratory.Arthur, handles, Laboratory.SecondStory,
            Array.Empty<Utterance>(), opening);

        var original = new ParticipantContextBuilder().Build(inputs);
        var control = Laboratory.BuildContextBuilder(false).Build(inputs);
        var treatment = Laboratory.BuildContextBuilder(true).Build(inputs);

        Assert.Equal(original.Prompt, control.Prompt);
        Assert.Equal(Laboratory.ContextVersion, Laboratory.GetContextVersion(false));
        Assert.Equal(Laboratory.C2ContextVersion, Laboratory.GetContextVersion(true));
        Assert.DoesNotContain(Laboratory.C2AttributionGuidance, control.Prompt);
        Assert.Contains(Laboratory.C2AttributionGuidance, treatment.Prompt);
        Assert.StartsWith(
            control.Prompt[..control.Prompt.IndexOf("# The story you are discussing", StringComparison.Ordinal)],
            treatment.Prompt[..treatment.Prompt.IndexOf("# The story you are discussing", StringComparison.Ordinal)]
                .Replace(" " + Laboratory.C2AttributionGuidance, ""));
        Assert.Equal(control.AddressableParticipants, treatment.AddressableParticipants);
        Assert.Equal(control.VisibleUtteranceIds, treatment.VisibleUtteranceIds);
    }

    [Fact]
    public void C2_variant_and_control_pin_distinct_context_versions_in_diagnostics()
    {
        using var store = ConversationStore.OpenFile(_path);
        store.InitializeSchema();

        foreach (var enabled in new[] { false, true })
        {
            var version = Laboratory.GetContextVersion(enabled);
            var session = Laboratory.Seed(
                store, SessionMode.FreshEncounter, briefing: Laboratory.SecondStory,
                contextVersion: version);
            var result = new ConversationEngine(
                store, Laboratory.DemoModel(), Laboratory.BuildContextBuilder(enabled),
                session, Laboratory.Cast, Laboratory.SecondStory).Run();

            Assert.Equal(TerminationReason.NaturalEnd, result.Reason);
            Assert.Equal(version, store.GetSession(session)!.ContextVersion);
            var diagnostics = TranscriptRenderer.RenderDiagnostics(
                store, session, result, "stub", "stub-digest");
            using var doc = JsonDocument.Parse(diagnostics);
            Assert.Equal(version, doc.RootElement.GetProperty("config")
                .GetProperty("contextVersion").GetString());
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}

using System.Text.Json;
using BusStop.Console;
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

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}

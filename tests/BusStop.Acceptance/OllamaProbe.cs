using BusStop.Console;
using BusStop.Engine;
using BusStop.Models;
using BusStop.Persistence;

namespace BusStop.Acceptance;

/// <summary>The real-model acceptance probe. It is not part of the gating suite: it runs a full session
/// against a local Ollama model and asserts only mechanical invariants — valid structured outputs,
/// monotonic committed sequence, no duplication, a clean termination. Conversational quality is for a
/// human to read in the transcript it writes, not for this test to judge. It skips cleanly when no
/// Ollama server is reachable.
///
/// Configure with environment variables:
///   BUSSTOP_OLLAMA_MODEL  (default: llama3.2)
///   BUSSTOP_OLLAMA_URL    (default: http://localhost:11434)
///   BUSSTOP_OUT           (default: a temp directory) — where transcript.md and diagnostics.json land.</summary>
public sealed class OllamaProbe
{
    [Fact]
    public void Real_model_run_is_mechanically_valid_and_emits_artifacts()
    {
        var url = Environment.GetEnvironmentVariable("BUSSTOP_OLLAMA_URL") is { } urlValue ? new Uri(urlValue) : null;

        if (!OllamaAdapter.IsAvailable(url))
        {
            // No server reachable: skip the probe rather than fail the build.
            return;
        }

        var model = Environment.GetEnvironmentVariable("BUSSTOP_OLLAMA_MODEL") ?? "llama3.2";
        var outDir = Environment.GetEnvironmentVariable("BUSSTOP_OUT")
                     ?? Path.Combine(Path.GetTempPath(), $"busstop-probe-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(outDir);

        var dbPath = Path.Combine(outDir, "probe.db");
        using var store = ConversationStore.OpenFile(dbPath);
        store.InitializeSchema();
        var session = Laboratory.Seed(store);

        using var client = new OllamaAdapter(model);
        var engine = new ConversationEngine(
            store, client, new BusStop.Context.ParticipantContextBuilder(), session,
            Laboratory.Cast, Laboratory.Story,
            new EngineOptions(MaxOpportunities: 40, MaxModelCalls: 80, MaxAttemptsPerOpportunity: 2));

        var result = engine.Run();

        var transcript = TranscriptRenderer.RenderTranscript(store, session, Laboratory.Cast);
        var diagnostics = TranscriptRenderer.RenderDiagnostics(store, session, result, client.ModelName, client.ModelDigest);
        File.WriteAllText(Path.Combine(outDir, "transcript.md"), transcript);
        File.WriteAllText(Path.Combine(outDir, "diagnostics.json"), diagnostics);

        // Mechanical invariants only.
        Assert.True(result.Reason is TerminationReason.NaturalEnd or TerminationReason.BudgetReached);

        var committed = store.GetCommittedUtterances(session);
        var sequences = committed.Select(c => c.Sequence).ToArray();
        Assert.Equal(sequences.Distinct().Count(), sequences.Length);               // no duplicates
        Assert.Equal(Enumerable.Range(1, committed.Count).Select(i => (long)i), sequences); // contiguous, 1-based

        foreach (var u in committed)
            Assert.False(string.IsNullOrWhiteSpace(u.Text));                         // no empty committed speech
    }
}

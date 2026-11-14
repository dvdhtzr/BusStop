using BusStop.Console;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Models;
using BusStop.Persistence;

// Minimal laboratory runner for Phase 1. Writes a Markdown transcript and a JSON diagnostics sidecar,
// and prints the transcript.
//
// Usage:
//   dotnet run --project src/BusStop.Console -- [--model stub|ollama] [--ollama-model <name>]
//                                              [--ollama-url <url>] [--db <path>] [--out <dir>]
//
// The stub model (default) runs a deterministic demo with no external dependency. The ollama model runs
// a real conversation against a local Ollama server.

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
string? Arg(string name)
{
    var i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

var modelKind = Arg("--model") ?? "stub";
var outDir = Arg("--out") ?? Directory.GetCurrentDirectory();
Directory.CreateDirectory(outDir);
var dbPath = Arg("--db") ?? Path.Combine(outDir, $"busstop-lab-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");

IModelClient client;
string modelConfigVersion;

switch (modelKind)
{
    case "stub":
        client = Laboratory.DemoModel();
        modelConfigVersion = "stub/stub-digest";
        break;

    case "ollama":
        var ollamaModel = Arg("--ollama-model") ?? "llama3.2";
        var ollamaUrl = Arg("--ollama-url") is { } u ? new Uri(u) : null;
        if (!OllamaAdapter.IsAvailable(ollamaUrl))
        {
            System.Console.Error.WriteLine(
                $"No Ollama server reachable at {ollamaUrl?.ToString() ?? "http://localhost:11434"}. " +
                "Start Ollama and ensure the model is pulled, then retry.");
            return 2;
        }
        var adapter = new OllamaAdapter(ollamaModel, ollamaUrl);
        client = adapter;
        modelConfigVersion = $"{adapter.ModelName}/{adapter.ModelDigest}";
        break;

    default:
        System.Console.Error.WriteLine($"Unknown --model '{modelKind}'. Use 'stub' or 'ollama'.");
        return 2;
}

using var store = ConversationStore.OpenFile(dbPath);
store.InitializeSchema();
var session = Laboratory.Seed(store, SessionMode.FreshEncounter, modelConfigVersion);

var options = modelKind == "ollama"
    ? new EngineOptions(MaxOpportunities: 40, MaxModelCalls: 80, MaxAttemptsPerOpportunity: 2)
    : new EngineOptions();

var result = new ConversationEngine(
    store, client, new BusStop.Context.ParticipantContextBuilder(), session,
    Laboratory.Cast, Laboratory.Story, options).Run();

var transcript = TranscriptRenderer.RenderTranscript(store, session, Laboratory.Cast);
var diagnostics = TranscriptRenderer.RenderDiagnostics(store, session, result, client.ModelName, client.ModelDigest);

var transcriptPath = Path.Combine(outDir, "transcript.md");
var diagnosticsPath = Path.Combine(outDir, "diagnostics.json");
File.WriteAllText(transcriptPath, transcript);
File.WriteAllText(diagnosticsPath, diagnostics);

(client as IDisposable)?.Dispose();

var committedCount = store.GetCommittedUtterances(session).Count;

System.Console.WriteLine(transcript);
System.Console.WriteLine("---");
System.Console.WriteLine($"Model: {client.ModelName}  Termination: {result.Reason}  Opportunities: {result.Opportunities}  Model calls: {result.ModelCalls}");
if (modelKind == "ollama" && committedCount == 0)
{
    System.Console.Error.WriteLine(
        "WARNING: no one spoke — every model call failed. Check that Ollama is running and that " +
        $"--ollama-model ('{Arg("--ollama-model") ?? "llama3.2"}') exactly matches a name from `ollama list`.");
}
System.Console.WriteLine($"Transcript:  {transcriptPath}");
System.Console.WriteLine($"Diagnostics: {diagnosticsPath}");
System.Console.WriteLine($"Database:    {dbPath}");
return 0;

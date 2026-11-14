using BusStop.Console;
using BusStop.Models;
using BusStop.Persistence;

// Minimal laboratory runner for Phase 1. Offline by default: runs the deterministic demo on the stub
// model, writes a Markdown transcript and a JSON diagnostics sidecar, and prints the transcript.
//
// Usage:
//   dotnet run --project src/BusStop.Console -- [--db <path>] [--out <dir>] [--model stub]

var args0 = Environment.GetCommandLineArgs().Skip(1).ToArray();
string? Arg(string name)
{
    var i = Array.IndexOf(args0, name);
    return i >= 0 && i + 1 < args0.Length ? args0[i + 1] : null;
}

var model = Arg("--model") ?? "stub";
if (model != "stub")
{
    System.Console.Error.WriteLine($"Model '{model}' is not available yet. Only the stub model is wired in Phase 1 step 7; the Ollama adapter arrives in step 8.");
    return 2;
}

var outDir = Arg("--out") ?? Directory.GetCurrentDirectory();
var dbPath = Arg("--db") ?? Path.Combine(outDir, $"busstop-lab-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
Directory.CreateDirectory(outDir);

IModelClient client = Laboratory.DemoModel();

using var store = ConversationStore.OpenFile(dbPath);
store.InitializeSchema();
var session = Laboratory.Seed(store);

var result = Laboratory.BuildEngine(store, session, client).Run();

var transcript = TranscriptRenderer.RenderTranscript(store, session, Laboratory.Cast);
var diagnostics = TranscriptRenderer.RenderDiagnostics(store, session, result, client.ModelName, client.ModelDigest);

var transcriptPath = Path.Combine(outDir, "transcript.md");
var diagnosticsPath = Path.Combine(outDir, "diagnostics.json");
File.WriteAllText(transcriptPath, transcript);
File.WriteAllText(diagnosticsPath, diagnostics);

System.Console.WriteLine(transcript);
System.Console.WriteLine("---");
System.Console.WriteLine($"Termination: {result.Reason}  Opportunities: {result.Opportunities}  Model calls: {result.ModelCalls}");
System.Console.WriteLine($"Transcript:  {transcriptPath}");
System.Console.WriteLine($"Diagnostics: {diagnosticsPath}");
System.Console.WriteLine($"Database:    {dbPath}");
return 0;

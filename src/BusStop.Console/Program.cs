using System.Text;
using BusStop.Console;
using BusStop.Coordinator;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Memory;
using BusStop.Models;
using BusStop.Persistence;

// Minimal laboratory runner for Phase 1. Writes a Markdown transcript and a JSON diagnostics sidecar,
// and prints the transcript. With --runs N it runs N independent conversations and writes one combined
// batch.md with a summary table — the file to send for review.
//
// Usage:
//   dotnet run --project src/BusStop.Console -- [--model stub|ollama] [--ollama-model <name>]
//       [--ollama-url <url>] [--no-think] [--ollama-timeout <seconds>] [--runs <n>] [--db <path>] [--out <dir>]

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
string? Arg(string name)
{
    var i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

var modelKind = Arg("--model") ?? "stub";
var runs = int.TryParse(Arg("--runs"), out var r) && r > 0 ? r : 1;
var outDir = Arg("--out") ?? Directory.GetCurrentDirectory();
Directory.CreateDirectory(outDir);
var dbPath = Arg("--db") ?? Path.Combine(outDir, $"busstop-lab-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");

const double Temperature = 0.8;
bool? thinkSetting = null;

Func<IModelClient> makeClient;
OllamaAdapter? ollamaAdapter = null;
string modelConfigVersion;

switch (modelKind)
{
    case "stub":
        makeClient = () => Laboratory.DemoModel();
        modelConfigVersion = "stub/stub-digest";
        break;

    case "ollama":
        var ollamaModel = Arg("--ollama-model") ?? "llama3.2";
        var ollamaUrl = Arg("--ollama-url") is { } u ? new Uri(u) : null;
        var timeoutSeconds = int.TryParse(Arg("--ollama-timeout"), out var t) ? t : 300;
        if (!OllamaAdapter.IsAvailable(ollamaUrl))
        {
            System.Console.Error.WriteLine(
                $"No Ollama server reachable at {ollamaUrl?.ToString() ?? "http://localhost:11434"}. " +
                "Start Ollama and ensure the model is pulled, then retry.");
            return 2;
        }
        thinkSetting = argv.Contains("--no-think") ? false : null;
        ollamaAdapter = new OllamaAdapter(ollamaModel, ollamaUrl, temperature: Temperature, timeout: TimeSpan.FromSeconds(timeoutSeconds), think: thinkSetting);
        makeClient = () => ollamaAdapter;
        modelConfigVersion = $"{ollamaAdapter.ModelName}/{ollamaAdapter.ModelDigest}";
        System.Console.WriteLine($"Using Ollama model '{ollamaModel}'{(thinkSetting == false ? " (thinking off)" : "")}. {runs} run(s). The first turn loads the model and can take a while.");
        break;

    default:
        System.Console.Error.WriteLine($"Unknown --model '{modelKind}'. Use 'stub' or 'ollama'.");
        return 2;
}

var engineOptions = modelKind == "ollama"
    ? new EngineOptions(
        MaxOpportunities: 18,
        MaxModelCalls: 36,
        MaxAttemptsPerOpportunity: 2,
        Coordinator: new CoordinatorOptions(EndAfterConsecutivePasses: 2))
    : new EngineOptions();

var runConfig = new RunConfig(
    engineOptions.MaxOpportunities,
    engineOptions.MaxModelCalls,
    engineOptions.MaxAttemptsPerOpportunity,
    engineOptions.Coordinator?.EndAfterConsecutivePasses,
    Temperature,
    thinkSetting);

Action<string>? trace = modelKind == "ollama"
    ? line => System.Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}")
    : null;

using var store = ConversationStore.OpenFile(dbPath);
store.InitializeSchema();

// --continuing demonstrates memory: run a fresh "prior" conversation, then a continuing one between the
// same characters that can recall it. Otherwise run N independent fresh conversations.
var continuing = argv.Contains("--continuing");
var recollectionService = new RecollectionService(store);
var impressionService = new ImpressionService(store);

var plan = continuing
    ? new[]
      {
          (Mode: SessionMode.FreshEncounter, Label: "Prior conversation", Briefing: Laboratory.Story),
          (Mode: SessionMode.Continuing, Label: "Continuing conversation", Briefing: Laboratory.SecondStory)
      }
    : Enumerable.Range(1, runs)
        .Select(i => (Mode: SessionMode.FreshEncounter, Label: $"Run {i}", Briefing: Laboratory.Story))
        .ToArray();

var runSections = new StringBuilder();
var summary = new StringBuilder();
summary.AppendLine("| Session | Termination | Opportunities | Utterances |");
summary.AppendLine("| --- | --- | --- | --- |");

string lastTranscript = string.Empty;
string lastDiagnostics = string.Empty;

foreach (var (mode, label, briefing) in plan)
{
    if (plan.Length > 1) System.Console.WriteLine($"\n==== {label} ====");

    var client = makeClient();
    var session = Laboratory.Seed(store, mode, modelConfigVersion, briefing);
    var result = new ConversationEngine(
        store, client, new BusStop.Context.ParticipantContextBuilder(), session,
        Laboratory.Cast, briefing, engineOptions, trace: trace,
        recollections: mode == SessionMode.Continuing ? recollectionService : null,
        impressions: mode == SessionMode.Continuing ? impressionService : null).Run();

    // Reflect so a later continuing conversation carries each character's sense of the others.
    if (continuing)
    {
        trace?.Invoke("… (reflecting on the conversation)");
        impressionService.Reflect(session, makeClient(), Laboratory.Cast);
    }

    var transcript = TranscriptRenderer.RenderTranscript(store, session, Laboratory.Cast);
    var diagnostics = TranscriptRenderer.RenderDiagnostics(store, session, result, client.ModelName, client.ModelDigest, runConfig);
    var committed = store.GetCommittedUtterances(session).Count;

    lastTranscript = transcript;
    lastDiagnostics = diagnostics;

    summary.AppendLine($"| {label} | {result.Reason} | {result.Opportunities} | {committed} |");
    runSections.AppendLine($"## {label}");
    runSections.AppendLine();
    runSections.AppendLine($"_{result.Reason}, {result.Opportunities} opportunities, {committed} utterances._");
    runSections.AppendLine();
    runSections.AppendLine(transcript);
    runSections.AppendLine();
    runSections.AppendLine("---");
    runSections.AppendLine();
}

ollamaAdapter?.Dispose();

// Single run: keep transcript.md / diagnostics.json for the usual single-run flow.
File.WriteAllText(Path.Combine(outDir, "transcript.md"), lastTranscript);
File.WriteAllText(Path.Combine(outDir, "diagnostics.json"), lastDiagnostics);

// The combined batch file — the one to send for review.
var batch = new StringBuilder();
batch.AppendLine($"# BusStop batch — {plan.Length} session(s)");
batch.AppendLine();
batch.AppendLine($"**Model:** {(ollamaAdapter is null ? modelConfigVersion : ollamaAdapter.ModelName)}  ");
batch.AppendLine($"**Protocol:** {Laboratory.ProtocolVersion} · {Laboratory.ContextVersion}  ");
batch.AppendLine($"**Generated:** {DateTime.Now:yyyy-MM-dd HH:mm}");
batch.AppendLine();
batch.AppendLine("## Summary");
batch.AppendLine();
batch.Append(summary);
batch.AppendLine();
batch.AppendLine("---");
batch.AppendLine();
batch.Append(runSections);
var batchPath = Path.Combine(outDir, "batch.md");
File.WriteAllText(batchPath, batch.ToString());

if (plan.Length == 1) System.Console.WriteLine(lastTranscript);
System.Console.WriteLine("\n---");
System.Console.WriteLine($"Completed {plan.Length} session(s).");
System.Console.WriteLine(summary.ToString());
System.Console.WriteLine($"Combined report: {batchPath}");
System.Console.WriteLine($"Database:        {dbPath}");
return 0;

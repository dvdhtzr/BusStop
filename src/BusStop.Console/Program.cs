using System.Text;
using BusStop.Console;
using BusStop.Coordinator;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Memory;
using BusStop.Models;
using BusStop.Persistence;
using BusStop.Sources;

// Minimal laboratory runner for Phase 1. Writes a Markdown transcript and a JSON diagnostics sidecar,
// and prints the transcript. With --runs N it runs N independent conversations and writes one combined
// batch.md with a summary table — the file to send for review.
//
// Usage:
//   dotnet run --project src/BusStop.Console -- [--model stub|ollama] [--ollama-model <name>]
//       [--ollama-url <url>] [--no-think] [--ollama-timeout <seconds>] [--runs <n>] [--db <path>] [--out <dir>]
//       [--continuing | --fresh-bridge] [--c2-attribution]
//   dotnet run --project src/BusStop.Console -- --inspect-document <file.txt|file.md>
//   dotnet run --project src/BusStop.Console -- --discuss-document <file.txt|file.md> --model ollama ...
//   dotnet run --project src/BusStop.Console -- --rag-document <file.txt|file.md> --model ollama ...

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
string? Arg(string name)
{
    var i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

// Inspection proves local evidence capture without sending document text to Ollama
// or changing any existing conversation or memory behavior.
if (argv.Contains("--inspect-document"))
{
    if (string.IsNullOrWhiteSpace(Arg("--inspect-document")) ||
        argv.Any(x => x is "--model" or "--continuing" or "--fresh-bridge" or
            "--c2-attribution" or "--runs" or "--db" or "--out" or
            "--discuss-document" or "--rag-document"))
    {
        System.Console.Error.WriteLine(
            "Usage: --inspect-document <file.txt|file.md> (no conversation flags).");
        return 2;
    }

    try
    {
        var evidence = new LocalTextDocumentImporter().Import(Arg("--inspect-document")!);
        System.Console.WriteLine($"Captured local document: {evidence.Descriptor.Title}");
        System.Console.WriteLine($"Snapshot: {evidence.SnapshotId}");
        System.Console.WriteLine($"Text SHA-256: {evidence.TextSha256}");
        System.Console.WriteLine($"Characters: {evidence.SnapshotText.Length}");
        System.Console.WriteLine($"Citable passages: {evidence.Passages.Count}");
        System.Console.WriteLine($"First passage ID: {evidence.Passages[0].PassageId}");
        System.Console.WriteLine("Inspection only: no Ollama call, conversation, or RAG retrieval.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
        NotSupportedException or ArgumentException)
    {
        System.Console.Error.WriteLine($"Document inspection failed: {ex.Message}");
        return 2;
    }
}

var modelKind = Arg("--model") ?? "stub";
var runs = int.TryParse(Arg("--runs"), out var r) && r > 0 ? r : 1;
var continuing = argv.Contains("--continuing");
var freshBridge = argv.Contains("--fresh-bridge");
var c2Attribution = argv.Contains("--c2-attribution");
var discussDocument = argv.Contains("--discuss-document");
var ragDocument = argv.Contains("--rag-document");
var contextVersion = ragDocument
    ? EvidenceBriefingBuilder.RetrievalContextVersion
    : discussDocument
        ? EvidenceBriefingBuilder.ContextVersion
        : Laboratory.GetContextVersion(c2Attribution);

if ((discussDocument || ragDocument) &&
    ((discussDocument && ragDocument) ||
     string.IsNullOrWhiteSpace(Arg(discussDocument ? "--discuss-document" : "--rag-document")) ||
     modelKind != "ollama" || continuing || freshBridge || c2Attribution || runs != 1))
{
    System.Console.Error.WriteLine(
        "Document discussion requires --model ollama, a single fresh session, and " +
        "exactly one of --discuss-document or --rag-document; " +
        "it cannot be combined with --continuing, --fresh-bridge or --c2-attribution.");
    return 2;
}

EvidenceSnapshot? documentSnapshot = null;
Briefing? documentBriefing = null;
IReadOnlyList<string>? documentPassages = null;
if (discussDocument || ragDocument)
{
    try
    {
        documentSnapshot = new LocalTextDocumentImporter().Import(
            Arg(discussDocument ? "--discuss-document" : "--rag-document")!);
        if (ragDocument)
        {
            documentBriefing = EvidenceBriefingBuilder.BuildForRetrieval(documentSnapshot);
            System.Console.WriteLine(
                $"Document '{documentSnapshot.Descriptor.Title}': {documentSnapshot.Passages.Count} " +
                "passages available to local lexical retrieval; maximum 3 per speaking turn.");
        }
        else
        {
            (documentBriefing, documentPassages) = EvidenceBriefingBuilder.Build(documentSnapshot);
            System.Console.WriteLine(
                $"Document '{documentSnapshot.Descriptor.Title}': showing " +
                $"{documentPassages.Count} of {documentSnapshot.Passages.Count} passages. " +
                "No on-demand retrieval (RAG) in R1b.");
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
        NotSupportedException or ArgumentException or InvalidOperationException)
    {
        System.Console.Error.WriteLine($"Document ingestion failed: {ex.Message}");
        return 2;
    }
}
if (continuing && freshBridge)
{
    System.Console.Error.WriteLine("--fresh-bridge is a fresh-session control and cannot be combined with --continuing.");
    return 2;
}
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

// C1b: --fresh-bridge starts independent FreshEncounter bridge sessions with no
// recollection context. The established --continuing pair and default factory runs
// retain their original behavior.
var recollectionService = new RecollectionService(store);
if (documentSnapshot is not null)
    store.SaveEvidenceSnapshot(documentSnapshot);
var plan = discussDocument || ragDocument
    ? new[] { (Mode: SessionMode.FreshEncounter, Label: ragDocument
        ? "Document discussion (lexical RAG)" : "Document discussion", Briefing: documentBriefing!) }
    : Laboratory.BuildRunPlan(runs, continuing, freshBridge);

var runSections = new StringBuilder();
var summary = new StringBuilder();
summary.AppendLine("| Session | Termination | Opportunities | Utterances |");
summary.AppendLine("| --- | --- | --- | --- |");

string lastTranscript = string.Empty;
string lastDiagnostics = string.Empty;
var runIndex = 0;

foreach (var (mode, label, briefing) in plan)
{
    runIndex++;
    if (plan.Length > 1) System.Console.WriteLine($"\n==== {label} ====");

    var client = makeClient();
    var session = Laboratory.Seed(store, mode, modelConfigVersion, briefing, contextVersion);
    if (documentSnapshot is not null)
    {
        if (ragDocument)
            store.PinSessionEvidenceForRetrieval(session, documentSnapshot.SnapshotId);
        else
            store.PinSessionEvidence(session, documentSnapshot.SnapshotId, documentPassages!);
    }
    var result = new ConversationEngine(
        store, client, Laboratory.BuildContextBuilder(c2Attribution), session,
        Laboratory.Cast, briefing, engineOptions, trace: trace,
        recollections: mode == SessionMode.Continuing ? recollectionService : null,
        sourceProvider: ragDocument
            ? new LocalOpportunitySourceProvider(store, documentSnapshot!)
            : null).Run();

    var transcript = TranscriptRenderer.RenderTranscript(store, session, Laboratory.Cast);
    var diagnostics = TranscriptRenderer.RenderDiagnostics(store, session, result, client.ModelName, client.ModelDigest, runConfig);
    var committed = store.GetCommittedUtterances(session).Count;

    lastTranscript = transcript;
    lastDiagnostics = diagnostics;

    // Every run has its own transcript and diagnostics, even in a batch.
    RunArtifactWriter.WriteRun(outDir, runIndex, session, transcript, diagnostics);

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
batch.AppendLine($"**Protocol:** {Laboratory.ProtocolVersion} · {contextVersion}  ");
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

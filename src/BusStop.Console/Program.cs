using System.Text;
using System.Text.Json;
using BusStop.Console;
using BusStop.Coordinator;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Memory;
using BusStop.Moderation;
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

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
string? Arg(string name)
{
    var i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

// M1b is a separate, synthetic evaluation process. It deliberately cannot
// start a participant session, read the private FAA audit, or write to SQLite.
if (argv.Contains("--m1b-challenge-check") || argv.Contains("--m1b-challenge-run"))
{
    var check = argv.Contains("--m1b-challenge-check");
    var run = argv.Contains("--m1b-challenge-run");
    var approved = new HashSet<string>(StringComparer.Ordinal)
    {
        "--m1b-challenge-check", "--m1b-challenge-run",
        "--challenge-corpus", "--challenge-limit", "--challenge-budget",
        "--moderator-model", "--ollama-url", "--moderator-timeout", "--out"
    };
    if (check == run || argv.Where(x => x.StartsWith("--", StringComparison.Ordinal))
        .Any(x => !approved.Contains(x)))
    {
        System.Console.Error.WriteLine(
            "M1b lab cannot be combined with conversation, session, or enforcement flags.");
        return 2;
    }

    try
    {
        var corpusPath = Arg("--challenge-corpus") ??
            Path.Combine("docs", "moderator", "corpus", "m1b-challenges-v1.json");
        var corpusText = File.ReadAllText(corpusPath);
        var corpus = M1bChallengeLab.Load(corpusText);

        if (check)
        {
            System.Console.WriteLine("VALID: " + corpus.Version + " with " +
                corpus.Cases.Length + " synthetic cases.");
            System.Console.WriteLine("Labels: " + corpus.LabelStatus);
            foreach (var group in corpus.Cases.GroupBy(x => x.Expected))
                System.Console.WriteLine("  " + group.Key + ": " + group.Count());
            System.Console.WriteLine(
                "Validation only: no Ollama, participant session, or database.");
            return 0;
        }

        static int Bounded(string? value, int defaultValue, int maximum,
            string name)
        {
            if (value is null) return defaultValue;
            if (!int.TryParse(value, out var count) ||
                count < 1 || count > maximum)
                throw new ArgumentException(name + " must be 1.." + maximum);
            return count;
        }

        var limit = Bounded(Arg("--challenge-limit"), 36, 36, "--challenge-limit");
        var budget = Bounded(Arg("--challenge-budget"), 72, 72, "--challenge-budget");
        var timeout = Bounded(Arg("--moderator-timeout"), 180, 3600, "--moderator-timeout");
        var outPath = Arg("--out");
        if (string.IsNullOrWhiteSpace(outPath))
            throw new ArgumentException(
                "--out is required; use a PRIVATE folder outside the repository.");
        var outputDir = Path.GetFullPath(outPath);
        var repoRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (outputDir.Equals(repoRoot, StringComparison.OrdinalIgnoreCase) ||
            outputDir.StartsWith(repoRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "M1b private results must be stored OUTSIDE the repository.");

        var endpoint = Arg("--ollama-url") is { } address
            ? new Uri(address) : null;
        if (!OllamaAdapter.IsAvailable(endpoint))
        {
            System.Console.Error.WriteLine("Local Ollama is unreachable.");
            return 2;
        }
        var model = Arg("--moderator-model") ?? "qwen3.5:9b";
        using var moderator = new OllamaAdapter(
            model, endpoint, temperature: 0.1,
            timeout: TimeSpan.FromSeconds(timeout), think: false);
        if (moderator.ModelDigest == "unknown")
            throw new InvalidOperationException(
                "Moderator model digest unavailable; refusing unpinned evaluation.");

        System.Console.WriteLine("M1b synthetic lab: " + limit + " cases, at most " +
            budget + " moderator calls. Model " + moderator.ModelName);
        var result = M1bChallengeLab.Run(
            corpus, corpusText, moderator, maxCases: limit, modelBudget: budget);

        Directory.CreateDirectory(outputDir);
        var reportPath = Path.Combine(outputDir, "m1b-challenges-" +
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
            Guid.NewGuid().ToString("N")[..8] + ".json");
        using (var stream = new FileStream(
            reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, result,
                new JsonSerializerOptions { WriteIndented = true });

        var s = result.Summary;
        System.Console.WriteLine("Valid model reviews: " + s.ValidModelResponses +
            "; invalid: " + s.InvalidModelResponses +
            "; transport failures: " + s.TransportFailures);
        System.Console.WriteLine("True positives: " + s.TruePositive +
            "; false positives: " + s.FalsePositive +
            "; false negatives: " + s.FalseNegative +
            "; true negatives: " + s.TrueNegative +
            "; unscorable: " + s.Unscorable);
        System.Console.WriteLine("Abstentions: " + s.Abstentions +
            "; incoherent verdicts: " + s.InconsistentJudgments);
        System.Console.WriteLine("Deterministic exact copies: " +
            s.DeterministicCases + "; Ollama calls: " + s.ModelCalls);
        System.Console.WriteLine("Private case-by-case report: " + reportPath);
        System.Console.WriteLine(
            "Provisional labels only. No enforcement or automatic promotion.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
        InvalidDataException or JsonException or ArgumentException or
        InvalidOperationException)
    {
        System.Console.Error.WriteLine("M1b evaluation failed: " + ex.Message);
        return 2;
    }
}

// Read-only evaluation of the private, historical FAA shadow audit.
if (argv.Contains("--evaluate-shadow-audit"))
{
    var auditPath = Arg("--evaluate-shadow-audit");
    if (string.IsNullOrWhiteSpace(auditPath) ||
        argv.Any(x => x is "--model" or "--discuss-document" or "--inspect-document" or
            "--shadow-moderator" or "--runs" or "--continuing"))
    {
        System.Console.Error.WriteLine(
            "Usage: --evaluate-shadow-audit <private shadow.json> [--corpus <labels.json>]");
        return 2;
    }
    var labelsPath = Arg("--corpus") ??
        Path.Combine("docs", "moderator", "corpus", "faa-shadow-v1.labels.json");
    try
    {
        var metrics = ShadowCorpusEvaluator.Evaluate(
            File.ReadAllText(auditPath), File.ReadAllText(labelsPath));
        System.Console.WriteLine(JsonSerializer.Serialize(metrics,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
        JsonException or InvalidDataException or ArgumentException)
    {
        System.Console.Error.WriteLine($"Corpus evaluation failed: {ex.Message}");
        return 2;
    }
}

// Inspection proves local evidence capture without sending document text to Ollama
// or changing any existing conversation or memory behavior.
if (argv.Contains("--inspect-document"))
{
    if (string.IsNullOrWhiteSpace(Arg("--inspect-document")) ||
        argv.Any(x => x is "--model" or "--continuing" or "--fresh-bridge" or
            "--c2-attribution" or "--runs" or "--db" or "--out" or "--discuss-document"))
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
var shadowMode = argv.Contains("--shadow-moderator");

int PositiveOption(string name, int fallback, int maximum)
{
    var raw = Arg(name);
    if (raw is null) return fallback;
    if (!int.TryParse(raw, out var parsed) || parsed is < 1 || parsed > maximum)
        throw new ArgumentException($"{name} must be an integer from 1 to {maximum}.");
    return parsed;
}

var maxOpportunities = PositiveOption("--max-opportunities", 18, 1000);
var maxModelCalls = PositiveOption("--max-model-calls", 36, 2000);
var endAfterPasses = PositiveOption("--end-after-passes", 2, 100);
var moderatorBudget = PositiveOption("--moderator-budget", 24, 1000);
var moderatorSampleEvery = PositiveOption("--moderator-sample-every", 3, 1000);
var moderatorTimeout = PositiveOption("--moderator-timeout", 180, 3600);

if (!shadowMode && argv.Any(arg => arg.StartsWith("--moderator-", StringComparison.Ordinal)) ||
    shadowMode && modelKind != "ollama")
{
    System.Console.Error.WriteLine(
        "Shadow moderation requires --model ollama --shadow-moderator. " +
        "Do not supply moderator options without --shadow-moderator.");
    return 2;
}

var contextVersion = discussDocument
    ? EvidenceBriefingBuilder.ContextVersion
    : Laboratory.GetContextVersion(c2Attribution);

if (discussDocument &&
    (string.IsNullOrWhiteSpace(Arg("--discuss-document")) ||
     modelKind != "ollama" || continuing || freshBridge || c2Attribution || runs != 1))
{
    System.Console.Error.WriteLine(
        "--discuss-document requires --model ollama and a single fresh session; " +
        "it cannot be combined with --continuing, --fresh-bridge or --c2-attribution.");
    return 2;
}

EvidenceSnapshot? documentSnapshot = null;
Briefing? documentBriefing = null;
IReadOnlyList<string>? documentPassages = null;
if (discussDocument)
{
    try
    {
        documentSnapshot = new LocalTextDocumentImporter().Import(Arg("--discuss-document")!);
        (documentBriefing, documentPassages) = EvidenceBriefingBuilder.Build(documentSnapshot);
        System.Console.WriteLine(
            $"Document '{documentSnapshot.Descriptor.Title}': showing " +
            $"{documentPassages.Count} of {documentSnapshot.Passages.Count} passages. " +
            "No on-demand retrieval (RAG) in R1b.");
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
        MaxOpportunities: maxOpportunities,
        MaxModelCalls: maxModelCalls,
        MaxAttemptsPerOpportunity: 2,
        Coordinator: new CoordinatorOptions(EndAfterConsecutivePasses: endAfterPasses))
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

OllamaAdapter? moderatorAdapter = null;
ModerationSessionPolicy moderatorPolicy = ModerationSessionPolicy.Disabled;
if (shadowMode)
{
    var moderatorModelName = Arg("--moderator-model") ?? Arg("--ollama-model") ?? "qwen3.5:9b";
    var endpoint = Arg("--ollama-url") is { } moderatorUrl ? new Uri(moderatorUrl) : null;
    moderatorAdapter = new OllamaAdapter(
        moderatorModelName, endpoint, temperature: 0.1,
        timeout: TimeSpan.FromSeconds(moderatorTimeout), think: false);
    moderatorPolicy = new ModerationSessionPolicy(
        ModerationSessionPolicy.CurrentContractVersion,
        ModerationOperatingMode.Shadow, ShadowModerationWorker.CurrentRuleVersion,
        ShadowModerationWorker.CurrentPromptVersion, moderatorAdapter.ModelName, moderatorAdapter.ModelDigest,
        0.1, false, moderatorBudget, 0, 0, 0, moderatorSampleEvery);
    moderatorPolicy.Validate();
    if (moderatorAdapter.ModelDigest == "unknown")
    {
        System.Console.Error.WriteLine(
            $"Moderator model '{moderatorModelName}' was not found with a digest in Ollama. " +
            "Check 'ollama list' before pinning a shadow session.");
        moderatorAdapter.Dispose();
        ollamaAdapter?.Dispose();
        return 2;
    }
    System.Console.WriteLine(
        $"[Moderator] SHADOW ONLY: {moderatorAdapter.ModelName}, " +
        $"budget {moderatorBudget} calls/session, periodic sample every " +
        $"{moderatorSampleEvery} committed utterances.");
}

using var store = ConversationStore.OpenFile(dbPath);
store.InitializeSchema();

// C1b: --fresh-bridge starts independent FreshEncounter bridge sessions with no
// recollection context. The established --continuing pair and default factory runs
// retain their original behavior.
var recollectionService = new RecollectionService(store);
if (documentSnapshot is not null)
    store.SaveEvidenceSnapshot(documentSnapshot);
var plan = discussDocument
    ? new[] { (Mode: SessionMode.FreshEncounter, Label: "Document discussion", Briefing: documentBriefing!) }
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
    var session = Laboratory.Seed(
        store, mode, modelConfigVersion, briefing, contextVersion, moderatorPolicy);
    if (documentSnapshot is not null)
        store.PinSessionEvidence(session, documentSnapshot.SnapshotId, documentPassages!);
    ShadowModerationWorker? shadowWorker = null;
    if (moderatorAdapter is not null)
    {
        shadowWorker = new ShadowModerationWorker(
            dbPath, session, moderatorPolicy, moderatorAdapter,
            Laboratory.Cast, briefing,
            store.GetSessionEvidence(session)?.ExposedPassageIds,
            sampleEvery: moderatorSampleEvery,
            queueCapacity: 8,
            log: line => System.Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] {line}"));
    }

    RunResult result;
    try
    {
        result = new ConversationEngine(
            store, client, Laboratory.BuildContextBuilder(c2Attribution), session,
            Laboratory.Cast, briefing, engineOptions, trace: trace,
            recollections: mode == SessionMode.Continuing ? recollectionService : null,
            shadowSink: shadowWorker).Run();
    }
    finally
    {
        // Wait only AFTER authoritative conversation processing stops.
        // Never wait for a shadow review between two participant turns.
        shadowWorker?.DrainAsync().GetAwaiter().GetResult();
    }

    if (shadowWorker is not null)
    {
        var reviews = store.GetShadowReviews(session);
        var shadowReportPath = Path.Combine(outDir, $"shadow-{session}.json");
        File.WriteAllText(shadowReportPath, JsonSerializer.Serialize(new
        {
            session = session.ToString(),
            mode = "Shadow",
            moderatorPolicy,
            summary = new
            {
                selected = shadowWorker.Selected,
                notSelected = shadowWorker.NotSelected,
                dropped = shadowWorker.Dropped,
                audited = shadowWorker.Written,
                workerErrors = shadowWorker.Errors,
                deterministicExactCopies = reviews.Count(r =>
                    r.RequestId is null &&
                    r.Judgment?.Finding?.Kind == ModerationIssueKind.ExactDuplicate),
                modelValidReviews = reviews.Count(r =>
                    r.Attempts is { Count: > 0 } &&
                    r.Status == ShadowReviewStatus.Reviewed),
                modelInvalidReviews = reviews.Count(r =>
                    r.Attempts is { Count: > 0 } &&
                    r.Status == ShadowReviewStatus.InvalidResponse),
                chargedModeratorAttempts = store.GetModeratorReservations(session).Count
            },
            reviews,
            reservations = store.GetModeratorReservations(session)
        }, new JsonSerializerOptions { WriteIndented = true }));
        System.Console.WriteLine(
            $"[Moderator] Shadow complete: {shadowWorker.Written} audits, " +
            $"{shadowWorker.NotSelected} not selected, {shadowWorker.Dropped} dropped, " +
            $"{shadowWorker.Errors} errors. Private audit: {shadowReportPath}");
    }

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

moderatorAdapter?.Dispose();
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

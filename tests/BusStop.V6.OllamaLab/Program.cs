using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BusStop.Engine.Evaluation;
using BusStop.Models;
using BusStop.Moderation.Evaluation;

// This executable is MANUAL only. It is NOT added to BusStop's startup,
// conversation flow, background services, default tests or V5 worker.
return Run(args);

static int Run(string[] args)
{
    const string usage = """
        V6b isolated LOCAL Ollama synthetic evaluation laboratory
        Usage:
          dotnet run --project tests/BusStop.V6.OllamaLab -- \
            --model qwen3.5:9b \
            --corpus docs/moderator/corpus/v6a-synthetic-provisional-v1.json \
            --private-dir data/v6b-private \
            --safe-dir data/v6b-safe \
            --runs 2 --cases 4

        --runs: 1..3 distinct same-corpus runs (default 1)
        --cases: 1..20 first development cases (default 20)
        No --endpoint option: ONLY the loopback Ollama service is supported.
        Private audits contain raw model output and prompts. NEVER commit them.
        Safe reports contain aggregate provisional counts, not ground truth.
        """;

    if (args.Length == 1 && args[0] is "--help" or "-h")
    {
        Console.WriteLine(usage);
        return 0;
    }

    try
    {
        if (args.Length == 0 || args.Length % 2 != 0)
            throw new ArgumentException("Required named options are missing.");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "--model", "--corpus", "--private-dir", "--safe-dir", "--runs", "--cases"
        };
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i]) || !opts.TryAdd(args[i], args[i + 1]) ||
                string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException("Unknown, repeated or empty option: " + args[i]);
        }

        string Required(string key) => opts.TryGetValue(key, out var value)
            ? value : throw new ArgumentException("Missing required option " + key);

        var name = Required("--model");
        var corpusPath = Path.GetFullPath(Required("--corpus"));
        var privateDirectory = Path.GetFullPath(Required("--private-dir"));
        var safeDirectory = Path.GetFullPath(Required("--safe-dir"));
        var comparisons = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (privateDirectory.Equals(safeDirectory, comparisons) ||
            privateDirectory.StartsWith(safeDirectory + Path.DirectorySeparatorChar, comparisons) ||
            safeDirectory.StartsWith(privateDirectory + Path.DirectorySeparatorChar, comparisons))
            throw new InvalidDataException(
                "Private and safe export folders must be disjoint.");

        static int Bounded(Dictionary<string, string> options, string key, int fallback,
            int limit)
        {
            if (!options.TryGetValue(key, out var raw)) return fallback;
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var value) || value < 1 || value > limit)
                throw new ArgumentException(key + " must be between 1 and " + limit);
            return value;
        }
        var runs = Bounded(opts, "--runs", 1, 3);
        var cases = Bounded(opts, "--cases", 20, V6bLabContract.MaxCasesPerRun);
        var corpus = V6CorpusReader.LoadProvisionalSynthetic(
            File.ReadAllText(corpusPath));

        // Ensure both destinations exist and are writable BEFORE any model call.
        Directory.CreateDirectory(privateDirectory);
        Directory.CreateDirectory(safeDirectory);
        if (!OllamaAdapter.IsAvailable(new Uri("http://127.0.0.1:11434")))
            throw new InvalidOperationException(
                "Local Ollama is unavailable at loopback 127.0.0.1:11434.");

        using var model = new OllamaAdapter(
            name, endpoint: new Uri("http://127.0.0.1:11434"),
            temperature: 0.1, think: false, timeout: TimeSpan.FromMinutes(4));
        if (model.ModelDigest == "unknown" ||
            string.IsNullOrWhiteSpace(model.ModelDigest))
            throw new InvalidOperationException(
                "Ollama model digest unavailable: refusing an unpinned run.");

        var lab = new V6bLocalModelLab(model);
        var completedRuns = new List<V6bLabRun>();
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        for (int i = 0; i < runs; i++)
        {
            var runId = "v6b-" + DateTimeOffset.UtcNow.ToString(
                "yyyyMMddHHmmss", CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N");
            var run = lab.Run(corpus, runId, cases);
            // PRIVATE and SAFE files are deliberately distinct. CreateNew
            // refuses accidental overwrite; only safe aggregates are shareable.
            WriteNew(Path.Combine(privateDirectory, runId + ".private.json"),
                JsonSerializer.Serialize(run.PrivateAudit, jsonOptions));
            WriteNew(Path.Combine(safeDirectory, runId + ".safe.json"),
                JsonSerializer.Serialize(V6bSafeExporter.From(run), jsonOptions));
            completedRuns.Add(run);

            var overall = run.Score.Overall;
            Console.WriteLine(
                "V6b run " + (i + 1) + "/" + runs +
                " model=" + model.ModelName +
                " digest=" + model.ModelDigest +
                " corpusSha=" + corpus.Sha256 +
                " selected=" + overall.Selected +
                " completed=" + overall.Completed +
                " invalid=" + overall.InvalidResponses +
                " transport=" + overall.TransportFailures +
                " provisionalFalsePositives=" + overall.ConfirmedFalsePositives +
                " chargedLocalCalls=" + overall.ChargedModelCalls);
        }

        for (int i = 1; i < completedRuns.Count; i++)
        {
            var drift = V6bSafeExporter.Compare(
                completedRuns[i - 1], completedRuns[i]);
            var namePart = drift.FirstRunId + "-versus-" +
                drift.SecondRunId + ".drift.safe.json";
            WriteNew(Path.Combine(safeDirectory, namePart),
                JsonSerializer.Serialize(drift, jsonOptions));
            Console.WriteLine("V6b same-case drift: comparable=" +
                drift.ComparableCompletedCases + " flips=" +
                drift.OutcomeFlipCount + " statusChanges=" +
                drift.SelectionOrOperationalChangeCount);
        }

        Console.WriteLine("V6b LOCAL PROBE FINISHED; private audits: " +
            privateDirectory);
        Console.WriteLine("Safe aggregate development reports: " +
            safeDirectory);
        Console.WriteLine(
            "PROVISIONAL: technical pass and synthetic-label agreement are NOT semantic acceptance.");
        return 0;
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        Console.Error.WriteLine(
            "V6b FAILED (" + ex.GetType().Name + "): " + ex.Message);
        Console.Error.WriteLine(usage);
        return 1;
    }
}

static void WriteNew(string path, string content)
{
    using var file = new FileStream(path, FileMode.CreateNew,
        FileAccess.Write, FileShare.None);
    using var writer = new StreamWriter(file);
    writer.Write(content);
}

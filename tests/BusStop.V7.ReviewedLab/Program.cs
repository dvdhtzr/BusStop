using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BusStop.Engine.Evaluation;
using BusStop.Models;
using BusStop.Moderation.Evaluation;

// MANUAL ONLY. Never attached to the conversation engine, V6c or default tests.
return Run(args);

static int Run(string[] args)
{
    const string usage = """
        V7b LOCAL PRIVATE owner-reviewed Ollama evaluation (NO release authorization)

        Required:
          --development <private-development.json>
          --holdout <private-holdout.json>
          --criteria <private-criteria.json>
          --approval <private-approval.json>
          --model <exact-pinned-ollama-name>
          --partition Development|Holdout
          --private-dir <private-result-folder>
          --safe-dir <aggregate-only-folder>

        Optional: --runs 1..3  --cases 1..128
        For a Holdout run ONLY add:
          --unlock-holdout i-authorize-first-holdout-evaluation

        All reviewed examples remain PRIVATE. Holdout requires explicit unlock
        even AFTER approval hash validation. No cloud endpoint, retry, auto-
        promotion, participant writing, or V5 production session inference.
        """;

    if (args.Length == 1 && args[0] is "--help" or "-h")
    {
        Console.WriteLine(usage);
        return 0;
    }

    try
    {
        if (args.Length == 0 || args.Length % 2 != 0)
            throw new ArgumentException("V7b requires named paired arguments.");
        var permitted = new HashSet<string>(StringComparer.Ordinal)
        {
            "--development", "--holdout", "--criteria", "--approval",
            "--model", "--partition", "--private-dir", "--safe-dir",
            "--runs", "--cases", "--unlock-holdout"
        };
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!permitted.Contains(args[i]) || !opts.TryAdd(args[i], args[i + 1]) ||
                string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException("Unknown, repeated or empty V7b option.");
        }
        string Required(string key) =>
            opts.TryGetValue(key, out var value)
                ? value : throw new ArgumentException("Missing required option " + key);

        var partition = Required("--partition") switch
        {
            "Development" => V7Partition.Development,
            "Holdout" => V7Partition.Holdout,
            _ => throw new ArgumentException("Partition must be Development or Holdout.")
        };
        var unlocked = opts.TryGetValue("--unlock-holdout", out var unlock);
        if (partition == V7Partition.Holdout &&
            (!unlocked || unlock != "i-authorize-first-holdout-evaluation"))
            throw new InvalidDataException(
                "Holdout run requires explicit owner unlock; do not use for routine tuning.");
        if (partition == V7Partition.Development && unlocked)
            throw new InvalidDataException(
                "Holdout unlock is forbidden on a development-only run.");

        static int Positive(Dictionary<string, string> args, string name,
            int fallback, int max)
        {
            if (!args.TryGetValue(name, out var value)) return fallback;
            if (!int.TryParse(value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var number) ||
                number < 1 || number > max)
                throw new ArgumentException(name + " must be 1.." + max);
            return number;
        }

        var runs = Positive(opts, "--runs", 1, 3);
        var cases = Positive(opts, "--cases", 8, V7bLabContract.MaxCasesPerRun);
        var privateDir = Path.GetFullPath(Required("--private-dir"));
        var safeDir = Path.GetFullPath(Required("--safe-dir"));
        var pathCompare = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (privateDir.Equals(safeDir, pathCompare) ||
            privateDir.StartsWith(safeDir + Path.DirectorySeparatorChar,
                pathCompare) ||
            safeDir.StartsWith(privateDir + Path.DirectorySeparatorChar,
                pathCompare))
            throw new InvalidDataException(
                "Private and aggregate output directories must be disjoint.");

        static string ReadStrict(string path)
        {
            var content = File.ReadAllBytes(Path.GetFullPath(path));
            if (content.Length is < 2 or > V7FreezeContract.MaxDocumentBytes ||
                content.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
                throw new InvalidDataException(
                    "V7b requires bounded, BOM-free exact UTF-8 review inputs.");
            return new UTF8Encoding(false, true).GetString(content);
        }

        // This is the ONLY path from private source data into the lab.
        // Neither side can run unless V7a validates the exact-byte hash
        // commitment, owner approval and all per-lane requirements.
        var approved = V7ReviewFreezeValidator.OpenApproved(
            ReadStrict(Required("--development")),
            ReadStrict(Required("--holdout")),
            ReadStrict(Required("--criteria")),
            ReadStrict(Required("--approval")));
        var modelName = Required("--model");
        if (modelName != approved.Criteria.ModelName)
            throw new InvalidDataException(
                "Model name differs from frozen owner criteria.");

        var local = new Uri("http://127.0.0.1:11434");
        if (!OllamaAdapter.IsAvailable(local))
            throw new InvalidOperationException(
                "Local loopback Ollama is unavailable. No remote fallback.");
        using var model = new OllamaAdapter(
            modelName, endpoint: local, temperature: 0.1,
            think: false, timeout: TimeSpan.FromMinutes(4));
        if (model.ModelDigest != approved.Criteria.ModelDigest)
            throw new InvalidDataException(
                "The installed local model digest does not match frozen criteria.");

        Directory.CreateDirectory(privateDir);
        Directory.CreateDirectory(safeDir);
        var json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var lab = new V7bReviewedModelLab(model);
        var previous = (V7bLabRun?)null;
        for (int i = 0; i < runs; i++)
        {
            // The tag may have changed since the initial Ollama construction.
            using (var check = new OllamaAdapter(
                modelName, endpoint: local,
                temperature: 0.1, think: false))
            {
                if (check.ModelDigest != approved.Criteria.ModelDigest)
                    throw new InvalidDataException(
                        "Model tag/digest changed between frozen V7b runs.");
            }

            var runId = "v7b-" + DateTimeOffset.UtcNow.ToString(
                "yyyyMMddHHmmss", CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N");
            var result = lab.Run(approved, partition, runId, cases);
            var safe = V7bSafeExporter.From(result, approved);

            WriteNew(Path.Combine(privateDir, runId + ".private.json"),
                JsonSerializer.Serialize(result.PrivateAudit, json));
            WriteNew(Path.Combine(privateDir, runId + ".score.private.json"),
                JsonSerializer.Serialize(result.Score, json));
            WriteNew(Path.Combine(safeDir, runId + ".aggregate.safe.json"),
                JsonSerializer.Serialize(safe, json));

            Console.WriteLine($"V7b {partition} run {i + 1}/{runs}: " +
                $"reviewed={safe.Overall.IndependentCases} " +
                $"observed={safe.Overall.ObservedCases} " +
                $"v3-span-hits={safe.Overall.ExactSpanHits} " +
                $"oracle-completed={safe.Overall.OracleCompleted} " +
                $"calls={safe.ModelCalls} " +
                $"sha={safe.CorpusSha256} " +
                "ACCEPTANCE=NOT_GRANTED");

            if (previous is not null)
            {
                var drift = V7bSafeExporter.Drift(previous, result);
                WriteNew(Path.Combine(safeDir,
                        previous.Score.RunId + "-versus-" + runId +
                        ".drift.safe.json"),
                    JsonSerializer.Serialize(drift, json));
                Console.WriteLine(
                    $"Same-case drift: comparable={drift.ComparableCompletedOracleCases}, " +
                    $"flipped={drift.ChangedOracleOutcomeCount}; " +
                    "repeated cases are NOT independent test cases.");
            }
            previous = result;
        }
        Console.WriteLine(
            "V7b complete. Private per-case model evidence remains LOCAL; " +
            "aggregate-only reports require owner review. No V7 acceptance or enforcement.");
        return 0;
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        // Do not echo paths, private content, raw API response or exception
        // messages: IO exceptions may embed the private holdout pathname.
        Console.Error.WriteLine("V7b refused or failed: " +
            ex.GetType().Name +
            ". Verify the private freeze, local Ollama digest and output folders.");
        return 1;
    }
}

static void WriteNew(string path, string contents)
{
    using var file = new FileStream(path, FileMode.CreateNew,
        FileAccess.Write, FileShare.None);
    using var writer = new StreamWriter(file);
    writer.Write(contents);
}

using System.Text.Json;
using System.Text.Json.Serialization;
using BusStop.Domain;
using BusStop.Engine.Evaluation;
using BusStop.Persistence;

// V7d-1..3: explicit offline invocation ONLY. No Ollama, DB migrations,
// participant prompts, coordinator, moderator or inference. Analysis prints
// safe aggregates to stdout. A PRIVATE worksheet is created only when an
// owner explicitly supplies an unused --output path.
return Run(args);

static int Run(string[] args)
{
    const string usage = """
        BusStop V7d PRIVATE conversational progress laboratory

        Required for all modes:
          --db <existing-BusStop-session.sqlite>
          --session <session-guid>
          --coverage <existing-v6c-coverage.private.json>
          --mode analyze|template|calibrate

        For mode template ONLY:
          --output <existing-private-directory/new-private-file.json>

        For mode calibrate ONLY:
          --review <completed-private-V7d-worksheet.json>

        Analyze/calibrate: stdout SAFE aggregates only, no writes.
        Template: create exactly ONE PRIVATE JSON file at the owner path,
        FileMode.CreateNew, no directory creation and no overwrite.
        No local Ollama, enforcement, participant writing or holdout access.
        """;

    if (args.Length == 1 && args[0] is "--help" or "-h")
    {
        Console.WriteLine(usage);
        return 0;
    }

    try
    {
        if (args.Length < 8 || args.Length % 2 != 0)
            throw new ArgumentException("Missing V7d CLI options.");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "--db", "--session", "--coverage", "--mode", "--review", "--output"
        };
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i]) ||
                !options.TryAdd(args[i], args[i + 1]) ||
                string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException("Unknown, repeated or empty V7d option.");
        }

        string Required(string key) => options.TryGetValue(key, out var val)
            ? val : throw new ArgumentException("Missing " + key);

        var mode = Required("--mode");
        if (mode is not ("analyze" or "template" or "calibrate") ||
            mode == "analyze" &&
                (options.ContainsKey("--output") || options.ContainsKey("--review")) ||
            mode == "template" &&
                (!options.ContainsKey("--output") || options.ContainsKey("--review")) ||
            mode == "calibrate" &&
                (!options.ContainsKey("--review") || options.ContainsKey("--output")))
            throw new ArgumentException("Conflicting mode and input/output options.");

        var db = Path.GetFullPath(Required("--db"));
        var coveragePath = Path.GetFullPath(Required("--coverage"));
        if (!Guid.TryParse(Required("--session"), out var sessionGuid) ||
            sessionGuid == Guid.Empty)
            throw new ArgumentException("Invalid session GUID.");
        var session = new SessionId(sessionGuid);
        var json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
        };
        static string ReadBounded(string path, int ceiling)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 2 ||
                info.Length > ceiling)
                throw new InvalidDataException(
                    "Required private file is missing or exceeds the size ceiling.");
            return File.ReadAllText(path);
        }

        var coverage = JsonSerializer.Deserialize<V7cCoverageEnvelope>(
            ReadBounded(coveragePath, 500_000), json)
            ?? throw new InvalidDataException("Invalid V6c coverage payload.");

        using var store = ConversationStore.OpenReadOnly(db);
        var v7c = V7cNaturalSessionAuditor.Audit(store, session, coverage);
        if (!v7c.ThreeParticipantSession ||
            !v7c.TranscriptSequenceValid ||
            !v7c.TranscriptMutationCheckAvailable ||
            !v7c.TranscriptUnchangedAcrossVerifierDrain ||
            !v7c.CoverageMatchesCommittedCount ||
            !v7c.CoverageInternallyConsistent ||
            !v7c.LedgerMatchesCoverage)
            throw new InvalidDataException(
                "V7d refuses a session failing original V7c integrity checks.");

        var turns = v7c.Turns.Select(x =>
            new V7dInputTurn(x.Sequence, x.SpeakerId, x.Text)).ToArray();
        var analysis = V7dProgressAnalyzer.Analyze(
            turns, v7c.TranscriptSha256);
        var safe = V7dProgressAnalyzer.ExportSafe(analysis, turns);

        switch (mode)
        {
            case "analyze":
                Console.WriteLine(JsonSerializer.Serialize(safe, json));
                return 0;

            case "template":
                var dest = Path.GetFullPath(Required("--output"));
                // Locate the nearest BusStop repository root regardless of
                // whether the owner launched the tool from its root or a child.
                var candidate = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (candidate is not null &&
                    !File.Exists(Path.Combine(candidate.FullName, "BusStop.sln")))
                    candidate = candidate.Parent;
                var repo = candidate?.FullName;
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (repo is not null &&
                    (dest.Equals(repo, comparison) ||
                     dest.StartsWith(repo + Path.DirectorySeparatorChar,
                         comparison)))
                    throw new InvalidDataException(
                        "Private owner worksheets cannot be written to the BusStop repository.");
                if (dest.Equals(db, comparison) ||
                    dest.Equals(coveragePath, comparison) ||
                    !Directory.Exists(Path.GetDirectoryName(dest)) ||
                    Path.GetExtension(dest) != ".json")
                    throw new InvalidDataException(
                        "V7d template needs a new JSON path in an EXISTING private folder.");
                var template = V7dProgressCalibration.CreateBlank(analysis);
                using (var file = new FileStream(dest, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(file))
                    writer.Write(JsonSerializer.Serialize(template, json));
                Console.WriteLine(
                    "Created one owner-requested PRIVATE review template. " +
                    "No labels were filled and no results were accepted.");
                return 0;

            case "calibrate":
                var reviewed = JsonSerializer.Deserialize<V7dProgressReviewWorksheet>(
                    ReadBounded(Path.GetFullPath(Required("--review")),
                        5_000_000), json)
                    ?? throw new InvalidDataException("Invalid private V7d review.");
                var metrics = V7dProgressCalibration.CalibrateDevelopment(
                    analysis, reviewed);
                Console.WriteLine(JsonSerializer.Serialize(metrics, json));
                return 0;
        }
        throw new InvalidDataException("Unsupported mode.");
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        // Never print private evidence, raw exceptions, or file paths.
        Console.Error.WriteLine(
            "V7d offline evaluation failed (" + error.GetType().Name + "). " +
            "No semantic acceptance or behavioral change authorized.");
        return 2;
    }
}

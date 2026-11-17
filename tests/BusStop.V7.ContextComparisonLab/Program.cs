using System.Text.Json;
using System.Text.Json.Serialization;
using BusStop.Domain;
using BusStop.Engine.Evaluation;
using BusStop.Persistence;

// MANUAL OFFLINE ONLY. No Ollama, session mutations or generated run folders.
// Reuses the original V7c SQLite and immutable-ledger audit.
return Run(args);

static int Run(string[] args)
{
    const string usage = """
        V7d-4a BusStop A/B/C context-selection comparison (model-free)

        Required:
          --db <existing-v7c-session.sqlite>
          --session <session-guid>
          --coverage <existing-v6c-coverage.private.json>

        Optional:
          --private-report <new-file-in-existing-private-folder.private.json>

        Default: stdout SAFE aggregate only, no files created.
        Private report: creates exactly one explicitly requested file (never
        overwrites or creates a directory). It contains per-turn sequence
        IDs and hashed selections but no original utterance text.
        No Ollama, no prompts sent, no new experiment, no changed dialogue.
        """;

    if (args.Length == 1 && args[0] is "--help" or "-h")
    {
        Console.WriteLine(usage);
        return 0;
    }

    try
    {
        if (args.Length < 6 || args.Length % 2 != 0)
            throw new ArgumentException("Missing V7d-4a CLI options.");

        var permitted = new HashSet<string>(StringComparer.Ordinal)
        {
            "--db", "--session", "--coverage", "--private-report"
        };
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!permitted.Contains(args[i]) ||
                !opts.TryAdd(args[i], args[i + 1]) ||
                string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException("Unknown, duplicate or empty option.");
        }

        string Required(string key) => opts.TryGetValue(key, out var value)
            ? value : throw new ArgumentException("Missing option " + key);

        var db = Path.GetFullPath(Required("--db"));
        var coveragePath = Path.GetFullPath(Required("--coverage"));
        if (!Guid.TryParse(Required("--session"), out var id) || id == Guid.Empty)
            throw new ArgumentException("Invalid BusStop session.");
        var session = new SessionId(id);

        static string ReadBounded(string path, int maxBytes)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is < 2 || file.Length > maxBytes)
                throw new InvalidDataException("Private audit input is missing or too large.");
            return File.ReadAllText(path);
        }

        var json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
        };
        var envelope = JsonSerializer.Deserialize<V7cCoverageEnvelope>(
            ReadBounded(coveragePath, 500_000), json)
            ?? throw new InvalidDataException("Invalid V7c coverage data.");

        using var store = ConversationStore.OpenReadOnly(db);
        var audit = V7cNaturalSessionAuditor.Audit(store, session, envelope);
        if (!audit.ThreeParticipantSession || !audit.TranscriptSequenceValid ||
            !audit.CoverageInternallyConsistent || !audit.CoverageMatchesCommittedCount ||
            !audit.LedgerMatchesCoverage ||
            !audit.TranscriptMutationCheckAvailable ||
            !audit.TranscriptUnchangedAcrossVerifierDrain)
            throw new InvalidDataException("V7c mechanical integrity gate failed.");

        var committed = store.GetCommittedUtterances(session);
        if (committed.Count != audit.Turns.Count ||
            committed.Where((x, i) =>
                audit.Turns[i].Sequence != x.Sequence ||
                audit.Turns[i].SpeakerId != x.Speaker.ToString() ||
                audit.Turns[i].Text != x.Text).Any() ||
            V7cNaturalSessionAuditor.HashTranscript(committed) !=
                audit.TranscriptSha256)
            throw new InvalidDataException(
                "V7d-4a committed transcript does not match audited speech.");

        var result = V7d4aOfflineComparison.Compare(
            committed, audit.TranscriptSha256);
        var safe = V7d4aOfflineComparison.ExportSafe(result);

        if (opts.TryGetValue("--private-report", out var privatePath))
        {
            var full = Path.GetFullPath(privatePath);
            var root = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (root is not null &&
                   !File.Exists(Path.Combine(root.FullName, "BusStop.sln")))
                root = root.Parent;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (root is null ||
                full.Equals(root.FullName, comparison) ||
                full.StartsWith(root.FullName + Path.DirectorySeparatorChar,
                    comparison) ||
                !full.EndsWith(".private.json", comparison) ||
                !Directory.Exists(Path.GetDirectoryName(full)) ||
                full.Equals(db, comparison) ||
                full.Equals(coveragePath, comparison))
                throw new InvalidDataException(
                    "Private report requires a new file outside the BusStop repo, " +
                    "in an existing private folder.");
            using (var file = new FileStream(full, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file))
                writer.Write(JsonSerializer.Serialize(result, json));
        }

        Console.WriteLine(JsonSerializer.Serialize(safe, json));
        return 0;
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        // Never print private text, source paths, or raw exception details.
        Console.Error.WriteLine(
            "V7d-4a comparison failed (" + ex.GetType().Name + "). " +
            "No dialogue modified or behavior authorized.");
        return 2;
    }
}

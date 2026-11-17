using System.Text.Json;
using System.Text.Json.Serialization;
using BusStop.Domain;
using BusStop.Engine.Evaluation;
using BusStop.Persistence;

// Explicit owner invocation ONLY. No Ollama, new session, automatic recovery,
// web access, participant prompt, or production SQLite write is permitted.
return Run(args);

static int Run(string[] args)
{
    const string usage = """
        BusStop V7c PRIVATE natural conversation / V5 audit

        Required:
          --db <existing-local-sqlite.db>
          --session <session-guid>
          --coverage <v6c-coverage-SESSION.private.json>
          --private-dir <OUTSIDE-repo-private-folder>
          --safe-dir <OUTSIDE-repo-aggregate-folder>

        Optional:
          --review <completed-private-human-review.json>

        Run after a fresh opt-in three-person conversation with
        --verifier-shadow (and optionally --shadow-moderator).
        Uses SQLite read-only mode; no Ollama, participant or V5c action.
        A blank private review worksheet is written for manual annotation.
        No semantic acceptance or enforcement is possible.
        """;

    if (args.Length == 1 && args[0] is "--help" or "-h")
    {
        Console.WriteLine(usage);
        return 0;
    }

    try
    {
        if (args.Length == 0 || args.Length % 2 != 0)
            throw new ArgumentException("Required V7c options are missing.");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "--db", "--session", "--coverage",
            "--private-dir", "--safe-dir", "--review"
        };
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i]) ||
                !opts.TryAdd(args[i], args[i + 1]) ||
                string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException("Invalid, repeated or empty V7c option.");
        }
        string Required(string name) => opts.TryGetValue(name, out var value)
            ? value : throw new ArgumentException("Missing " + name);

        var db = Path.GetFullPath(Required("--db"));
        var coverage = Path.GetFullPath(Required("--coverage"));
        var privateDir = Path.GetFullPath(Required("--private-dir"));
        var safeDir = Path.GetFullPath(Required("--safe-dir"));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!Guid.TryParse(Required("--session"), out var rawSession) ||
            rawSession == Guid.Empty)
            throw new ArgumentException("Invalid session ID.");
        var session = new SessionId(rawSession);

        bool Within(string item, string directory) =>
            item.Equals(directory, comparison) ||
            item.StartsWith(directory + Path.DirectorySeparatorChar, comparison);

        if (Within(privateDir, safeDir) || Within(safeDir, privateDir))
            throw new InvalidDataException(
                "V7c private and safe output directories must be disjoint.");
        var repo = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (File.Exists(Path.Combine(repo, "BusStop.sln")) &&
            (Within(privateDir, repo) || Within(safeDir, repo)))
            throw new InvalidDataException(
                "V7c review and outputs must be OUTSIDE the repository.");

        static string ReadBounded(string file, int limit)
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length < 2 || info.Length > limit)
                throw new InvalidDataException("Private V7c input missing/oversized.");
            return File.ReadAllText(file);
        }

        var json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
        };
        var envelope = JsonSerializer.Deserialize<V7cCoverageEnvelope>(
            ReadBounded(coverage, 500_000), json)
            ?? throw new InvalidDataException("Invalid V6c private coverage report.");

        // No InitializeSchema / no migrations / no writes.
        using var store = ConversationStore.OpenReadOnly(db);
        var audit = V7cNaturalSessionAuditor.Audit(store, session, envelope);
        var safe = V7cNaturalSessionAuditor.ExportSafe(audit);
        var worksheet = V7cHumanReview.CreateBlankWorksheet(audit);

        V7cHumanReviewAggregate? human = null;
        if (opts.TryGetValue("--review", out var reviewFile))
        {
            var submitted = JsonSerializer.Deserialize<V7cHumanReviewWorksheet>(
                ReadBounded(Path.GetFullPath(reviewFile), 5_000_000), json)
                ?? throw new InvalidDataException("Missing human review JSON.");
            human = V7cHumanReview.ValidateCompleted(audit, submitted);
        }

        Directory.CreateDirectory(privateDir);
        Directory.CreateDirectory(safeDir);
        var prefix = "v7c-" + Guid.NewGuid().ToString("N");
        static void WriteNew(string file, string content)
        {
            using var stream = new FileStream(file, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }

        WriteNew(Path.Combine(privateDir, prefix + ".audit.private.json"),
            JsonSerializer.Serialize(audit, json));
        WriteNew(Path.Combine(privateDir, prefix + ".worksheet.private.json"),
            JsonSerializer.Serialize(worksheet, json));
        WriteNew(Path.Combine(safeDir, prefix + ".operational.safe.json"),
            JsonSerializer.Serialize(safe, json));
        if (human is not null)
            WriteNew(Path.Combine(safeDir, prefix + ".human.safe.json"),
                JsonSerializer.Serialize(human, json));

        Console.WriteLine(
            $"V7c real-session observation: committed={safe.CommittedUtterances}; " +
            $"observed={safe.V6cObserved}; selected={safe.V6cSelected}; " +
            $"dropped={safe.V6cDropped}; registered={safe.V6cRegistered}; " +
            $"terminal={safe.TerminalLedgerJobs}; " +
            $"incomplete={safe.IncompleteLedgerSummaries}; " +
            $"charged={safe.ChargedVerificationAttempts}.");
        Console.WriteLine(
            "Three-participants=" + safe.ThreeParticipantSession +
            "; coverage-consistent=" + safe.CoverageInternallyConsistent +
            "; ledger-aligned=" + safe.LedgerMatchesCoverage +
            "; transcript-digest-unchanged=" +
            (safe.TranscriptMutationCheckAvailable
                ? safe.TranscriptUnchangedAcrossVerifierDrain.ToString()
                : "UNMEASURED"));
        Console.WriteLine(
            "Private: audit and unfilled human review worksheet; " +
            "safe: aggregate-only operational report.");
        Console.WriteLine(
            "Human review: " +
            (human?.OwnerAttestedReviewCompleted == true
                ? "self-attested complete (not independently authenticated)"
                : "NOT COMPLETED") +
            ". Semantic acceptance and enforcement: NOT GRANTED.");

        // A successful tool execution is NOT release acceptance. However,
        // mechanical integrity/coverage failures must not print 'GREEN'.
        var mechanicalIntegrity = safe.ThreeParticipantSession &&
            audit.TranscriptSequenceValid &&
            safe.CoverageInternallyConsistent &&
            safe.CoverageMatchesCommittedCount &&
            safe.LedgerMatchesCoverage &&
            safe.TranscriptMutationCheckAvailable &&
            safe.TranscriptUnchangedAcrossVerifierDrain;
        if (!mechanicalIntegrity)
        {
            Console.Error.WriteLine(
                "V7c MECHANICAL INTEGRITY WARNING: inspect private audit; " +
                "readiness is blocked, no data repaired.");
            return 2;
        }
        Console.WriteLine(
            "V7c mechanical audit coherent; qualitative, crash and latency " +
            "release gates remain UNMEASURED or UNAPPROVED.");
        return 0;
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        // Paths and raw content are private. Never echo exception.Message.
        Console.Error.WriteLine("V7c refused: " + ex.GetType().Name +
            " (no participant, SQLite, or verifier writes performed).");
        return 1;
    }
}

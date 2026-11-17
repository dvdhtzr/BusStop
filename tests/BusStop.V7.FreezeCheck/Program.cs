using System.Text;
using System.Text.Json;
using BusStop.Moderation.Evaluation;

// V7a only checks human-supplied private review documents and threshold
// preregistration. No Ollama, sessions, V5 writes, holdout scoring, or network.
return Run(args);

static int Run(string[] args)
{
    const string usage = """
        BusStop V7a LOCAL reviewer-attestation / preregistration check

        --prepare  --development <local.json> --holdout <local.json>
                   --criteria <local.json>
        --validate --development <local.json> --holdout <local.json>
                   --criteria <local.json> --approval <local.json>

        All inputs remain local. Prints hashes and aggregate counts only.
        --prepare does NOT approve a freeze.
        --validate checks an owner-supplied exact-byte approval; it is NOT
        a digital signature and DOES NOT award semantic acceptance.
        No model calls, SQLite mutations, or enforcement are possible.
        """;

    if (args.Length == 1 && args[0] is "--help" or "-h")
    {
        Console.WriteLine(usage);
        return 0;
    }

    try
    {
        if (args.Length < 1 || args[0] is not ("--prepare" or "--validate") ||
            (args.Length - 1) % 2 != 0)
            throw new ArgumentException("Choose --prepare or --validate with named local file inputs.");

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "--development", "--holdout", "--criteria", "--approval"
        };
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i]) || !supplied.TryAdd(args[i], args[i + 1]) ||
                string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException("Invalid, repeated, or empty input argument.");
        }

        var isValidation = args[0] == "--validate";
        if (supplied.Count != (isValidation ? 4 : 3) ||
            isValidation != supplied.ContainsKey("--approval") ||
            !supplied.ContainsKey("--development") ||
            !supplied.ContainsKey("--holdout") ||
            !supplied.ContainsKey("--criteria"))
            throw new ArgumentException("V7a input set does not match chosen mode.");

        // Strict UTF-8, BOM-free, byte-limited. The frozen SHA-256 is of
        // exactly the bytes the human approves; no newline rewriting.
        static string ReadLocal(string path)
        {
            var bytes = File.ReadAllBytes(Path.GetFullPath(path));
            if (bytes.Length is < 2 or > V7FreezeContract.MaxDocumentBytes ||
                bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
                throw new InvalidDataException("V7a requires bounded BOM-free UTF-8 review inputs.");
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        var development = ReadLocal(supplied["--development"]);
        var holdout = ReadLocal(supplied["--holdout"]);
        var criteria = ReadLocal(supplied["--criteria"]);
        var options = new JsonSerializerOptions { WriteIndented = true };

        if (!isValidation)
        {
            var prepared = V7ReviewFreezeValidator.Prepare(
                development, holdout, criteria);
            Console.WriteLine(JsonSerializer.Serialize(prepared, options));
            Console.Error.WriteLine(
                "V7a PREPARED ONLY. An owner must independently review and explicitly " +
                "approve exact hashes before any freeze exists.");
            return 0;
        }

        var approval = ReadLocal(supplied["--approval"]);
        var receipt = V7ReviewFreezeValidator.Validate(
            development, holdout, criteria, approval);
        Console.WriteLine(JsonSerializer.Serialize(receipt, options));
        Console.Error.WriteLine(
            "V7a ATTESTATION VALIDATED. No holdout run or semantic acceptance occurred. " +
            "Enforcement remains forbidden.");
        return 0;
    }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or
        IOException or UnauthorizedAccessException or DecoderFallbackException or
        JsonException or InvalidOperationException)
    {
        // Never print file contents or private paths from arbitrary IO errors.
        var safe = ex is IOException or UnauthorizedAccessException
            ? "Unable to read one or more local private documents."
            : ex is DecoderFallbackException
                ? "Invalid UTF-8 in local private input."
                : ex.Message;
        Console.Error.WriteLine("V7a validation refused (" +
            ex.GetType().Name + "): " + safe);
        return 1;
    }
}

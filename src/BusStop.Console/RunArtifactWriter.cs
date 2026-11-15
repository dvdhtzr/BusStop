using BusStop.Domain;

namespace BusStop.Console;

/// <summary>Writes one uniquely named artifact pair per laboratory session, preserving
/// earlier runs even when multiple batches share an output directory.</summary>
public static class RunArtifactWriter
{
    public static void WriteRun(string directory, int index, SessionId session,
        string transcript, string diagnostics)
    {
        if (index <= 0) throw new ArgumentOutOfRangeException(nameof(index));
        Directory.CreateDirectory(directory);
        var prefix = $"run-{index:00}-{session}";
        var transcriptPath = Path.Combine(directory, $"{prefix}.transcript.md");
        var diagnosticsPath = Path.Combine(directory, $"{prefix}.diagnostics.json");

        // Never overwrite earlier experiment evidence. A failed second write is
        // visible instead of silently destroying an existing artifact.
        File.WriteAllText(transcriptPath, transcript);
        File.WriteAllText(diagnosticsPath, diagnostics);
    }
}

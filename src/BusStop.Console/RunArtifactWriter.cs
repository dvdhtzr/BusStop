namespace BusStop.Console;

/// <summary>Writes a distinct artifact pair for each independent laboratory run.</summary>
public static class RunArtifactWriter
{
    public static void WriteRun(string directory, int index, string transcript, string diagnostics)
    {
        if (index <= 0) throw new ArgumentOutOfRangeException(nameof(index));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"run-{index:00}.transcript.md"), transcript);
        File.WriteAllText(Path.Combine(directory, $"run-{index:00}.diagnostics.json"), diagnostics);
    }
}

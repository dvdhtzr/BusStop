using BusStop.Domain;

namespace BusStop.Persistence;

/// <summary>
/// V5b is an isolated verifier ownership contract. This is NOT a global
/// ConversationEngine lock and does not change the historical M1 worker.
/// </summary>
public static class V5bOwnershipContract
{
    public const string SchemaVersion = "verifier-ownership-v5b-1";
}

/// <summary>
/// Exclusive file-backed OS gate for one canonical local SQLite database.
/// The .v5b.lock path is left on disk; its OPEN FILE HANDLE is the lock.
/// A crash releases the handle without requiring deletion of a stale file.
/// </summary>
public sealed class V5bProcessGate : IDisposable
{
    private readonly FileStream _handle;
    private bool _disposed;

    private V5bProcessGate(string canonicalDatabasePath, FileStream handle)
    {
        CanonicalDatabasePath = canonicalDatabasePath;
        _handle = handle;
    }

    public string CanonicalDatabasePath { get; }

    public static V5bProcessGate Acquire(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) ||
            databasePath is ":memory:" ||
            databasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "V5b ownership requires the canonical path of a local file-backed SQLite database.");

        var full = Path.GetFullPath(databasePath);
        if (!File.Exists(full))
            throw new FileNotFoundException(
                "V5b must lock an existing, initialized SQLite database.", full);
        if (OperatingSystem.IsWindows() &&
            full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException(
                "V5b process fencing requires a local filesystem, not a UNC share.");

        // Do not unlink this file after release. Unlinking a live lockfile
        // would permit two processes to lock different inodes at one path.
        var handle = new FileStream(full + ".v5b.lock",
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return new V5bProcessGate(full, handle);
    }

    internal void AssertHeldForDatabase(string sqliteSource)
    {
        if (_disposed || _handle.SafeFileHandle.IsClosed ||
            _handle.SafeFileHandle.IsInvalid)
            throw new InvalidOperationException("V5b OS process gate is no longer held.");

        if (string.IsNullOrWhiteSpace(sqliteSource) ||
            sqliteSource == ":memory:")
            throw new InvalidOperationException(
                "V5b fenced write requires file-backed SQLite storage.");
        var candidate = Path.GetFullPath(sqliteSource);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(CanonicalDatabasePath, candidate, comparison))
            throw new InvalidOperationException(
                "V5b process gate belongs to another SQLite database.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }
}

/// <summary>
/// In-process capability: owner/epoch must also match SQLite's LATEST
/// append-only epoch under the SAME write transaction. Holding an old token
/// or a disposed OS gate never authorizes a future write.
/// </summary>
public sealed class V5bOwnership
{
    internal V5bOwnership(
        V5bProcessGate processGate, SessionId session, Guid ownerId, long epoch)
    {
        ProcessGate = processGate;
        Session = session;
        OwnerId = ownerId;
        Epoch = epoch;
    }

    internal V5bProcessGate ProcessGate { get; }
    public SessionId Session { get; }
    public Guid OwnerId { get; }
    public long Epoch { get; }
}

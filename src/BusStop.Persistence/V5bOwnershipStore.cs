using BusStop.Domain;
using BusStop.Moderation;
using Microsoft.Data.Sqlite;

namespace BusStop.Persistence;

/// <summary>
/// Local-only ownership epoch controller. A process first acquires the OS
/// file gate, then atomically advances the session's SQLite epoch.
/// All protected writes check the current epoch UNDER their own transaction.
/// V5b does not schedule, dispatch or resume model work.
/// </summary>
public sealed partial class ConversationStore
{
    private sealed record V5bEpochRow(
        long Epoch, Guid OwnerId, Guid GateId, bool Released);

    private void EnsureV5bSchemaCompatible()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type='table' AND name='verifier_v5b_schema';
            """;
        bool exists = (long)cmd.ExecuteScalar()! == 1;
        if (!exists)
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type='table' AND name LIKE 'verifier_v5b_%';
                """;
            if ((long)cmd.ExecuteScalar()! != 0)
                throw new InvalidDataException(
                    "V5b ownership tables exist without a valid schema marker.");
            return;
        }
        cmd.CommandText = """
            SELECT version FROM verifier_v5b_schema WHERE singleton=1;
            """;
        if (cmd.ExecuteScalar() is not string version ||
            version != V5bOwnershipContract.SchemaVersion)
            throw new InvalidDataException(
                "Unknown or corrupted V5b ownership schema version.");

        cmd.CommandText = "SELECT COUNT(*) FROM verifier_v5b_schema;";
        if ((long)cmd.ExecuteScalar()! != 1)
            throw new InvalidDataException("Ambiguous V5b schema markers.");

        cmd.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type='table' AND name IN
                ('verifier_v5b_schema', 'verifier_v5b_epochs',
                 'verifier_v5b_releases', 'verifier_v5b_attempt_epochs');
            """;
        if ((long)cmd.ExecuteScalar()! != 4)
            throw new InvalidDataException(
                "V5b ownership schema is only partially installed.");
    }

    private V5bEpochRow? V5bLatestEpoch(
        SessionId session, SqliteTransaction? tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT e.epoch, e.owner_id, e.gate_id,
                   EXISTS(SELECT 1 FROM verifier_v5b_releases AS r
                          WHERE r.session_id=e.session_id AND r.epoch=e.epoch)
            FROM verifier_v5b_epochs AS e
            WHERE e.session_id=@session ORDER BY e.epoch DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@session", session.ToString());
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return null;
        var epoch = row.GetInt64(0);
        if (epoch < 1 ||
            !Guid.TryParse(row.GetString(1), out var owner) ||
            !Guid.TryParse(row.GetString(2), out var gate) ||
            owner == Guid.Empty || gate == Guid.Empty)
            throw new InvalidDataException("Invalid V5b persisted ownership epoch.");
        return new V5bEpochRow(epoch, owner, gate, row.GetInt64(3) == 1);
    }

    /// <summary>
    /// Opt in an already-registered V5a session to exclusive fenced writes.
    /// A newly acquired OS gate may advance a prior, still-active epoch after
    /// its former process died. No clock expiry or competing owner can steal.
    /// </summary>
    public V5bOwnership AcquireV5bSession(
        SessionId session, V5bProcessGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        gate.AssertHeldForDatabase(_connection.DataSource);
        EnsureV5bSchemaCompatible();

        var policy = GetV4VerifierPolicy(session)
            ?? throw new InvalidOperationException(
                "V5b ownership requires an explicitly pinned shadow V4 session.");
        policy.Validate();

        using var tx = _connection.BeginTransaction(deferred: false);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT COUNT(*) FROM verifier_v5a_jobs WHERE session_id=@session;
                """;
            cmd.Parameters.AddWithValue("@session", session.ToString());
            if ((long)cmd.ExecuteScalar()! < 1)
                throw new InvalidOperationException(
                    "V5b cannot enroll a session without a registered V5a job.");
        }

        var prior = V5bLatestEpoch(session, tx);
        if (prior is { Released: false } &&
            prior.GateId == gate.InstanceId)
            throw new InvalidOperationException(
                "This OS gate already owns an unreleased V5b session epoch.");

        long next = prior is null ? 1 : checked(prior.Epoch + 1);
        Guid owner = Guid.NewGuid();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO verifier_v5b_epochs
                    (session_id, epoch, owner_id, gate_id, acquired_at)
                VALUES (@session, @epoch, @owner, @gate, @at);
                """;
            cmd.Parameters.AddWithValue("@session", session.ToString());
            cmd.Parameters.AddWithValue("@epoch", next);
            cmd.Parameters.AddWithValue("@owner", owner.ToString("D"));
            cmd.Parameters.AddWithValue("@gate", gate.InstanceId.ToString("D"));
            cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return new V5bOwnership(gate, session, owner, next);
    }

    /// <summary>
    /// Idempotent release for an already released *current* owner. Does not
    /// delete an epoch or free the gate; callers release the OS gate separately.
    /// </summary>
    public void ReleaseV5bSession(V5bOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ownership.ProcessGate.AssertHeldForDatabase(_connection.DataSource);
        using var tx = _connection.BeginTransaction(deferred: false);
        var latest = V5bLatestEpoch(ownership.Session, tx);
        if (latest is null ||
            latest.Epoch != ownership.Epoch ||
            latest.OwnerId != ownership.OwnerId ||
            latest.GateId != ownership.ProcessGate.InstanceId)
            throw new InvalidOperationException(
                "Stale V5b epoch cannot release another worker's ownership.");
        if (latest.Released)
        {
            tx.Commit();
            return;
        }
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO verifier_v5b_releases
                (session_id, epoch, owner_id, released_at)
            VALUES (@session, @epoch, @owner, @at);
            """;
        cmd.Parameters.AddWithValue("@session", ownership.Session.ToString());
        cmd.Parameters.AddWithValue("@epoch", ownership.Epoch);
        cmd.Parameters.AddWithValue("@owner", ownership.OwnerId.ToString("D"));
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>
    /// A separate pre-dispatch check. Actual registration, reserve, finalize
    /// and stage-write authorization is rechecked under THEIR write transaction.
    /// This does not itself send or retry a model request.
    /// </summary>
    public void AssertV5bCanDispatch(V5bOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        using var tx = _connection.BeginTransaction(deferred: false);
        V5bAssertSessionOwner(tx, ownership.Session, ownership,
            requireEnrollment: true);
        tx.Commit();
    }

    /// <summary>
    /// Protects existing V5a/V4 methods only AFTER an explicit V5b epoch
    /// exists. Unenrolled M1/V4/V5a workflows retain original semantics.
    /// The caller must pass the actual in-process OS-gate capability.
    /// </summary>
    private void V5bAssertSessionOwner(
        SqliteTransaction tx, SessionId session, V5bOwnership? ownership,
        bool requireEnrollment = false)
    {
        var epoch = V5bLatestEpoch(session, tx);
        if (epoch is null)
        {
            if (ownership is not null || requireEnrollment)
                throw new InvalidOperationException(
                    "V5b owner token supplied to a session without a fenced epoch.");
            return;
        }
        if (ownership is null)
            throw new InvalidOperationException(
                "Fenced V5b verifier write requires a current ownership token.");
        ownership.ProcessGate.AssertHeldForDatabase(_connection.DataSource);
        if (ownership.Session != session ||
            epoch.Epoch != ownership.Epoch ||
            epoch.OwnerId != ownership.OwnerId ||
            epoch.GateId != ownership.ProcessGate.InstanceId ||
            epoch.Released)
            throw new InvalidOperationException(
                "V5b verifier writer was released or superseded by a newer epoch.");
    }

    /// <summary>
    /// Bind the charged V4 request to the EPOCH that actually reserved it.
    /// Call AFTER inserting the V4 phase and BEFORE committing that SAME
    /// SQLite reservation transaction. Legacy/unfenced sessions add no row.
    /// </summary>
    private void V5bStampAttempt(
        SqliteTransaction tx, SessionId session, Guid requestId,
        V5bOwnership? ownership)
    {
        var current = V5bLatestEpoch(session, tx);
        if (current is null)
        {
            if (ownership is not null)
                throw new InvalidOperationException(
                    "Cannot stamp an unenrolled V5b attempt.");
            return;
        }
        V5bAssertSessionOwner(tx, session, ownership);
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO verifier_v5b_attempt_epochs
                (request_id, session_id, epoch, owner_id, gate_id)
            VALUES (@request, @session, @epoch, @owner, @gate);
            """;
        cmd.Parameters.AddWithValue("@request", requestId.ToString("D"));
        cmd.Parameters.AddWithValue("@session", session.ToString());
        cmd.Parameters.AddWithValue("@epoch", current.Epoch);
        cmd.Parameters.AddWithValue("@owner", current.OwnerId.ToString("D"));
        cmd.Parameters.AddWithValue("@gate", current.GateId.ToString("D"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// An old request can remain charged/Reserved, but must never be marked
    /// Completed or newly linked as if dispatched by a new worker epoch.
    /// V5c will implement a separate explicit recovery classification.
    /// </summary>
    private void V5bAssertAttemptEpoch(
        SqliteTransaction tx, SessionId session, Guid requestId,
        V5bOwnership? ownership)
    {
        var current = V5bLatestEpoch(session, tx);
        if (current is null)
        {
            if (ownership is not null)
                throw new InvalidOperationException(
                    "Cannot authorize a V5b attempt for an unenrolled session.");
            return;
        }
        V5bAssertSessionOwner(tx, session, ownership);
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT epoch, owner_id, gate_id FROM verifier_v5b_attempt_epochs
            WHERE request_id=@request AND session_id=@session;
            """;
        cmd.Parameters.AddWithValue("@request", requestId.ToString("D"));
        cmd.Parameters.AddWithValue("@session", session.ToString());
        using var row = cmd.ExecuteReader();
        if (!row.Read() ||
            row.GetInt64(0) != current.Epoch ||
            row.GetString(1) != current.OwnerId.ToString("D") ||
            row.GetString(2) != current.GateId.ToString("D"))
            throw new InvalidOperationException(
                "Charged V4 request belongs to another or abandoned V5b worker epoch.");
    }

    /// <summary>
    /// Only V4 extraction/verification requests are fenced. M1 uses its
    /// original APIs and is not governed by this experimental worker contract.
    /// </summary>
    private void V5bAssertV4FinalizationOwner(
        SqliteTransaction tx, SessionId session, Guid requestId,
        V5bOwnership? ownership)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT purpose FROM moderator_inference_reservations
            WHERE session_id=@session AND request_id=@request;
            """;
        cmd.Parameters.AddWithValue("@session", session.ToString());
        cmd.Parameters.AddWithValue("@request", requestId.ToString("D"));
        var purpose = cmd.ExecuteScalar() as string;
        if (purpose is nameof(ModerationInferencePurpose.VerifierClaimExtraction)
            or nameof(ModerationInferencePurpose.VerifierClaimVerification))
        {
            V5bAssertSessionOwner(tx, session, ownership);
            V5bAssertAttemptEpoch(tx, session, requestId, ownership);
        }
        else if (ownership is not null)
            throw new InvalidOperationException(
                "V5b capability cannot authorize a non-verifier moderator attempt.");
    }
}

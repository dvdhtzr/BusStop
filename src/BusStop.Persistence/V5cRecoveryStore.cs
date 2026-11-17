using System.Text.Json;
using BusStop.Domain;
using BusStop.Moderation;
using BusStop.Moderation.Verification;
using Microsoft.Data.Sqlite;

namespace BusStop.Persistence;

/// <summary>
/// V5c is an explicitly invoked, model-FREE recovery coordinator.
/// It never launches a worker, chooses a model or queries new evidence.
/// Read/decision stages are private; mutations require the current V5b owner.
/// </summary>
public sealed partial class ConversationStore
{
    private sealed record V5cChargedRow(
        Guid RequestId, V4AttemptPhase Phase, string Status,
        long? Epoch, string? OwnerId);

    private void EnsureV5cSchemaCompatible()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type='table' AND name='verifier_v5c_schema';
            """;
        var present = (long)cmd.ExecuteScalar()! == 1;
        if (!present)
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type='table' AND name LIKE 'verifier_v5c_%';
                """;
            if ((long)cmd.ExecuteScalar()! != 0)
                throw new InvalidDataException(
                    "Partial V5c recovery schema without version marker.");
            return;
        }

        cmd.CommandText =
            "SELECT version FROM verifier_v5c_schema WHERE singleton=1;";
        if (cmd.ExecuteScalar() is not string version ||
            version != V5cContract.Version)
            throw new InvalidDataException("Unknown V5c recovery schema version.");
        cmd.CommandText = "SELECT COUNT(*) FROM verifier_v5c_schema;";
        if ((long)cmd.ExecuteScalar()! != 1)
            throw new InvalidDataException("Ambiguous V5c recovery schema marker.");
        cmd.CommandText = """
            SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND
                name IN ('verifier_v5c_schema', 'verifier_v5c_reconciliations',
                         'verifier_v5c_stops');
            """;
        if ((long)cmd.ExecuteScalar()! != 3)
            throw new InvalidDataException("Incomplete V5c recovery schema.");
    }

    private void V5cAssertJobNotStopped(SqliteTransaction tx, Guid jobId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COUNT(*) FROM verifier_v5c_stops WHERE job_id=@job;
            """;
        cmd.Parameters.AddWithValue("@job", V5aId(jobId));
        if ((long)cmd.ExecuteScalar()! != 0)
            throw new InvalidOperationException(
                "Stopped V5c evaluation cannot be retried or written to.");
    }

    private SessionId V5cSessionForJob(Guid jobId, SqliteTransaction? tx = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT session_id FROM verifier_v5a_jobs WHERE job_id=@job;
            """;
        cmd.Parameters.AddWithValue("@job", V5aId(jobId));
        return cmd.ExecuteScalar() is string session
            ? SessionId.Parse(session)
            : throw new InvalidDataException("Unknown durable V5a recovery job.");
    }

    public V5cStopRecord? GetV5cStop(Guid jobId)
    {
        EnsureV5cSchemaCompatible();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT disposition, reason_code, owner_epoch, owner_id
            FROM verifier_v5c_stops WHERE job_id=@job;
            """;
        cmd.Parameters.AddWithValue("@job", V5aId(jobId));
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return null;
        if (!Enum.TryParse<V5cRecoveryDisposition>(
                row.GetString(0), false, out var disposition) ||
            !Enum.IsDefined(typeof(V5cRecoveryDisposition), disposition))
            throw new InvalidDataException("Invalid persisted V5c stop disposition.");
        return new V5cStopRecord(jobId, disposition,
            row.GetString(1), row.GetInt64(2), Guid.Parse(row.GetString(3)));
    }

    /// <summary>
    /// Bounded, deterministic, keyset-paginated scan. Caller must own the
    /// original V5b OS gate for this session. Stops are returned separately
    /// through GetV5cStop and are not re-scanned.
    /// </summary>
    public IReadOnlyList<V5cRecoveryPlan> ReviewV5cBatch(
        V5bOwnership ownership, V5cRuntimePins runtime,
        int limit = V5cContract.MaxJobsPerBatch, Guid? afterJobId = null)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(runtime);
        if (limit is < 1 or > V5cContract.MaxJobsPerBatch)
            throw new ArgumentOutOfRangeException(nameof(limit));
        AssertV5bCanDispatch(ownership);
        EnsureV5cSchemaCompatible();

        var ids = new List<Guid>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT j.job_id FROM verifier_v5a_jobs AS j
                LEFT JOIN verifier_v5a_summaries AS s ON s.job_id=j.job_id
                LEFT JOIN verifier_v5c_stops AS x ON x.job_id=j.job_id
                WHERE j.session_id=@session AND s.job_id IS NULL
                    AND x.job_id IS NULL AND j.job_id > @cursor
                ORDER BY j.job_id LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@session", ownership.Session.ToString());
            cmd.Parameters.AddWithValue("@cursor", afterJobId.HasValue
                ? afterJobId.Value.ToString("D") : "");
            cmd.Parameters.AddWithValue("@limit", limit);
            using var rows = cmd.ExecuteReader();
            while (rows.Read()) ids.Add(Guid.Parse(rows.GetString(0)));
        }
        var plans = ids.Select(jobId =>
            InspectV5cJob(ownership, jobId, runtime)).ToArray();
        return Array.AsReadOnly(plans);
    }

    /// <summary>
    /// Inspects a single job with frozen evidence and pinned versions.
    /// Malformed checkpoints are recorded as a separate incomplete/error
    /// STOP, without mutating V5a snapshots or promoting findings.
    /// </summary>
    public V5cRecoveryPlan InspectV5cJob(
        V5bOwnership ownership, Guid jobId, V5cRuntimePins runtime)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(runtime);
        AssertV5bCanDispatch(ownership);
        // V5c inspects only after a NEW OS gate has advanced the durable
        // session epoch. The initial V5b owner may still be writing the job.
        if (ownership.Epoch < 2)
            throw new InvalidOperationException(
                "V5c recovery requires a newer worker epoch, not first-owner in-flight work.");
        if (V5cSessionForJob(jobId) != ownership.Session)
            throw new InvalidOperationException("Cross-session V5c inspection denied.");

        var stopped = GetV5cStop(jobId);
        if (stopped is not null)
            return V5cPlan(jobId, V5cRecoveryDisposition.AlreadyStopped,
                "previously-stopped");

        V5aLedgerCheckpoint checkpoint;
        try
        {
            checkpoint = ReadV5aCheckpoint(jobId);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException
                               or SqliteException or FormatException
                               or OverflowException or ArgumentException)
        {
            return V5cStop(ownership, jobId,
                V5cRecoveryDisposition.StoppedIncompatibleCheckpoint,
                "checkpoint-integrity-or-schema");
        }
        if (checkpoint.IsTerminal)
            return V5cPlan(jobId, V5cRecoveryDisposition.AlreadyTerminal,
                "already-final");

        var pinned = checkpoint.PinnedPolicy;
        if (pinned.ModelName != runtime.ModelName ||
            pinned.ModelDigest != runtime.ModelDigest ||
            pinned.RuleVersion != runtime.RuleVersion ||
            pinned.ExtractionPromptVersion != runtime.ExtractionPromptVersion ||
            pinned.VerificationPromptVersion != runtime.VerificationPromptVersion ||
            pinned.RetrievalSelectorVersion != runtime.RetrievalSelectorVersion ||
            pinned.VerificationSchemaVersion != runtime.VerificationSchemaVersion ||
            pinned.VerificationPromptVersion != EvidenceVerifierV4.VerificationPromptVersion ||
            pinned.VerificationSchemaVersion != EvidenceVerifierV4.VerificationSchemaVersion ||
            checkpoint.Packets.Any(packet =>
                packet.Content.SerializerVersion != EvidenceVerifierV1.PacketSerializerVersion ||
                packet.Content.RetrievalSelectorVersion != pinned.RetrievalSelectorVersion ||
                packet.Content.PromptVersion != pinned.ExtractionPromptVersion))
            return V5cStop(ownership, jobId,
                V5cRecoveryDisposition.StoppedIncompatibleCheckpoint,
                "pinned-verifier-runtime-mismatch");

        if (!checkpoint.Events.Any(e => e.Stage == V5aLedgerStage.ClaimsSealed) ||
            !checkpoint.Events.Any(e => e.Stage == V5aLedgerStage.PacketsSealed))
            return V5cStop(ownership, jobId,
                V5cRecoveryDisposition.StoppedMissingFrozenStage,
                "missing-immutable-claim-or-packet-stage");

        var pending = checkpoint.Claims
            .Where(claim => checkpoint.ClaimResults.All(r => r.ClaimId != claim.ClaimId))
            .Select(claim => claim.ClaimId).ToArray();

        // Inspect ALL V4 request reservations for the job, including requests
        // that crashed before V5a's attempt-link stage.
        List<V5cChargedRow> attempts;
        try
        {
            attempts = V5cReadCharged(checkpoint.Job);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException
                               or SqliteException or OverflowException)
        {
            return V5cStop(ownership, jobId,
                V5cRecoveryDisposition.StoppedUnknownAttemptOwner,
                "invalid-charged-attempt-record");
        }
        var reserved = attempts.Where(a => a.Status == "Reserved").ToArray();
        if (attempts.Any(a =>
                a.Status is not ("Reserved" or "Interrupted" or "Completed")) ||
            attempts.Count > EvidenceVerifierV4.MaxTotalCalls ||
            attempts.Any(a => a.Epoch is null || a.Epoch > ownership.Epoch ||
                string.IsNullOrWhiteSpace(a.OwnerId)))
            return V5cStop(ownership, jobId,
                V5cRecoveryDisposition.StoppedUnknownAttemptOwner,
                "missing-or-future-attempt-ownership");

        if (reserved.Length > V5cContract.MaxOrphansPerJob)
            return V5cStop(ownership, jobId,
                V5cRecoveryDisposition.StoppedUnknownAttemptOwner,
                "too-many-unresolved-attempts");

        var counters = GetV4AttemptCounts(checkpoint.Job);
        var moderation = GetModerationPolicy(checkpoint.Job.SessionId);
        var leftSession = Math.Max(0, pinned.MaxSessionCalls - counters.SessionSharedAttempts);
        if (moderation.Mode != ModerationOperatingMode.Disabled)
            leftSession = Math.Min(leftSession,
                Math.Max(0, moderation.MaxModeratorCalls - counters.SessionSharedAttempts));
        var leftUtterance = Math.Max(0,
            Math.Min(pinned.MaxCallsPerUtterance, EvidenceVerifierV4.MaxTotalCalls)
            - counters.UtteranceTotalAttempts);
        var leftVerification = Math.Max(0,
            Math.Min(pinned.MaxVerificationCallsPerUtterance,
                     EvidenceVerifierV4.MaxVerificationCalls)
            - counters.UtteranceVerificationAttempts);

        if (reserved.Length > 0)
        {
            var kind = reserved.Any(a => a.Epoch == ownership.Epoch)
                ? V5cRecoveryDisposition.BlockedActiveAttempt
                : V5cRecoveryDisposition.NeedsOrphanReconciliation;
            return new V5cRecoveryPlan(jobId, kind, Array.AsReadOnly(pending),
                Array.AsReadOnly(reserved.Select(a => a.RequestId).ToArray()),
                leftSession, leftUtterance, leftVerification,
                kind == V5cRecoveryDisposition.BlockedActiveAttempt
                    ? "current-owner-request-still-reserved"
                    : "older-owner-requests-need-interruption");
        }

        if (pending.Length == 0)
            return new V5cRecoveryPlan(jobId,
                V5cRecoveryDisposition.ReadyForSummary,
                Array.Empty<Guid>(), Array.Empty<Guid>(),
                leftSession, leftUtterance, leftVerification,
                "all-claim-results-are-durable");

        if (pending.Any(id => checkpoint.Packets.All(p => p.Content.ClaimId != id)))
            return V5cStop(ownership, jobId,
                V5cRecoveryDisposition.StoppedMissingClaimPacket,
                "missing-original-packet-for-pending-claim");

        if (leftSession == 0 || leftUtterance == 0 || leftVerification == 0)
            return V5cStop(ownership, jobId,
                V5cRecoveryDisposition.StoppedNoRemainingBudget,
                "no-further-inference-budget");

        return new V5cRecoveryPlan(jobId,
            V5cRecoveryDisposition.ReadyForFrozenVerification,
            Array.AsReadOnly(pending), Array.Empty<Guid>(),
            leftSession, leftUtterance, leftVerification,
            "frozen-packets-only-manual-dispatch-permitted");
    }

    private static V5cRecoveryPlan V5cPlan(
        Guid jobId, V5cRecoveryDisposition kind, string reason) =>
        new(jobId, kind, Array.Empty<Guid>(), Array.Empty<Guid>(),
            0, 0, 0, reason);

    private V5cRecoveryPlan V5cStop(
        V5bOwnership ownership, Guid jobId,
        V5cRecoveryDisposition kind, string reason)
    {
        using var tx = _connection.BeginTransaction(deferred: false);
        V5bAssertSessionOwner(tx, ownership.Session, ownership,
            requireEnrollment: true);
        if (V5cSessionForJob(jobId, tx) != ownership.Session)
            throw new InvalidOperationException("Cross-session V5c stop denied.");
        bool alreadyStopped = false;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT disposition, reason_code
                FROM verifier_v5c_stops WHERE job_id=@job;
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            using var row = cmd.ExecuteReader();
            if (row.Read())
            {
                if (row.GetString(0) != kind.ToString() ||
                    row.GetString(1) != reason)
                    throw new InvalidDataException(
                        "V5c stop identity reused with altered reason.");
                alreadyStopped = true;
            }
        }
        if (alreadyStopped)
        {
            tx.Commit();
            return V5cPlan(jobId, V5cRecoveryDisposition.AlreadyStopped,
                "previously-stopped");
        }
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO verifier_v5c_stops
                   (job_id, disposition, reason_code, owner_epoch, owner_id,
                    recorded_at)
                VALUES (@job, @kind, @reason, @epoch, @owner, @at);
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            cmd.Parameters.AddWithValue("@kind", kind.ToString());
            cmd.Parameters.AddWithValue("@reason", reason);
            cmd.Parameters.AddWithValue("@epoch", ownership.Epoch);
            cmd.Parameters.AddWithValue("@owner", ownership.OwnerId.ToString("D"));
            cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return V5cPlan(jobId, kind, reason);
    }

    private List<V5cChargedRow> V5cReadCharged(VerificationJobIdentity job)
    {
        var attempts = new List<V5cChargedRow>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT p.request_id, p.phase, a.status, e.epoch, e.owner_id,
                   p.session_id, p.utterance_id, p.policy_version,
                   a.session_id, a.opportunity_id, a.purpose,
                   e.session_id, e.gate_id
            FROM v4_verifier_attempt_phases AS p
            JOIN moderator_inference_reservations AS a
              ON a.request_id=p.request_id
            LEFT JOIN verifier_v5b_attempt_epochs AS e
              ON e.request_id=p.request_id
            WHERE p.job_id=@job ORDER BY p.reserved_at, p.request_id;
            """;
        cmd.Parameters.AddWithValue("@job", V5aId(job.JobId));
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            if (!Guid.TryParse(rows.GetString(0), out var requestId) ||
                requestId == Guid.Empty ||
                !Enum.TryParse<V4AttemptPhase>(
                    rows.GetString(1), false, out var phase) ||
                !Enum.IsDefined(typeof(V4AttemptPhase), phase))
                throw new InvalidDataException("Invalid V5c charged request identity or phase.");

            var expectedPurpose = phase == V4AttemptPhase.ClaimExtraction
                ? ModerationInferencePurpose.VerifierClaimExtraction.ToString()
                : ModerationInferencePurpose.VerifierClaimVerification.ToString();
            if (rows.GetString(5) != job.SessionId.ToString() ||
                rows.GetString(6) != job.UtteranceId.ToString() ||
                rows.GetString(7) != EvidenceVerifierV4.ContractVersion ||
                rows.GetString(8) != job.SessionId.ToString() ||
                rows.IsDBNull(9) ||
                rows.GetString(9) != job.OpportunityId.ToString() ||
                rows.GetString(10) != expectedPurpose)
                throw new InvalidDataException(
                    "V5c charged request mismatches the original frozen speech and phase.");

            long? epoch = rows.IsDBNull(3) ? null : rows.GetInt64(3);
            var owner = rows.IsDBNull(4) ? null : rows.GetString(4);
            var stampSession = rows.IsDBNull(11) ? null : rows.GetString(11);
            var gate = rows.IsDBNull(12) ? null : rows.GetString(12);
            if (epoch.HasValue != (owner is not null) ||
                epoch.HasValue != (stampSession is not null) ||
                epoch.HasValue != (gate is not null) ||
                (epoch.HasValue && (epoch < 1 ||
                    stampSession != job.SessionId.ToString() ||
                    !Guid.TryParse(owner, out var ownerId) ||
                    ownerId == Guid.Empty ||
                    !Guid.TryParse(gate, out var gateId) ||
                    gateId == Guid.Empty)))
                throw new InvalidDataException(
                    "V5c attempt epoch has malformed or cross-session ownership.");

            attempts.Add(new V5cChargedRow(
                requestId, phase, rows.GetString(2), epoch, owner));
            if (attempts.Count > EvidenceVerifierV4.MaxTotalCalls)
                throw new InvalidDataException(
                    "V5c recovered attempt set exceeds V4's hard ceiling.");
        }
        return attempts;
    }

    /// <summary>
    /// ONE previously charged Reserved request can be classified operationally
    /// Interrupted by a strictly newer, currently fenced owner. It is not
    /// retried, refunded, re-linked, or changed to an epistemic result.
    /// The reservation transition and audit insertion are ONE transaction.
    /// </summary>
    public V5cOrphanReconciliation InterruptV5cAbandonedRequest(
        V5bOwnership ownership, Guid jobId, Guid requestId)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (requestId == Guid.Empty || jobId == Guid.Empty)
            throw new InvalidDataException("Invalid recovery request or job ID.");
        AssertV5bCanDispatch(ownership);
        var checkpoint = ReadV5aCheckpoint(jobId);
        if (checkpoint.IsTerminal || checkpoint.Job.SessionId != ownership.Session)
            throw new InvalidOperationException(
                "V5c cannot reconcile a terminal or foreign session.");
        var selected = V5cReadCharged(checkpoint.Job).SingleOrDefault(a =>
            a.RequestId == requestId);
        if (selected is null || selected.Epoch is null ||
            selected.Epoch >= ownership.Epoch || selected.OwnerId is null)
            throw new InvalidDataException(
                "V5c can only interrupt a request owned by a known older epoch.");

        using var tx = _connection.BeginTransaction(deferred: false);
        V5bAssertSessionOwner(tx, ownership.Session, ownership,
            requireEnrollment: true);
        V5cAssertJobNotStopped(tx, jobId);

        long previousEpoch;
        string status;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT e.epoch, a.status FROM v4_verifier_attempt_phases AS p
                JOIN verifier_v5b_attempt_epochs AS e ON e.request_id=p.request_id
                JOIN moderator_inference_reservations AS a
                  ON a.request_id=p.request_id
                WHERE p.request_id=@request AND p.job_id=@job
                  AND a.session_id=@session AND e.session_id=@session;
                """;
            cmd.Parameters.AddWithValue("@request", V5aId(requestId));
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            cmd.Parameters.AddWithValue("@session", ownership.Session.ToString());
            using var row = cmd.ExecuteReader();
            if (!row.Read())
                throw new InvalidDataException("V5c charged reservation identity changed.");
            previousEpoch = row.GetInt64(0);
            status = row.GetString(1);
        }
        if (previousEpoch < 1 || previousEpoch >= ownership.Epoch)
            throw new InvalidOperationException("V5c cannot interrupt current/future owner request.");

        V5cOrphanReconciliation record;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT prior_epoch, recovery_epoch, recovery_owner_id,
                       policy_sha256, disposition
                FROM verifier_v5c_reconciliations WHERE request_id=@request;
                """;
            cmd.Parameters.AddWithValue("@request", V5aId(requestId));
            using var row = cmd.ExecuteReader();
            if (row.Read())
            {
                if (row.GetInt64(0) != previousEpoch ||
                    row.GetInt64(1) != ownership.Epoch ||
                    row.GetString(2) != ownership.OwnerId.ToString("D") ||
                    row.GetString(3) != checkpoint.PolicySha256 ||
                    row.GetString(4) != "InterruptedAfterCrash" ||
                    status != "Interrupted")
                    throw new InvalidDataException(
                        "V5c orphan reconciliation already has different immutable provenance.");
                record = new V5cOrphanReconciliation(jobId, requestId,
                    previousEpoch, ownership.Epoch, ownership.OwnerId,
                    checkpoint.PolicySha256, "InterruptedAfterCrash");
                tx.Commit();
                return record;
            }
        }
        if (status != "Reserved")
            throw new InvalidOperationException(
                "Only a previously charged Reserved attempt may be interrupted.");

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE moderator_inference_reservations SET status='Interrupted'
                WHERE request_id=@request AND session_id=@session
                  AND status='Reserved';
                """;
            cmd.Parameters.AddWithValue("@request", V5aId(requestId));
            cmd.Parameters.AddWithValue("@session", ownership.Session.ToString());
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException(
                    "V5c interruption lost the charged-attempt state transition.");
        }
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO verifier_v5c_reconciliations
                    (request_id, job_id, prior_epoch, recovery_epoch,
                     recovery_owner_id, policy_sha256, disposition, recorded_at)
                VALUES (@request, @job, @prior, @epoch,
                        @owner, @policy, 'InterruptedAfterCrash', @at);
                """;
            cmd.Parameters.AddWithValue("@request", V5aId(requestId));
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            cmd.Parameters.AddWithValue("@prior", previousEpoch);
            cmd.Parameters.AddWithValue("@epoch", ownership.Epoch);
            cmd.Parameters.AddWithValue("@owner", ownership.OwnerId.ToString("D"));
            cmd.Parameters.AddWithValue("@policy", checkpoint.PolicySha256);
            cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        record = new V5cOrphanReconciliation(jobId, requestId,
            previousEpoch, ownership.Epoch, ownership.OwnerId,
            checkpoint.PolicySha256, "InterruptedAfterCrash");
        return record;
    }

    /// <summary>
    /// Reconstruct only a *conservative* summary from already persisted
    /// per-claim outcomes. Complete model results are never regenerated.
    /// This does not require an LLM, and Partial is NOT proof of completeness.
    /// </summary>
    public void SealV5cRecoveredSummary(
        V5bOwnership ownership, Guid jobId, V5cRuntimePins runtime)
    {
        var plan = InspectV5cJob(ownership, jobId, runtime);
        if (plan.Disposition != V5cRecoveryDisposition.ReadyForSummary)
            throw new InvalidOperationException(
                "Recovery summary requires all frozen claim results and no unresolved request.");

        var checkpoint = ReadV5aCheckpoint(jobId);
        var results = checkpoint.ClaimResults;
        var verified = results.Count(r => r.Status == VerificationOperationalStatus.Completed);
        var skipped = results.Count(r => r.Status == VerificationOperationalStatus.SkippedForBudget);
        var failed = results.Count - verified - skipped;
        var summary = new VerificationObservationSummary(
            jobId, checkpoint.Claims.Count, verified,
            results.Count(r => r.Judgment?.Outcome == ClaimVerificationOutcome.ConfirmedContradiction),
            results.Count(r => r.Judgment?.Outcome == ClaimVerificationOutcome.NoMaterialIssue),
            results.Count(r => r.Judgment?.Outcome == ClaimVerificationOutcome.InsufficientEvidence),
            results.Count(r => r.Judgment?.Outcome == ClaimVerificationOutcome.ConflictingEvidence),
            skipped, failed,
            failed > 0 || skipped > 0
                ? VerificationCompletionStatus.Incomplete
                : VerificationCompletionStatus.Partial,
            "Recovered durable claim dispositions under frozen packets; extraction/source coverage not certified.");

        // V5a rechecks every count and current V5b ownership under its own
        // write transaction. A concurrent stage mutation cannot be hidden.
        SealV5aSummary(jobId, summary, ownership);
    }
}

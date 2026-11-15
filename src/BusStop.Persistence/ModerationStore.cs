using BusStop.Domain;
using BusStop.Moderation;
using Microsoft.Data.Sqlite;

namespace BusStop.Persistence;

public sealed partial class ConversationStore
{
    private void EnsureModerationMarkerColumn()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(sessions);";
        var found = false;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                if (reader.GetString(1) == "moderation_policy_marker")
                    found = true;
        }

        if (!found)
            Execute("ALTER TABLE sessions ADD COLUMN moderation_policy_marker TEXT NULL;");
    }

    private void InsertModerationPolicy(
        SqliteTransaction transaction, SessionId session, ModerationSessionPolicy policy)
    {
        policy.Validate();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO session_moderation_policies (
                session_id, contract_version, mode, rule_version, prompt_version,
                model_name, model_digest, temperature, think, max_moderator_calls,
                max_revisions, stagnation_window, nudge_cooldown)
            VALUES (@session, @contract, @mode, @rules, @prompt, @model, @digest,
                @temp, @think, @calls, @revisions, @window, @cooldown);
            """;
        cmd.Parameters.AddWithValue("@session", session.ToString());
        cmd.Parameters.AddWithValue("@contract", policy.ContractVersion);
        cmd.Parameters.AddWithValue("@mode", policy.Mode.ToString());
        cmd.Parameters.AddWithValue("@rules", policy.RuleVersion);
        cmd.Parameters.AddWithValue("@prompt", policy.PromptVersion);
        cmd.Parameters.AddWithValue("@model", (object?)policy.ModelName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@digest", (object?)policy.ModelDigest ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@temp", (object?)policy.Temperature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@think", policy.Think.HasValue ? (object)(policy.Think.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("@calls", policy.MaxModeratorCalls);
        cmd.Parameters.AddWithValue("@revisions", policy.MaxRevisionsPerOpportunity);
        cmd.Parameters.AddWithValue("@window", policy.StagnationWindow);
        cmd.Parameters.AddWithValue("@cooldown", policy.NudgeCooldownOpportunities);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Legacy sessions without a marker are disabled. Incomplete or incompatible
    /// pinned policies on new sessions are errors, never permission to downgrade.</summary>
    public ModerationSessionPolicy GetModerationPolicy(SessionId session)
    {
        using var markerCmd = _connection.CreateCommand();
        markerCmd.CommandText = "SELECT moderation_policy_marker FROM sessions WHERE id = @session;";
        markerCmd.Parameters.AddWithValue("@session", session.ToString());
        var marker = markerCmd.ExecuteScalar();
        if (marker is null)
            throw new InvalidOperationException("Missing conversation session.");
        if (marker is DBNull)
            return ModerationSessionPolicy.Disabled;
        if (!string.Equals(marker as string, ModerationSessionPolicy.CurrentContractVersion,
            StringComparison.Ordinal))
            throw new InvalidOperationException("Incompatible persisted moderation policy marker.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT contract_version, mode, rule_version, prompt_version, model_name,
                   model_digest, temperature, think, max_moderator_calls, max_revisions,
                   stagnation_window, nudge_cooldown
            FROM session_moderation_policies WHERE session_id = @session;
            """;
        cmd.Parameters.AddWithValue("@session", session.ToString());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("Pinned moderation policy is missing.");

        if (!Enum.TryParse<ModerationOperatingMode>(reader.GetString(1), out var mode) ||
            !Enum.IsDefined(typeof(ModerationOperatingMode), mode))
            throw new InvalidOperationException("Unrecognized persisted moderation mode.");

        var thinkValue = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
        if (thinkValue is not null and not (0 or 1))
            throw new InvalidOperationException("Invalid pinned moderator thinking setting.");

        var policy = new ModerationSessionPolicy(
            reader.GetString(0), mode, reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            thinkValue is null ? null : thinkValue == 1,
            reader.GetInt32(8), reader.GetInt32(9),
            reader.GetInt32(10), reader.GetInt32(11));
        policy.Validate();
        if (!string.Equals(marker as string, policy.ContractVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Moderation marker and policy disagree.");
        return policy;
    }

    /// <summary>Hard ceiling reservation. A retry uses a NEW request ID and is charged.
    /// An idempotent repeat of an already reserved ID does not receive a new allowance.</summary>
    public ModeratorInferenceReservation ReserveModeratorAttempt(
        SessionId session, Guid requestId, OpportunityId? opportunity,
        ModerationInferencePurpose purpose, DateTimeOffset reservedAt)
    {
        if (requestId == Guid.Empty || !Enum.IsDefined(typeof(ModerationInferencePurpose), purpose))
            throw new InvalidOperationException("Invalid moderator inference request.");
        var policy = GetModerationPolicy(session);
        if (policy.Mode == ModerationOperatingMode.Disabled)
            throw new InvalidOperationException("Moderator inference is disabled.");
        if (GetSession(session)?.Status != SessionStatus.Active)
            throw new InvalidOperationException("Moderator inference requires an active session.");
        if (opportunity is { } opp && !GetRecordedOpportunities(session).Any(o => o.Id == opp))
            throw new InvalidOperationException("Moderator inference references an unknown speaking opportunity.");

        using var tx = _connection.BeginTransaction(deferred: false);
        using (var existingCmd = _connection.CreateCommand())
        {
            existingCmd.Transaction = tx;
            existingCmd.CommandText = """
                SELECT session_id, opportunity_id, purpose, status, reserved_at
                FROM moderator_inference_reservations WHERE request_id = @id;
                """;
            existingCmd.Parameters.AddWithValue("@id", requestId.ToString("D"));
            ModeratorInferenceReservation? saved = null;
            using (var reader = existingCmd.ExecuteReader())
            {
                if (reader.Read())
                    saved = new ModeratorInferenceReservation(
                        requestId, SessionId.Parse(reader.GetString(0)),
                        reader.IsDBNull(1) ? null : OpportunityId.Parse(reader.GetString(1)),
                        Enum.Parse<ModerationInferencePurpose>(reader.GetString(2)),
                        Enum.Parse<ModerationAttemptStatus>(reader.GetString(3)),
                        DateTimeOffset.Parse(reader.GetString(4)));
            }

            if (saved is not null)
            {
                if (saved.Session != session || saved.Opportunity != opportunity || saved.Purpose != purpose)
                    throw new InvalidOperationException("Moderator reservation ID reused with different scope.");
                tx.Commit();
                return saved;
            }
        }

        using (var count = _connection.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(*) FROM moderator_inference_reservations WHERE session_id = @session;";
            count.Parameters.AddWithValue("@session", session.ToString());
            if ((long)count.ExecuteScalar()! >= policy.MaxModeratorCalls)
                throw new InvalidOperationException("Moderator inference budget exhausted.");
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO moderator_inference_reservations
                    (request_id, session_id, opportunity_id, purpose, status, reserved_at)
                VALUES (@id, @session, @opportunity, @purpose, @status, @at);
                """;
            insert.Parameters.AddWithValue("@id", requestId.ToString("D"));
            insert.Parameters.AddWithValue("@session", session.ToString());
            insert.Parameters.AddWithValue("@opportunity", (object?)opportunity?.ToString() ?? DBNull.Value);
            insert.Parameters.AddWithValue("@purpose", purpose.ToString());
            insert.Parameters.AddWithValue("@status", ModerationAttemptStatus.Reserved.ToString());
            insert.Parameters.AddWithValue("@at", reservedAt.ToUniversalTime().ToString("O"));
            insert.ExecuteNonQuery();
        }
        tx.Commit();
        return new ModeratorInferenceReservation(
            requestId, session, opportunity, purpose, ModerationAttemptStatus.Reserved, reservedAt);
    }

    public IReadOnlyList<ModeratorInferenceReservation> GetModeratorReservations(SessionId session)
    {
        var results = new List<ModeratorInferenceReservation>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT request_id, opportunity_id, purpose, status, reserved_at
            FROM moderator_inference_reservations WHERE session_id = @session
            ORDER BY reserved_at, request_id;
            """;
        cmd.Parameters.AddWithValue("@session", session.ToString());
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new ModeratorInferenceReservation(
                Guid.Parse(reader.GetString(0)), session,
                reader.IsDBNull(1) ? null : OpportunityId.Parse(reader.GetString(1)),
                Enum.Parse<ModerationInferencePurpose>(reader.GetString(2)),
                Enum.Parse<ModerationAttemptStatus>(reader.GetString(3)),
                DateTimeOffset.Parse(reader.GetString(4))));
        return results;
    }

    public void FinalizeModeratorAttempt(
        SessionId session, Guid requestId, ModerationAttemptStatus status)
    {
        if (status is not (ModerationAttemptStatus.Completed or ModerationAttemptStatus.Interrupted))
            throw new InvalidOperationException("Only completed or interrupted is a final attempt status.");

        using var tx = _connection.BeginTransaction(deferred: false);
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT status FROM moderator_inference_reservations
            WHERE request_id = @id AND session_id = @session;
            """;
        cmd.Parameters.AddWithValue("@id", requestId.ToString("D"));
        cmd.Parameters.AddWithValue("@session", session.ToString());
        var old = cmd.ExecuteScalar() as string;
        if (old is null)
            throw new InvalidOperationException("Moderator request is not reserved for the session.");
        if (old == status.ToString())
        {
            tx.Commit();
            return;
        }
        if (old != ModerationAttemptStatus.Reserved.ToString())
            throw new InvalidOperationException("An attempt cannot change its final disposition.");

        using var update = _connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE moderator_inference_reservations SET status = @status
            WHERE request_id = @id AND session_id = @session AND status = 'Reserved';
            """;
        update.Parameters.AddWithValue("@status", status.ToString());
        update.Parameters.AddWithValue("@id", requestId.ToString("D"));
        update.Parameters.AddWithValue("@session", session.ToString());
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Moderator attempt transition failed.");
        tx.Commit();
    }
}

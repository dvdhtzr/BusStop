using System.Globalization;
using BusStop.Domain;
using BusStop.Moderation;
using BusStop.Moderation.Verification;
using Microsoft.Data.Sqlite;

namespace BusStop.Persistence;

/// <summary>
/// V4 private accounting only. All attempt IDs are charged in the historical
/// moderator_inference_reservations table; a versioned sidecar records their phase.
/// No existing M1 review or prompt path calls these methods.
/// </summary>
public sealed partial class ConversationStore
{
    /// <summary>Explicitly pin an opt-in shadow policy before first opportunity.
    /// An absent policy is disabled; existing sessions never silently enroll.</summary>
    public void PinV4VerifierPolicy(SessionId sessionId, V4SessionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        var session = GetSession(sessionId);
        if (session is null || session.Status != SessionStatus.Active)
            throw new InvalidOperationException("V4 policy requires an active new session.");

        using var tx = _connection.BeginTransaction(deferred: false);
        using (var check = _connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText =
                "SELECT COUNT(*) FROM speaking_opportunities WHERE session_id = @s;";
            check.Parameters.AddWithValue("@s", sessionId.ToString());
            if ((long)check.ExecuteScalar()! != 0)
                throw new InvalidOperationException(
                    "V4 policy must be explicitly pinned before the first speaking opportunity.");
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO session_v4_verifier_policies
                    (session_id, contract_version, mode, rule_version,
                     extraction_prompt, verification_prompt, retrieval_selector,
                     verification_schema, model_name, model_digest, temperature,
                     think, max_session_calls, max_utterance_calls,
                     max_verification_calls, generated_tokens, context_tokens)
                VALUES
                    (@s, @contract, @mode, @rules, @extract, @verify, @selector,
                     @schema, @model, @digest, @temp, @think,
                     @session_calls, @utterance_calls, @verification_calls,
                     @generated, @context)
                ON CONFLICT(session_id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("@s", sessionId.ToString());
            cmd.Parameters.AddWithValue("@contract", policy.ContractVersion);
            cmd.Parameters.AddWithValue("@mode", policy.Mode.ToString());
            cmd.Parameters.AddWithValue("@rules", policy.RuleVersion);
            cmd.Parameters.AddWithValue("@extract", policy.ExtractionPromptVersion);
            cmd.Parameters.AddWithValue("@verify", policy.VerificationPromptVersion);
            cmd.Parameters.AddWithValue("@selector", policy.RetrievalSelectorVersion);
            cmd.Parameters.AddWithValue("@schema", policy.VerificationSchemaVersion);
            cmd.Parameters.AddWithValue("@model", policy.ModelName);
            cmd.Parameters.AddWithValue("@digest", policy.ModelDigest);
            cmd.Parameters.AddWithValue("@temp", policy.Temperature);
            cmd.Parameters.AddWithValue("@think", policy.Think ? 1 : 0);
            cmd.Parameters.AddWithValue("@session_calls", policy.MaxSessionCalls);
            cmd.Parameters.AddWithValue("@utterance_calls", policy.MaxCallsPerUtterance);
            cmd.Parameters.AddWithValue("@verification_calls",
                policy.MaxVerificationCallsPerUtterance);
            cmd.Parameters.AddWithValue("@generated", policy.MaxGeneratedTokens);
            cmd.Parameters.AddWithValue("@context", policy.ContextTokens);
            cmd.ExecuteNonQuery();
        }
        // Read within transaction to compare the entire existing policy.
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = PolicySelect + " WHERE session_id = @s;";
            cmd.Parameters.AddWithValue("@s", sessionId.ToString());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || MapV4Policy(reader) != policy)
                throw new InvalidOperationException(
                    "V4 session policy already pinned to different values.");
        }
        tx.Commit();
    }

    private const string PolicySelect = """
        SELECT contract_version, mode, rule_version, extraction_prompt,
               verification_prompt, retrieval_selector, verification_schema,
               model_name, model_digest, temperature, think,
               max_session_calls, max_utterance_calls, max_verification_calls,
               generated_tokens, context_tokens
        FROM session_v4_verifier_policies
        """;

    /// <summary>Null means explicitly NOT enrolled. Invalid rows fail closed.</summary>
    public V4SessionPolicy? GetV4VerifierPolicy(SessionId sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = PolicySelect + " WHERE session_id = @s;";
        cmd.Parameters.AddWithValue("@s", sessionId.ToString());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var pinned = MapV4Policy(reader);
        pinned.Validate();
        return pinned;
    }

    private static V4SessionPolicy MapV4Policy(SqliteDataReader reader)
    {
        var modeText = reader.GetString(1);
        if (!Enum.TryParse<VerificationOperatingMode>(modeText, false, out var mode) ||
            !Enum.IsDefined(typeof(VerificationOperatingMode), mode))
            throw new InvalidDataException("Unrecognized pinned V4 verifier mode.");
        var think = reader.GetInt32(10);
        if (think is not (0 or 1))
            throw new InvalidDataException("Invalid pinned V4 thinking setting.");
        return new V4SessionPolicy(
            reader.GetString(0), mode, reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8),
            reader.GetDouble(9), think == 1, reader.GetInt32(11),
            reader.GetInt32(12), reader.GetInt32(13),
            reader.GetInt32(14), reader.GetInt32(15));
    }

    /// <summary>
    /// Atomically reserve a new shared moderator request and its V4 phase.
    /// Unique request IDs cannot be reused to dispatch, even if left Reserved.
    /// All checks and both inserts happen under the same BEGIN IMMEDIATE lock.
    /// Returns false ONLY for exhausted approved ceilings; invalid identity,
    /// policy or stage is a hard failure.
    /// </summary>
    public bool TryReserveV4Attempt(
        VerificationJobIdentity job, Guid requestId, V4AttemptPhase phase,
        V5bOwnership? ownership = null)
    {
        if (requestId == Guid.Empty ||
            !Enum.IsDefined(typeof(V4AttemptPhase), phase))
            throw new InvalidDataException("Invalid V4 request identity or phase.");
        var policy = GetV4VerifierPolicy(job.SessionId)
            ?? throw new InvalidOperationException("V4 verifier is disabled for this session.");
        policy.Validate();

        var committed = GetCommittedUtterances(job.SessionId)
            .SingleOrDefault(x => x.Id == job.UtteranceId)
            ?? throw new InvalidDataException("Unknown V4 committed utterance.");
        EvidenceVerificationValidator.ValidateJob(job, committed);
        ValidateV4RecordedOpportunityBoundary(job);

        var session = GetSession(job.SessionId)
            ?? throw new InvalidDataException("Missing V4 session.");
        if (session.Status is not (SessionStatus.Active or SessionStatus.Ended))
            throw new InvalidOperationException("V4 only permits active or ended shadow observation.");

        // An enabled M1 policy also participates in the shared request ceiling;
        // we do not rewrite its immutable policy or reservation semantics.
        var moderation = GetModerationPolicy(job.SessionId);

        using var tx = _connection.BeginTransaction(deferred: false);
        // A fenced V5b session cannot dispatch an unowned inference request.
        // Check in the SAME transaction as the shared-budget reservation.
        V5bAssertSessionOwner(tx, job.SessionId, ownership);
        using (var duplicate = _connection.CreateCommand())
        {
            duplicate.Transaction = tx;
            duplicate.CommandText =
                "SELECT COUNT(*) FROM moderator_inference_reservations WHERE request_id = @id;";
            duplicate.Parameters.AddWithValue("@id", requestId.ToString("D"));
            if ((long)duplicate.ExecuteScalar()! != 0)
                throw new InvalidOperationException(
                    "A charged moderator request ID cannot authorize a second dispatch.");
        }

        long sharedCount;
        using (var count = _connection.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText =
                "SELECT COUNT(*) FROM moderator_inference_reservations WHERE session_id = @s;";
            count.Parameters.AddWithValue("@s", job.SessionId.ToString());
            sharedCount = (long)count.ExecuteScalar()!;
        }

        var (extraction, verification) = CountV4Utterance(job.UtteranceId, tx);
        if (sharedCount >= policy.MaxSessionCalls ||
            (moderation.Mode != ModerationOperatingMode.Disabled &&
             sharedCount >= moderation.MaxModeratorCalls) ||
            extraction + verification >= policy.MaxCallsPerUtterance ||
            extraction + verification >= EvidenceVerifierV4.MaxTotalCalls ||
            (phase == V4AttemptPhase.ClaimExtraction &&
             extraction >= EvidenceVerifierV4.MaxExtractionCalls) ||
            (phase == V4AttemptPhase.ClaimVerification &&
             verification >= policy.MaxVerificationCallsPerUtterance))
        {
            tx.Commit();
            return false;
        }

        var purpose = phase == V4AttemptPhase.ClaimExtraction
            ? ModerationInferencePurpose.VerifierClaimExtraction
            : ModerationInferencePurpose.VerifierClaimVerification;
        var now = DateTimeOffset.UtcNow.ToUniversalTime().ToString("O");
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO moderator_inference_reservations
                    (request_id, session_id, opportunity_id, purpose, status, reserved_at)
                VALUES (@id, @session, @opportunity, @purpose, 'Reserved', @at);
                """;
            insert.Parameters.AddWithValue("@id", requestId.ToString("D"));
            insert.Parameters.AddWithValue("@session", job.SessionId.ToString());
            insert.Parameters.AddWithValue("@opportunity", job.OpportunityId.ToString());
            insert.Parameters.AddWithValue("@purpose", purpose.ToString());
            insert.Parameters.AddWithValue("@at", now);
            insert.ExecuteNonQuery();
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO v4_verifier_attempt_phases
                    (request_id, job_id, session_id, utterance_id,
                     phase, policy_version, reserved_at)
                VALUES (@id, @job, @session, @utterance, @phase, @policy, @at);
                """;
            insert.Parameters.AddWithValue("@id", requestId.ToString("D"));
            insert.Parameters.AddWithValue("@job", job.JobId.ToString("D"));
            insert.Parameters.AddWithValue("@session", job.SessionId.ToString());
            insert.Parameters.AddWithValue("@utterance", job.UtteranceId.ToString());
            insert.Parameters.AddWithValue("@phase", phase.ToString());
            insert.Parameters.AddWithValue("@policy", policy.ContractVersion);
            insert.Parameters.AddWithValue("@at", now);
            insert.ExecuteNonQuery();
        }
        // Stamp request and fencing epoch in the SAME transaction as its
        // shared-budget reservation. A restarted owner cannot recycle it.
        V5bStampAttempt(tx, job.SessionId, requestId, ownership);
        tx.Commit();
        return true;
    }

    public V4AttemptCounts GetV4AttemptCounts(
        VerificationJobIdentity job)
    {
        var committed = GetCommittedUtterances(job.SessionId)
            .SingleOrDefault(x => x.Id == job.UtteranceId)
            ?? throw new InvalidDataException("Unknown committed utterance.");
        EvidenceVerificationValidator.ValidateJob(job, committed);
        ValidateV4RecordedOpportunityBoundary(job);

        int shared;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT COUNT(*) FROM moderator_inference_reservations WHERE session_id = @s;";
            cmd.Parameters.AddWithValue("@s", job.SessionId.ToString());
            shared = checked((int)(long)cmd.ExecuteScalar()!);
        }
        var (extraction, verification) = CountV4Utterance(job.UtteranceId, null);
        return new V4AttemptCounts(shared, extraction, verification);
    }

    /// <summary>The committed turn must still bind to its original recorded
    /// speaking opportunity, not merely to a reconstructed sequence number.</summary>
    private void ValidateV4RecordedOpportunityBoundary(VerificationJobIdentity job)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT committed_boundary FROM speaking_opportunities
            WHERE id = @opp AND session_id = @session
              AND intended_participant = @speaker;
            """;
        cmd.Parameters.AddWithValue("@opp", job.OpportunityId.ToString());
        cmd.Parameters.AddWithValue("@session", job.SessionId.ToString());
        cmd.Parameters.AddWithValue("@speaker", job.SpeakerId.ToString());
        if (cmd.ExecuteScalar() is not long boundary ||
            boundary != job.CommittedBoundary)
            throw new InvalidDataException(
                "V4 original speaking-opportunity evidence boundary does not match the job.");
    }

    private (int Extraction, int Verification) CountV4Utterance(
        UtteranceId utterance, SqliteTransaction? tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT phase, COUNT(*) FROM v4_verifier_attempt_phases
            WHERE utterance_id = @u GROUP BY phase;
            """;
        cmd.Parameters.AddWithValue("@u", utterance.ToString());
        int extracted = 0, verified = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var count = checked((int)reader.GetInt64(1));
            switch (reader.GetString(0))
            {
                case "ClaimExtraction": extracted = count; break;
                case "ClaimVerification": verified = count; break;
                default: throw new InvalidDataException("Unknown V4 charged phase.");
            }
        }
        return (extracted, verified);
    }
}

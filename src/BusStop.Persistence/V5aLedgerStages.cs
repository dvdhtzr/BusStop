using System.Text.Json;
using System.Text.RegularExpressions;
using BusStop.Domain;
using BusStop.Moderation;
using BusStop.Moderation.Verification;
using Microsoft.Data.Sqlite;

namespace BusStop.Persistence;

/// <summary>
/// V5a isolated append-only stage writers. Each call uses a single SQLite
/// IMMEDIATE transaction for sidecars and chain event. No call dispatches
/// inference, changes speaker history or returns findings to participants.
/// </summary>
public sealed partial class ConversationStore
{
    public void SealV5aClaims(Guid jobId, IReadOnlyList<ClaimCandidate> claims,
        V5bOwnership? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var checkpoint = ReadV5aCheckpoint(jobId);
        var committed = V5aCommitted(checkpoint.Job);
        if (claims.Count > V5aLedgerContract.MaxClaims)
            throw new InvalidDataException("V5a claim count exceeds frozen safety ceiling.");
        for (var i = 0; i < claims.Count; i++)
            if (claims[i] is null || claims[i].Index != i ||
                claims[i].ValidationStatus != ClaimValidationStatus.HostValidated)
                throw new InvalidDataException(
                    "V5a accepts only sequential host-validated exact-span claims.");
        EvidenceVerificationValidator.ValidateClaims(claims, checkpoint.Job, committed);
        var snapshot = V5aJson(claims.ToArray());

        using var tx = _connection.BeginTransaction(deferred: false);
        V5bAssertSessionOwner(tx, checkpoint.Job.SessionId, ownership);
        V5cAssertJobNotStopped(tx, jobId);
        if (V5aStageIsSame(tx, jobId, "claims:sealed", snapshot))
        {
            tx.Commit();
            return;
        }
        V5aRequireOpen(tx, jobId);
        if (V5aHasStage(tx, jobId, V5aLedgerStage.ClaimsSealed) ||
            V5aHasStage(tx, jobId, V5aLedgerStage.PacketsSealed))
            throw new InvalidDataException("V5a claim snapshot cannot be resealed.");

        foreach (var claim in claims)
        {
            var json = V5aJson(claim);
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO verifier_v5a_claims
                    (job_id, claim_id, claim_index, claim_json, claim_sha256)
                VALUES (@job, @claim, @index, @json, @sha);
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            cmd.Parameters.AddWithValue("@claim", V5aId(claim.ClaimId));
            cmd.Parameters.AddWithValue("@index", claim.Index);
            cmd.Parameters.AddWithValue("@json", json);
            cmd.Parameters.AddWithValue("@sha", V5aHash(json));
            cmd.ExecuteNonQuery();
        }
        V5aAppendEvent(tx, jobId, "claims:sealed",
            V5aLedgerStage.ClaimsSealed, snapshot);
        tx.Commit();
    }

    public void SealV5aPackets(Guid jobId, IReadOnlyList<EvidencePacket> packets,
        V5bOwnership? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(packets);
        var checkpoint = ReadV5aCheckpoint(jobId);
        if (!checkpoint.Events.Any(e => e.Stage == V5aLedgerStage.ClaimsSealed))
            throw new InvalidDataException("V5a cannot freeze packets before the claim snapshot.");
        if (packets.Count > V5aLedgerContract.MaxPackets ||
            packets.Count > checkpoint.Claims.Count)
            throw new InvalidDataException("V5a packet count exceeds claims or frozen safety cap.");

        if (packets.Any(p => p?.Content is null))
            throw new InvalidDataException("V5a packet or content is missing.");
        var ids = new HashSet<Guid>();
        var ordered = packets.OrderBy(p => p.Content.ClaimId.ToString("D"),
            StringComparer.Ordinal).ToArray();
        foreach (var packet in ordered)
        {
            if (!ids.Add(packet.Content.ClaimId))
                throw new InvalidDataException("V5a has a duplicate claim packet.");
            var claim = checkpoint.Claims.SingleOrDefault(c =>
                c.ClaimId == packet.Content.ClaimId)
                ?? throw new InvalidDataException("V5a packet refers to unknown claim.");
            if (packet.Content.RetrievalSelectorVersion !=
                    checkpoint.PinnedPolicy.RetrievalSelectorVersion ||
                packet.Content.PromptVersion !=
                    checkpoint.PinnedPolicy.ExtractionPromptVersion)
                throw new InvalidDataException("V5a packet versions differ from the pinned session.");
            // Validate the supplied immutable envelope independently from
            // retrieval, including id, canonical bytes and SHA.
            var freeze = EvidencePacketFactory.Freeze(
                packet.PacketId, packet.Content);
            if (freeze.CanonicalJson != packet.CanonicalJson ||
                freeze.Sha256 != packet.Sha256)
                throw new InvalidDataException("V5a packet envelope/hash is inconsistent.");
        }

        var snapshot = V5aJson(ordered.Select(p =>
            new V5aPacketEnvelope(p.PacketId, p.Content.ClaimId, p.Sha256)).ToArray());
        var existingFreeze = checkpoint.Events.SingleOrDefault(e =>
            e.Stage == V5aLedgerStage.PacketsSealed);
        if (existingFreeze is not null)
        {
            // True replay reads ONLY the previous immutable snapshot. In
            // particular, it does not perform new evidence retrieval.
            if (existingFreeze.PayloadJson != snapshot ||
                checkpoint.Packets.Count != ordered.Length ||
                ordered.Any(p => !checkpoint.Packets.Any(saved =>
                    saved.PacketId == p.PacketId &&
                    saved.Sha256 == p.Sha256 &&
                    saved.CanonicalJson == p.CanonicalJson)))
                throw new InvalidDataException(
                    "V5a packet freeze idempotency key reused with changed bytes.");
            // Repeated freeze is read-only but still requires valid ownership.
            using var replayTx = _connection.BeginTransaction(deferred: false);
            V5bAssertSessionOwner(replayTx, checkpoint.Job.SessionId, ownership);
            V5cAssertJobNotStopped(replayTx, jobId);
            replayTx.Commit();
            return;
        }

        var committed = V5aCommitted(checkpoint.Job);
        foreach (var packet in ordered)
        {
            var claim = checkpoint.Claims.Single(c =>
                c.ClaimId == packet.Content.ClaimId);
            // Only FIRST freeze checks host-issued evidence at the original
            // boundary. A sealed packet is never rebuilt from later retrieval.
            var selected = SelectVerifierEvidence(checkpoint.Job, committed,
                V5aEffectiveCategory(claim), maxItems: 32);
            EvidenceVerificationValidator.ValidatePacket(packet, claim,
                checkpoint.Job, committed, selected.Scope);
            if (packet.Content.Completeness.Status ==
                    EvidenceCompletenessStatus.CompleteForDeclaredScope &&
                (selected.Completeness.Status !=
                    EvidenceCompletenessStatus.CompleteForDeclaredScope ||
                 packet.Content.Items.Count != selected.Scope.AuthorizedItems.Count))
                throw new InvalidDataException("V5a packet falsely claims complete evidence scope.");
        }

        using var tx = _connection.BeginTransaction(deferred: false);
        V5bAssertSessionOwner(tx, checkpoint.Job.SessionId, ownership);
        V5cAssertJobNotStopped(tx, jobId);
        if (V5aStageIsSame(tx, jobId, "packets:sealed", snapshot))
        {
            tx.Commit();
            return;
        }
        V5aRequireOpen(tx, jobId);
        if (!V5aHasStage(tx, jobId, V5aLedgerStage.ClaimsSealed) ||
            V5aHasStage(tx, jobId, V5aLedgerStage.PacketsSealed))
            throw new InvalidDataException("V5a packet stage order or idempotency failed.");

        foreach (var packet in ordered)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO verifier_v5a_packets
                    (packet_id, job_id, claim_id, canonical_json,
                     packet_sha256, completeness, retrieval_version, prompt_version)
                VALUES (@id, @job, @claim, @json, @sha,
                        @complete, @retrieval, @prompt);
                """;
            cmd.Parameters.AddWithValue("@id", V5aId(packet.PacketId));
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            cmd.Parameters.AddWithValue("@claim", V5aId(packet.Content.ClaimId));
            cmd.Parameters.AddWithValue("@json", packet.CanonicalJson);
            cmd.Parameters.AddWithValue("@sha", packet.Sha256);
            cmd.Parameters.AddWithValue("@complete",
                packet.Content.Completeness.Status.ToString());
            cmd.Parameters.AddWithValue("@retrieval",
                packet.Content.RetrievalSelectorVersion);
            cmd.Parameters.AddWithValue("@prompt", packet.Content.PromptVersion);
            cmd.ExecuteNonQuery();
        }
        V5aAppendEvent(tx, jobId, "packets:sealed",
            V5aLedgerStage.PacketsSealed, snapshot);
        tx.Commit();
    }

    /// <summary>
    /// Bind a previously reserved, charged V4 request to a job and exact
    /// claim-group before using its result. The ledger NEVER reserves another
    /// inference attempt, refunds it or reuses an existing request ID.
    /// </summary>
    public void LinkV5aChargedAttempt(
        Guid jobId, Guid requestId, V4AttemptPhase phase,
        IReadOnlyList<Guid> claimIds, V5bOwnership? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(claimIds);
        if (requestId == Guid.Empty ||
            !Enum.IsDefined(typeof(V4AttemptPhase), phase) ||
            claimIds.Count > EvidenceVerifierV4.MaxClaimsPerBatch ||
            claimIds.Count != claimIds.Distinct().Count() ||
            claimIds.Any(id => id == Guid.Empty) ||
            (phase == V4AttemptPhase.ClaimExtraction && claimIds.Count != 0) ||
            (phase == V4AttemptPhase.ClaimVerification && claimIds.Count == 0))
            throw new InvalidDataException("V5a charged attempt has invalid phase or claim group.");

        var checkpoint = ReadV5aCheckpoint(jobId);
        if (phase == V4AttemptPhase.ClaimVerification &&
            !checkpoint.Events.Any(e => e.Stage == V5aLedgerStage.PacketsSealed))
            throw new InvalidDataException(
                "V5a verification attempts require frozen packets.");
        foreach (var id in claimIds)
            if (!checkpoint.Packets.Any(p => p.Content.ClaimId == id))
                throw new InvalidDataException(
                    "V5a cannot link verification attempts to unpackaged claims.");

        var ordered = claimIds.OrderBy(id => id).ToArray();
        var payload = V5aJson(new V5aAttemptLink(
            requestId, phase, Array.AsReadOnly(ordered)));
        var idsJson = V5aJson(ordered);
        var key = "attempt:" + V5aId(requestId);

        using var tx = _connection.BeginTransaction(deferred: false);
        V5bAssertSessionOwner(tx, checkpoint.Job.SessionId, ownership);
        V5cAssertJobNotStopped(tx, jobId);
        V5aVerifyChargedRequest(tx, checkpoint.Job, requestId, phase);
        // Never attach an attempt from a superseded owner epoch to a new
        // checkpoint event, even if the old attempt remains durably charged.
        V5bAssertAttemptEpoch(tx, checkpoint.Job.SessionId, requestId, ownership);
        if (V5aStageIsSame(tx, jobId, key, payload))
        {
            tx.Commit();
            return;
        }
        V5aRequireOpen(tx, jobId);

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO verifier_v5a_attempt_links
                    (request_id, job_id, phase, claim_ids_json, claim_ids_sha256)
                VALUES (@request, @job, @phase, @claims, @hash);
                """;
            cmd.Parameters.AddWithValue("@request", V5aId(requestId));
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            cmd.Parameters.AddWithValue("@phase", phase.ToString());
            cmd.Parameters.AddWithValue("@claims", idsJson);
            cmd.Parameters.AddWithValue("@hash", V5aHash(idsJson));
            cmd.ExecuteNonQuery();
        }
        V5aAppendEvent(tx, jobId, key,
            V5aLedgerStage.AttemptLinked, payload);
        tx.Commit();
    }

    public void RecordV5aClaimResult(Guid jobId, VerificationClaimResult result,
        Guid? verificationRequestId = null, V5bOwnership? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var checkpoint = ReadV5aCheckpoint(jobId);
        if (!checkpoint.Events.Any(e => e.Stage == V5aLedgerStage.PacketsSealed))
            throw new InvalidDataException("V5a cannot record results before packet freeze.");

        var claim = checkpoint.Claims.SingleOrDefault(c => c.ClaimId == result.ClaimId)
            ?? throw new InvalidDataException("V5a result references an unknown claim.");
        if (result.Status == VerificationOperationalStatus.Completed)
        {
            var packet = checkpoint.Packets.SingleOrDefault(p =>
                p.Content.ClaimId == claim.ClaimId &&
                p.PacketId == result.Judgment?.PacketId)
                ?? throw new InvalidDataException(
                    "Completed V5a result requires the original frozen claim packet.");
            EvidenceVerificationValidator.ValidateClaimResult(
                result, packet, claim, checkpoint.Job, V5aCommitted(checkpoint.Job),
                V5aFrozenScope(checkpoint.Job, packet));
        }
        else if (result.Judgment is not null ||
                 result.FailureDetail is { Length: > 512 } ||
                 !Enum.IsDefined(typeof(VerificationOperationalStatus), result.Status))
            throw new InvalidDataException(
                "V5a operational failure cannot contain an epistemic judgment.");

        if (verificationRequestId is { } charged)
        {
            if (!checkpoint.AttemptLinks.Any(link =>
                link.RequestId == charged &&
                link.Phase == V4AttemptPhase.ClaimVerification &&
                link.ClaimIds.Contains(claim.ClaimId)))
                throw new InvalidDataException(
                    "V5a result is not linked to its exact charged verification claim group.");
            if (result.Status == VerificationOperationalStatus.Completed &&
                !V5aChargedRequestCompleted(charged))
                throw new InvalidDataException(
                    "V5a completed verdict requires a finalized, charged model request.");
        }
        else if (!V5aCanRecordWithoutModel(result, claim,
                     checkpoint.Packets.SingleOrDefault(p =>
                         p.Content.ClaimId == claim.ClaimId)))
            throw new InvalidDataException(
                "Model-dependent V5a result requires a charged verification attempt link.");

        var json = V5aJson(result);
        var eventJson = V5aJson(new V5aResultEnvelope(
            result, verificationRequestId));
        var key = "result:" + V5aId(result.ClaimId);
        using var tx = _connection.BeginTransaction(deferred: false);
        V5bAssertSessionOwner(tx, checkpoint.Job.SessionId, ownership);
        V5cAssertJobNotStopped(tx, jobId);
        // A later owner may inspect an old charged request, but must not
        // author a NEW judgment against that earlier inference's identity.
        if (verificationRequestId is { } request)
            V5bAssertAttemptEpoch(tx, checkpoint.Job.SessionId, request, ownership);
        if (V5aStageIsSame(tx, jobId, key, eventJson))
        {
            tx.Commit();
            return;
        }
        V5aRequireOpen(tx, jobId);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO verifier_v5a_results
                    (job_id, claim_id, operational_status, packet_id,
                     attempt_request_id, result_json, result_sha256, judgment_schema)
                VALUES (@job, @claim, @status, @packet,
                        @request, @json, @hash, @schema);
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            cmd.Parameters.AddWithValue("@claim", V5aId(claim.ClaimId));
            cmd.Parameters.AddWithValue("@status", result.Status.ToString());
            cmd.Parameters.AddWithValue("@packet",
                (object?)result.Judgment?.PacketId.ToString("D") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@request",
                (object?)verificationRequestId?.ToString("D") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@json", json);
            cmd.Parameters.AddWithValue("@hash", V5aHash(json));
            cmd.Parameters.AddWithValue("@schema",
                checkpoint.PinnedPolicy.VerificationSchemaVersion);
            cmd.ExecuteNonQuery();
        }
        V5aAppendEvent(tx, jobId, key,
            V5aLedgerStage.ClaimResultRecorded, eventJson);
        tx.Commit();
    }

    public void SealV5aSummary(Guid jobId, VerificationObservationSummary summary,
        V5bOwnership? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var checkpoint = ReadV5aCheckpoint(jobId);
        if (!checkpoint.Events.Any(e => e.Stage == V5aLedgerStage.PacketsSealed) ||
            checkpoint.ClaimResults.Count != checkpoint.Claims.Count ||
            summary.JobId != jobId)
            throw new InvalidDataException(
                "V5a terminal summary requires explicit disposition of every identified claim.");
        EvidenceVerificationValidator.ValidateSummary(summary);
        // The read-side audit checks every count and outcome against the
        // immutable result rows, including all incomplete/unverified statuses.
        V5aValidateSummaryCounts(checkpoint, summary);
        var json = V5aJson(summary);

        using var tx = _connection.BeginTransaction(deferred: false);
        V5bAssertSessionOwner(tx, checkpoint.Job.SessionId, ownership);
        V5cAssertJobNotStopped(tx, jobId);
        if (V5aStageIsSame(tx, jobId, "summary:sealed", json))
        {
            tx.Commit();
            return;
        }
        V5aRequireOpen(tx, jobId);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO verifier_v5a_summaries
                    (job_id, summary_json, summary_sha256, sealed_at)
                VALUES (@job, @json, @hash, @at);
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            cmd.Parameters.AddWithValue("@json", json);
            cmd.Parameters.AddWithValue("@hash", V5aHash(json));
            cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        V5aAppendEvent(tx, jobId, "summary:sealed",
            V5aLedgerStage.SummarySealed, json);
        tx.Commit();
    }

    private bool V5aChargedRequestCompleted(Guid requestId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT status FROM moderator_inference_reservations
            WHERE request_id = @request;
            """;
        cmd.Parameters.AddWithValue("@request", V5aId(requestId));
        return cmd.ExecuteScalar() is string state &&
            state == ModerationAttemptStatus.Completed.ToString();
    }

    private static bool V5aCanRecordWithoutModel(
        VerificationClaimResult result, ClaimCandidate claim, EvidencePacket? packet)
    {
        if (result.Status != VerificationOperationalStatus.Completed)
            return true; // error, skip, and no-call results remain operational, not truth claims

        if (result.Judgment?.Outcome == ClaimVerificationOutcome.NoMaterialIssue &&
            claim.RhetoricalMode != ClaimRhetoricalMode.Asserted)
            return true;

        if (result.Judgment?.Outcome == ClaimVerificationOutcome.InsufficientEvidence &&
            packet is not null &&
            (packet.Content.Items.Count == 0 ||
             packet.Content.Completeness.Status == EvidenceCompletenessStatus.Unknown))
            return true;

        return false;
    }

    private Utterance V5aCommitted(VerificationJobIdentity job) =>
        GetCommittedUtterances(job.SessionId)
            .SingleOrDefault(u => u.Id == job.UtteranceId)
            ?? throw new InvalidDataException("V5a committed utterance is missing.");

    private static ClaimCategory V5aEffectiveCategory(ClaimCandidate claim) =>
        (claim.Category is ClaimCategory.CharacterIdentity or ClaimCategory.PersonalHistory) &&
        !Regex.IsMatch(claim.ClaimText,
            @"^\s*(?:I|I've|I'm|my|mine|we|our)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? ClaimCategory.Other : claim.Category;

    private void V5aVerifyChargedRequest(
        SqliteTransaction? tx, VerificationJobIdentity job,
        Guid requestId, V4AttemptPhase phase)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT a.session_id, a.opportunity_id, a.purpose,
                   p.job_id, p.utterance_id, p.phase, p.policy_version
            FROM moderator_inference_reservations AS a
            JOIN v4_verifier_attempt_phases AS p
              ON p.request_id = a.request_id
            WHERE a.request_id = @request;
            """;
        cmd.Parameters.AddWithValue("@request", V5aId(requestId));
        using var row = cmd.ExecuteReader();
        var purpose = phase == V4AttemptPhase.ClaimExtraction
            ? ModerationInferencePurpose.VerifierClaimExtraction.ToString()
            : ModerationInferencePurpose.VerifierClaimVerification.ToString();
        if (!row.Read() ||
            row.GetString(0) != job.SessionId.ToString() ||
            row.IsDBNull(1) ||
            row.GetString(1) != job.OpportunityId.ToString() ||
            row.GetString(2) != purpose ||
            row.GetString(3) != V5aId(job.JobId) ||
            row.GetString(4) != job.UtteranceId.ToString() ||
            row.GetString(5) != phase.ToString() ||
            row.GetString(6) != EvidenceVerifierV4.ContractVersion)
            throw new InvalidDataException(
                "V5a cannot link a foreign, uncharged or different-phase inference request.");
    }

    private static void V5aValidateSummaryCounts(
        V5aLedgerCheckpoint checkpoint, VerificationObservationSummary summary)
    {
        var all = checkpoint.ClaimResults;
        if (summary.IdentifiedClaims != checkpoint.Claims.Count ||
            summary.VerifiedClaims != all.Count(r =>
                r.Status == VerificationOperationalStatus.Completed) ||
            summary.ConfirmedContradictions != all.Count(r =>
                r.Judgment?.Outcome == ClaimVerificationOutcome.ConfirmedContradiction) ||
            summary.NoMaterialIssues != all.Count(r =>
                r.Judgment?.Outcome == ClaimVerificationOutcome.NoMaterialIssue) ||
            summary.InsufficientEvidence != all.Count(r =>
                r.Judgment?.Outcome == ClaimVerificationOutcome.InsufficientEvidence) ||
            summary.ConflictingEvidence != all.Count(r =>
                r.Judgment?.Outcome == ClaimVerificationOutcome.ConflictingEvidence) ||
            summary.SkippedForBudget != all.Count(r =>
                r.Status == VerificationOperationalStatus.SkippedForBudget) ||
            summary.FailedClaims != all.Count(r =>
                r.Status != VerificationOperationalStatus.Completed &&
                r.Status != VerificationOperationalStatus.SkippedForBudget))
            throw new InvalidDataException(
                "V5a summary cannot omit unresolved claims or change deterministic counts.");
    }
}

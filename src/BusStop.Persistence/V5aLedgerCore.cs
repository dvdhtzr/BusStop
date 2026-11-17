using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Domain;
using BusStop.Moderation.Verification;
using Microsoft.Data.Sqlite;

namespace BusStop.Persistence;

public sealed partial class ConversationStore
{
    private sealed record V5aRegistrationEnvelope(
        VerificationJobIdentity Job, string EvaluationKey, string PolicySha256);
    private sealed record V5aEventEnvelope(
        Guid JobId, long Sequence, string EventKey, string Stage,
        string PayloadSha256, string? PreviousSha256);
    private sealed record V5aPacketEnvelope(
        Guid PacketId, Guid ClaimId, string PacketSha256);
    private sealed record V5aResultEnvelope(
        VerificationClaimResult Result, Guid? VerificationRequestId);

    private static string V5aJson<T>(T value) => JsonSerializer.Serialize(value);

    private static string V5aHash(string content) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string V5aId(Guid id) => id.ToString("D");

    /// <summary>Detect future/unknown ledger versions BEFORE running additive DDL.</summary>
    private void EnsureV5aLedgerSchemaCompatible()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE name = 'verifier_v5a_schema' AND type = 'table';
            """;
        var present = (long)cmd.ExecuteScalar()! == 1;
        if (!present)
        {
            // An incomplete/downgraded ledger cannot be treated as a new install.
            cmd.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE name LIKE 'verifier_v5a_%' AND type = 'table';
                """;
            if ((long)cmd.ExecuteScalar()! != 0)
                throw new InvalidDataException(
                    "V5a ledger tables exist without a version marker; refuse migration.");
            return;
        }
        cmd.CommandText = """
            SELECT version FROM verifier_v5a_schema WHERE singleton = 1;
            """;
        var version = cmd.ExecuteScalar() as string;
        if (version != V5aLedgerSchema.Version)
            throw new InvalidDataException(
                "Incompatible or missing V5a ledger schema version; refuse downgrade.");
        cmd.CommandText = "SELECT COUNT(*) FROM verifier_v5a_schema;";
        if ((long)cmd.ExecuteScalar()! != 1)
            throw new InvalidDataException("Ambiguous V5a ledger version markers.");
    }

    /// <summary>
    /// Register only a persisted, unchanged committed utterance and a frozen
    /// opt-in V4 session policy. Duplicate exact registration is idempotent;
    /// a different job for the same utterance/evaluation is rejected.
    /// </summary>
    public void RegisterV5aJob(
        VerificationJobIdentity job, string evaluationKey)
    {
        if (job is null ||
            string.IsNullOrWhiteSpace(evaluationKey) ||
            evaluationKey.Length > 128 ||
            !evaluationKey.StartsWith(V5aLedgerContract.EvaluationPrefix,
                StringComparison.Ordinal))
            throw new InvalidDataException("Invalid V5a job or evaluation identity.");

        EnsureV5aLedgerSchemaCompatible();
        var committed = GetCommittedUtterances(job.SessionId)
            .SingleOrDefault(x => x.Id == job.UtteranceId)
            ?? throw new InvalidDataException("V5a registration requires committed speech.");
        EvidenceVerificationValidator.ValidateJob(job, committed);
        ValidateV4RecordedOpportunityBoundary(job);

        var policy = GetV4VerifierPolicy(job.SessionId)
            ?? throw new InvalidOperationException(
                "V5a requires an explicitly opted-in V4 shadow verifier policy.");
        policy.Validate();
        var policyJson = V5aJson(policy);
        var policyHash = V5aHash(policyJson);
        var registration = V5aJson(new V5aRegistrationEnvelope(job, evaluationKey, policyHash));

        using var tx = _connection.BeginTransaction(deferred: false);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT session_id, opportunity_id, utterance_id, speaker_id,
                       committed_boundary, utterance_sha256, job_contract,
                       evaluation_key, policy_json, policy_sha256
                FROM verifier_v5a_jobs WHERE job_id = @job;
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(job.JobId));
            using var row = cmd.ExecuteReader();
            if (row.Read())
            {
                bool same = row.GetString(0) == job.SessionId.ToString() &&
                    row.GetString(1) == job.OpportunityId.ToString() &&
                    row.GetString(2) == job.UtteranceId.ToString() &&
                    row.GetString(3) == job.SpeakerId.ToString() &&
                    row.GetInt64(4) == job.CommittedBoundary &&
                    row.GetString(5) == job.UtteranceSha256 &&
                    row.GetString(6) == job.ContractVersion &&
                    row.GetString(7) == evaluationKey &&
                    row.GetString(8) == policyJson &&
                    row.GetString(9) == policyHash;
                row.Close();
                if (!same || !V5aStageIsSame(
                        tx, job.JobId, "registered", registration))
                    throw new InvalidDataException(
                        "V5a job ID reused with altered identity, pins or registration event.");
                tx.Commit();
                _ = ReadV5aCheckpoint(job.JobId); // verify durable chain on replay
                return;
            }
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO verifier_v5a_jobs
                    (job_id, session_id, opportunity_id, utterance_id,
                     speaker_id, committed_boundary, utterance_sha256,
                     job_contract, evaluation_key, policy_json,
                     policy_sha256, registered_at)
                VALUES (@job, @session, @opportunity, @utterance, @speaker,
                        @boundary, @hash, @contract, @evaluation,
                        @policy, @policyhash, @at);
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(job.JobId));
            cmd.Parameters.AddWithValue("@session", job.SessionId.ToString());
            cmd.Parameters.AddWithValue("@opportunity", job.OpportunityId.ToString());
            cmd.Parameters.AddWithValue("@utterance", job.UtteranceId.ToString());
            cmd.Parameters.AddWithValue("@speaker", job.SpeakerId.ToString());
            cmd.Parameters.AddWithValue("@boundary", job.CommittedBoundary);
            cmd.Parameters.AddWithValue("@hash", job.UtteranceSha256);
            cmd.Parameters.AddWithValue("@contract", job.ContractVersion);
            cmd.Parameters.AddWithValue("@evaluation", evaluationKey);
            cmd.Parameters.AddWithValue("@policy", policyJson);
            cmd.Parameters.AddWithValue("@policyhash", policyHash);
            cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        V5aAppendEvent(tx, job.JobId, "registered",
            V5aLedgerStage.Registered, registration);
        tx.Commit();
    }

    /// <summary>
    /// Read and validate only frozen durable data. No source retrieval happens
    /// here, and there is no model dispatch or auto-recovery.
    /// </summary>
    public V5aLedgerCheckpoint ReadV5aCheckpoint(Guid jobId)
    {
        if (jobId == Guid.Empty)
            throw new InvalidDataException("V5a checkpoint needs a job ID.");
        EnsureV5aLedgerSchemaCompatible();

        VerificationJobIdentity job;
        string evaluation, policyJson, policySha;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT session_id, opportunity_id, utterance_id, speaker_id,
                       committed_boundary, utterance_sha256, job_contract,
                       evaluation_key, policy_json, policy_sha256
                FROM verifier_v5a_jobs WHERE job_id = @job;
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            using var row = cmd.ExecuteReader();
            if (!row.Read())
                throw new InvalidDataException("V5a job is not durably registered.");
            job = new VerificationJobIdentity(
                jobId, SessionId.Parse(row.GetString(0)),
                OpportunityId.Parse(row.GetString(1)),
                UtteranceId.Parse(row.GetString(2)),
                CharacterId.Parse(row.GetString(3)),
                row.GetInt64(4), row.GetString(6), row.GetString(5));
            evaluation = row.GetString(7);
            policyJson = row.GetString(8);
            policySha = row.GetString(9);
        }
        if (!evaluation.StartsWith(V5aLedgerContract.EvaluationPrefix,
                StringComparison.Ordinal) || evaluation.Length > 128 ||
            policySha != V5aHash(policyJson))
            throw new InvalidDataException("V5a job evaluation or policy hash has changed.");
        var policy = JsonSerializer.Deserialize<V4SessionPolicy>(policyJson)
            ?? throw new InvalidDataException("V5a policy payload is missing.");
        policy.Validate();
        if (V5aJson(policy) != policyJson ||
            GetV4VerifierPolicy(job.SessionId) != policy)
            throw new InvalidDataException(
                "V5a pinned session policy changed or is incompatible.");

        var committed = GetCommittedUtterances(job.SessionId)
            .SingleOrDefault(x => x.Id == job.UtteranceId)
            ?? throw new InvalidDataException("V5a observed utterance was not found.");
        EvidenceVerificationValidator.ValidateJob(job, committed);
        ValidateV4RecordedOpportunityBoundary(job);

        var events = V5aReadEvents(jobId);
        if (events.Count < 1 || events[0].Stage != V5aLedgerStage.Registered ||
            events[0].EventKey != "registered" ||
            events[0].PayloadJson != V5aJson(
                new V5aRegistrationEnvelope(job, evaluation, policySha)))
            throw new InvalidDataException("V5a registration event is missing or incompatible.");
        var sealedClaims = events.SingleOrDefault(e =>
            e.Stage == V5aLedgerStage.ClaimsSealed);
        var sealedPackets = events.SingleOrDefault(e =>
            e.Stage == V5aLedgerStage.PacketsSealed);
        var sealedSummary = events.SingleOrDefault(e =>
            e.Stage == V5aLedgerStage.SummarySealed);

        var claims = new List<ClaimCandidate>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT claim_id, claim_index, claim_json, claim_sha256
                FROM verifier_v5a_claims WHERE job_id=@job ORDER BY claim_index;
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                var json = rows.GetString(2);
                if (V5aHash(json) != rows.GetString(3))
                    throw new InvalidDataException("V5a claim payload hash mismatch.");
                var claim = JsonSerializer.Deserialize<ClaimCandidate>(json)
                    ?? throw new InvalidDataException("Invalid persisted V5a claim.");
                if (claim.ClaimId.ToString("D") != rows.GetString(0) ||
                    claim.Index != rows.GetInt32(1) || V5aJson(claim) != json)
                    throw new InvalidDataException("V5a claim identity or serializer mismatch.");
                claims.Add(claim);
            }
        }
        if (claims.Count > V5aLedgerContract.MaxClaims ||
            (sealedClaims is null && claims.Count > 0) ||
            (sealedClaims is not null &&
             sealedClaims.PayloadJson != V5aJson(claims.ToArray())))
            throw new InvalidDataException("V5a extracted-claim snapshot is incomplete or changed.");
        if (sealedClaims is not null)
        {
            EvidenceVerificationValidator.ValidateClaims(claims, job, committed);
            for (int i = 0; i < claims.Count; i++)
                if (claims[i].Index != i ||
                    claims[i].ValidationStatus != ClaimValidationStatus.HostValidated)
                    throw new InvalidDataException("V5a extracted claim order/status mismatch.");
        }

        var packets = new List<EvidencePacket>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT packet_id, claim_id, canonical_json, packet_sha256,
                       completeness, retrieval_version, prompt_version
                FROM verifier_v5a_packets WHERE job_id = @job
                ORDER BY claim_id;
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                var bodyJson = rows.GetString(2);
                var body = JsonSerializer.Deserialize<EvidencePacketContent>(bodyJson)
                    ?? throw new InvalidDataException("V5a packet body is absent.");
                var packet = EvidencePacketFactory.Freeze(
                    Guid.Parse(rows.GetString(0)), body);
                if (packet.CanonicalJson != bodyJson ||
                    packet.Sha256 != rows.GetString(3) ||
                    body.ClaimId.ToString("D") != rows.GetString(1) ||
                    body.Completeness.Status.ToString() != rows.GetString(4) ||
                    body.RetrievalSelectorVersion != rows.GetString(5) ||
                    body.PromptVersion != rows.GetString(6) ||
                    body.JobId != jobId || body.AsOfBoundary != job.CommittedBoundary ||
                    !claims.Any(c => c.ClaimId == body.ClaimId))
                    throw new InvalidDataException("V5a packet changed or no longer matches its claim.");
                packets.Add(packet);
            }
        }
        var packetEnvelope = packets
            .OrderBy(p => p.Content.ClaimId.ToString("D"), StringComparer.Ordinal)
            .Select(p => new V5aPacketEnvelope(
                p.PacketId, p.Content.ClaimId, p.Sha256)).ToArray();
        if (packets.Count > V5aLedgerContract.MaxPackets ||
            (sealedPackets is null && packets.Count > 0) ||
            (sealedPackets is not null &&
             sealedPackets.PayloadJson != V5aJson(packetEnvelope)))
            throw new InvalidDataException("V5a frozen packet snapshot is incomplete or changed.");

        var links = new List<V5aAttemptLink>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT request_id, phase, claim_ids_json, claim_ids_sha256
                FROM verifier_v5a_attempt_links WHERE job_id=@job
                ORDER BY request_id;
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                var idsJson = rows.GetString(2);
                if (V5aHash(idsJson) != rows.GetString(3))
                    throw new InvalidDataException("V5a charged request-group hash mismatch.");
                var ids = JsonSerializer.Deserialize<Guid[]>(idsJson)
                    ?? throw new InvalidDataException("V5a charged request-group is absent.");
                if (V5aJson(ids) != idsJson ||
                    !Enum.TryParse<V4AttemptPhase>(rows.GetString(1), false, out var phase) ||
                    !Enum.IsDefined(typeof(V4AttemptPhase), phase))
                    throw new InvalidDataException("V5a attempt group is incompatible.");
                links.Add(new V5aAttemptLink(
                    Guid.Parse(rows.GetString(0)), phase, Array.AsReadOnly(ids)));
            }
        }

        foreach (var link in links)
            V5aVerifyChargedRequest(null, job, link.RequestId, link.Phase);

        var results = new List<VerificationClaimResult>();
        var resultLinks = new List<V5aClaimResultLink>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT claim_id, operational_status, packet_id, attempt_request_id,
                       result_json, result_sha256, judgment_schema
                FROM verifier_v5a_results WHERE job_id=@job ORDER BY claim_id;
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                var json = rows.GetString(4);
                var attemptRequestId = rows.IsDBNull(3)
                    ? (Guid?)null : Guid.Parse(rows.GetString(3));
                if (V5aHash(json) != rows.GetString(5) ||
                    rows.GetString(6) != policy.VerificationSchemaVersion)
                    throw new InvalidDataException("V5a claim-result hash or schema mismatch.");
                var result = JsonSerializer.Deserialize<VerificationClaimResult>(json)
                    ?? throw new InvalidDataException("V5a claim result is absent.");
                if (V5aJson(result) != json ||
                    result.ClaimId.ToString("D") != rows.GetString(0) ||
                    result.Status.ToString() != rows.GetString(1) ||
                    (result.Judgment?.PacketId.ToString("D")) !=
                    (rows.IsDBNull(2) ? null : rows.GetString(2)) ||
                    !claims.Any(c => c.ClaimId == result.ClaimId) ||
                    (result.Status == VerificationOperationalStatus.Completed) !=
                    (result.Judgment is not null))
                    throw new InvalidDataException("V5a result is inconsistent with its claim.");
                if (result.Status == VerificationOperationalStatus.Completed)
                {
                    var packet = packets.SingleOrDefault(p =>
                        p.PacketId == result.Judgment!.PacketId &&
                        p.Content.ClaimId == result.ClaimId)
                        ?? throw new InvalidDataException("V5a judgment's frozen packet is missing.");
                    var scope = V5aFrozenScope(job, packet);
                    EvidenceVerificationValidator.ValidateClaimResult(
                        result, packet, claims.Single(c => c.ClaimId == result.ClaimId),
                        job, committed, scope);
                }
                else if (result.FailureDetail is { Length: > 512 })
                    throw new InvalidDataException("V5a result error detail is too long.");
                var resultClaim = claims.Single(c => c.ClaimId == result.ClaimId);
                var resultPacket = packets.SingleOrDefault(p =>
                    p.Content.ClaimId == result.ClaimId);
                if (attemptRequestId is { } requestId)
                {
                    var link = links.SingleOrDefault(x =>
                        x.RequestId == requestId &&
                        x.Phase == V4AttemptPhase.ClaimVerification &&
                        x.ClaimIds.Contains(result.ClaimId));
                    if (link is null)
                        throw new InvalidDataException(
                            "V5a claim result refers to a foreign or unlinked inference attempt.");
                }
                else if (!V5aCanRecordWithoutModel(result, resultClaim, resultPacket))
                    throw new InvalidDataException(
                        "V5a model-backed claim result lacks a charged attempt link.");
                results.Add(result);
                resultLinks.Add(new V5aClaimResultLink(result.ClaimId, attemptRequestId));
            }
        }

        VerificationObservationSummary? summary = null;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT summary_json, summary_sha256 FROM verifier_v5a_summaries
                WHERE job_id=@job;
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            using var row = cmd.ExecuteReader();
            if (row.Read())
            {
                var json = row.GetString(0);
                if (V5aHash(json) != row.GetString(1))
                    throw new InvalidDataException("V5a terminal summary hash mismatch.");
                summary = JsonSerializer.Deserialize<VerificationObservationSummary>(json)
                    ?? throw new InvalidDataException("V5a terminal summary is absent.");
                if (V5aJson(summary) != json || summary.JobId != jobId)
                    throw new InvalidDataException("V5a terminal summary identity changed.");
                EvidenceVerificationValidator.ValidateSummary(summary);
            }
        }

        V5aValidateStageAndRelations(events, claims, packets,
            links, results, resultLinks, summary,
            sealedClaims, sealedPackets, sealedSummary);
        return new V5aLedgerCheckpoint(job, evaluation, policy, policySha,
            Array.AsReadOnly(events.ToArray()),
            Array.AsReadOnly(claims.ToArray()),
            Array.AsReadOnly(packets.ToArray()),
            Array.AsReadOnly(links.ToArray()),
            Array.AsReadOnly(results.ToArray()),
            summary)
        {
            ResultLinks = Array.AsReadOnly(resultLinks.ToArray())
        };
    }

    private static VerificationEvidenceScope V5aFrozenScope(
        VerificationJobIdentity job, EvidencePacket packet) =>
        new(job.SessionId, job.OpportunityId, job.SpeakerId,
            job.CommittedBoundary,
            packet.Content.Items.ToDictionary(i => i.ReferenceId,
                StringComparer.Ordinal));

    private List<V5aLedgerEvent> V5aReadEvents(Guid jobId)
    {
        var events = new List<V5aLedgerEvent>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT sequence, event_key, stage, payload_json, payload_sha256,
                   previous_sha256, event_sha256, recorded_at
            FROM verifier_v5a_events WHERE job_id=@job ORDER BY sequence;
            """;
        cmd.Parameters.AddWithValue("@job", V5aId(jobId));
        using var rows = cmd.ExecuteReader();
        string? previous = null;
        while (rows.Read())
        {
            var sequence = rows.GetInt64(0);
            var key = rows.GetString(1);
            var stageText = rows.GetString(2);
            var payload = rows.GetString(3);
            var payloadSha = rows.GetString(4);
            var predecessor = rows.IsDBNull(5) ? null : rows.GetString(5);
            var eventHash = rows.GetString(6);
            if (!Enum.TryParse<V5aLedgerStage>(stageText, false, out var stage) ||
                !Enum.IsDefined(typeof(V5aLedgerStage), stage) ||
                sequence != events.Count + 1 || previous != predecessor ||
                V5aHash(payload) != payloadSha ||
                V5aHash(V5aJson(new V5aEventEnvelope(jobId, sequence, key,
                    stageText, payloadSha, predecessor))) != eventHash ||
                sequence > V5aLedgerContract.MaxStageEvents)
                throw new InvalidDataException("V5a event chain or payload hash mismatch.");
            events.Add(new V5aLedgerEvent(jobId, sequence, key, stage,
                payload, payloadSha, predecessor, eventHash,
                DateTimeOffset.Parse(rows.GetString(7))));
            previous = eventHash;
        }
        return events;
    }

    private static void V5aValidateStageAndRelations(
        IReadOnlyList<V5aLedgerEvent> events,
        IReadOnlyList<ClaimCandidate> claims,
        IReadOnlyList<EvidencePacket> packets,
        IReadOnlyList<V5aAttemptLink> links,
        IReadOnlyList<VerificationClaimResult> results,
        IReadOnlyList<V5aClaimResultLink> resultLinks,
        VerificationObservationSummary? summary,
        V5aLedgerEvent? claimsEvent, V5aLedgerEvent? packetsEvent,
        V5aLedgerEvent? summaryEvent)
    {
        if (events.Count == 0 ||
            events[0].Stage != V5aLedgerStage.Registered ||
            events.Count(e => e.Stage == V5aLedgerStage.Registered) != 1 ||
            events.Count(e => e.Stage == V5aLedgerStage.ClaimsSealed) > 1 ||
            events.Count(e => e.Stage == V5aLedgerStage.PacketsSealed) > 1 ||
            events.Count(e => e.Stage == V5aLedgerStage.SummarySealed) > 1 ||
            (claimsEvent is null && packetsEvent is not null) ||
            (packetsEvent is null && (results.Count > 0 || summary is not null)) ||
            (summary is null) != (summaryEvent is null))
            throw new InvalidDataException("V5a ledger contains an illegal stage transition.");

        if (summaryEvent is not null &&
            (events[^1].Stage != V5aLedgerStage.SummarySealed ||
             summaryEvent.PayloadJson != V5aJson(summary)))
            throw new InvalidDataException("V5a terminal summary event was altered.");

        var attemptsById = links.ToDictionary(x => x.RequestId);
        var resultsById = results.ToDictionary(x => x.ClaimId);
        var linksSeen = 0;
        var resultsSeen = 0;
        foreach (var e in events.Skip(1))
        {
            if (summaryEvent is not null && e.Sequence > summaryEvent.Sequence)
                throw new InvalidDataException("V5a ledger continued after terminal summary.");

            switch (e.Stage)
            {
                case V5aLedgerStage.Registered:
                    throw new InvalidDataException("Duplicate V5a job registration.");
                case V5aLedgerStage.ClaimsSealed:
                    if (packetsEvent is not null && e.Sequence > packetsEvent.Sequence ||
                        e.EventKey != "claims:sealed")
                        throw new InvalidDataException("V5a claims sealed after packet freeze.");
                    break;
                case V5aLedgerStage.PacketsSealed:
                    if (claimsEvent is null || claimsEvent.Sequence > e.Sequence ||
                        e.EventKey != "packets:sealed")
                        throw new InvalidDataException("V5a packets were frozen before claims.");
                    break;
                case V5aLedgerStage.AttemptLinked:
                    var attempt = JsonSerializer.Deserialize<V5aAttemptLink>(e.PayloadJson)
                        ?? throw new InvalidDataException("Invalid V5a attempt event.");
                    if (e.EventKey != "attempt:" + V5aId(attempt.RequestId) ||
                        !attemptsById.TryGetValue(attempt.RequestId, out var stored) ||
                        attempt.Phase != stored.Phase ||
                        !attempt.ClaimIds.SequenceEqual(stored.ClaimIds) ||
                        attempt.Phase == V4AttemptPhase.ClaimVerification &&
                        (packetsEvent is null || e.Sequence <= packetsEvent.Sequence))
                        throw new InvalidDataException("V5a request event is not linked to its charged phase.");
                    linksSeen++;
                    break;
                case V5aLedgerStage.ClaimResultRecorded:
                    var resultEnvelope = JsonSerializer.Deserialize<V5aResultEnvelope>(e.PayloadJson)
                        ?? throw new InvalidDataException("Missing V5a claim-result event.");
                    var result = resultEnvelope.Result;
                    if (result is null ||
                        packetsEvent is null || e.Sequence <= packetsEvent.Sequence ||
                        e.EventKey != "result:" + V5aId(result.ClaimId) ||
                        !resultsById.TryGetValue(result.ClaimId, out var storedResult) ||
                        !resultLinks.Any(link =>
                            link.ClaimId == result.ClaimId &&
                            link.VerificationRequestId == resultEnvelope.VerificationRequestId) ||
                        V5aJson(storedResult) != V5aJson(result))
                        throw new InvalidDataException("V5a claim-result event/row mismatch.");
                    resultsSeen++;
                    break;
                case V5aLedgerStage.SummarySealed:
                    if (results.Count != claims.Count ||
                        summary?.JobId != e.JobId)
                        throw new InvalidDataException("V5a summary omitted claim results.");
                    break;
            }
        }

        if (linksSeen != links.Count || resultsSeen != results.Count ||
            resultLinks.Count != results.Count ||
            (summary is not null && (summary.IdentifiedClaims != claims.Count ||
                summary.VerifiedClaims != results.Count(r =>
                    r.Status == VerificationOperationalStatus.Completed) ||
                summary.ConfirmedContradictions != results.Count(r =>
                    r.Judgment?.Outcome == ClaimVerificationOutcome.ConfirmedContradiction) ||
                summary.NoMaterialIssues != results.Count(r =>
                    r.Judgment?.Outcome == ClaimVerificationOutcome.NoMaterialIssue) ||
                summary.InsufficientEvidence != results.Count(r =>
                    r.Judgment?.Outcome == ClaimVerificationOutcome.InsufficientEvidence) ||
                summary.ConflictingEvidence != results.Count(r =>
                    r.Judgment?.Outcome == ClaimVerificationOutcome.ConflictingEvidence) ||
                summary.SkippedForBudget != results.Count(r =>
                    r.Status == VerificationOperationalStatus.SkippedForBudget) ||
                summary.FailedClaims != results.Count(r =>
                    r.Status != VerificationOperationalStatus.Completed &&
                    r.Status != VerificationOperationalStatus.SkippedForBudget))))
            throw new InvalidDataException("V5a stored summary or stage counts disagree.");
    }

    private bool V5aStageIsSame(
        SqliteTransaction tx, Guid jobId, string eventKey, string payloadJson)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT payload_json, payload_sha256 FROM verifier_v5a_events
            WHERE job_id=@job AND event_key=@key;
            """;
        cmd.Parameters.AddWithValue("@job", V5aId(jobId));
        cmd.Parameters.AddWithValue("@key", eventKey);
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return false;
        if (row.GetString(0) != payloadJson ||
            row.GetString(1) != V5aHash(payloadJson))
            throw new InvalidDataException(
                "V5a idempotency key was reused with a different payload.");
        return true;
    }

    private bool V5aHasStage(SqliteTransaction tx, Guid jobId, V5aLedgerStage stage)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COUNT(*) FROM verifier_v5a_events WHERE job_id=@job AND stage=@stage;
            """;
        cmd.Parameters.AddWithValue("@job", V5aId(jobId));
        cmd.Parameters.AddWithValue("@stage", stage.ToString());
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private void V5aRequireOpen(SqliteTransaction tx, Guid jobId)
    {
        if (V5aHasStage(tx, jobId, V5aLedgerStage.SummarySealed))
            throw new InvalidDataException("Terminal V5a job cannot accept additional events.");
    }

    private void V5aAppendEvent(
        SqliteTransaction tx, Guid jobId, string key,
        V5aLedgerStage stage, string payload)
    {
        long nextSequence = 1;
        string? previous = null;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT sequence, event_sha256 FROM verifier_v5a_events
                WHERE job_id=@job ORDER BY sequence DESC LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("@job", V5aId(jobId));
            using var row = cmd.ExecuteReader();
            if (row.Read())
            {
                nextSequence = checked(row.GetInt64(0) + 1);
                previous = row.GetString(1);
            }
        }
        if (nextSequence > V5aLedgerContract.MaxStageEvents)
            throw new InvalidDataException("V5a stage-event ceiling exceeded.");

        var payloadHash = V5aHash(payload);
        var eventHash = V5aHash(V5aJson(new V5aEventEnvelope(
            jobId, nextSequence, key, stage.ToString(), payloadHash, previous)));

        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO verifier_v5a_events
                (job_id, sequence, event_key, stage, payload_json,
                 payload_sha256, previous_sha256, event_sha256, recorded_at)
            VALUES (@job, @seq, @key, @stage, @json, @payloadhash,
                    @previous, @eventhash, @at);
            """;
        insert.Parameters.AddWithValue("@job", V5aId(jobId));
        insert.Parameters.AddWithValue("@seq", nextSequence);
        insert.Parameters.AddWithValue("@key", key);
        insert.Parameters.AddWithValue("@stage", stage.ToString());
        insert.Parameters.AddWithValue("@json", payload);
        insert.Parameters.AddWithValue("@payloadhash", payloadHash);
        insert.Parameters.AddWithValue("@previous", (object?)previous ?? DBNull.Value);
        insert.Parameters.AddWithValue("@eventhash", eventHash);
        insert.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
    }
}

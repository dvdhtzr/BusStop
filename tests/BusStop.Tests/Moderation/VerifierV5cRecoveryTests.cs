using System.Security.Cryptography;
using System.Text;
using BusStop.Domain;
using BusStop.Moderation;
using BusStop.Moderation.Verification;
using BusStop.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BusStop.Tests.Moderation;

public sealed class VerifierV5cRecoveryTests
{
    private const string Evaluation = "evidence-evaluation-v5c-recovery";

    private sealed record Fixture(
        string Path, ConversationStore Db, Session Session,
        CharacterProfile Speaker, CharacterProfile Other) : IDisposable
    {
        public void Dispose()
        {
            Db.Dispose();
            try { File.Delete(Path); } catch (IOException) { }
            try { File.Delete(Path + ".v5b.lock"); } catch (IOException) { }
        }
    }

    private static string TempPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "busstop-v5c-" + Guid.NewGuid().ToString("N") + ".db");

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static V4SessionPolicy Policy() => new(
        EvidenceVerifierV4.ContractVersion, VerificationOperatingMode.Shadow,
        "claim-cues-v3-1", "claim-extraction-prompt-v3-1",
        EvidenceVerifierV4.VerificationPromptVersion,
        "host-packet-selector-v3-1",
        EvidenceVerifierV4.VerificationSchemaVersion,
        "v5c-test-model", "v5c-test-digest", 0.1, false,
        8, 3, 2, 2048, 8192);

    private static Fixture Setup(bool withM1 = false)
    {
        var path = TempPath();
        var db = ConversationStore.OpenFile(path);
        db.InitializeSchema();
        var first = new CharacterProfile(
            CharacterId.New(), 1, "Arthur", "Careful observer",
            new[] { "curious" });
        var second = new CharacterProfile(
            CharacterId.New(), 1, "Bea", "Practical observer",
            new[] { "practical" });
        db.SaveCharacter(first);
        db.SaveCharacter(second);
        var briefing = new Briefing(BriefingId.New(), "Local report",
            "test", "local-evidence:v5c", "The local report recorded 3 outages.");
        db.SaveBriefing(briefing);
        var session = new Session(SessionId.New(),
            new[] { first.Id, second.Id },
            SessionMode.FreshEncounter, briefing.Id,
            "floor-2", "test-model", "test-context",
            SessionStatus.Active, DateTimeOffset.UtcNow, null);
        ModerationSessionPolicy? m1 = null;
        if (withM1)
        {
            m1 = ModerationSessionPolicy.Disabled with
            {
                Mode = ModerationOperatingMode.Shadow,
                RuleVersion = "m1-rules-test", PromptVersion = "m1-prompt-test",
                ModelName = "m1-test", ModelDigest = "m1-test-digest",
                Temperature = 0.1, Think = false,
                MaxModeratorCalls = 8, ShadowSampleEvery = 1
            };
        }
        db.CreateSession(session, m1);
        db.PinV4VerifierPolicy(session.Id, Policy());
        return new Fixture(path, db, session, first, second);
    }

    private static (Utterance Utterance, VerificationJobIdentity Job)
        Register(Fixture fixture)
    {
        var store = fixture.Db;
        var boundary = store.MaxSequence(fixture.Session.Id);
        var opportunity = new SpeakingOpportunity(
            OpportunityId.New(), fixture.Session.Id,
            fixture.Speaker.Id, OpportunityReason.FairOrder,
            boundary, OpportunityStatus.Offered);
        store.SaveOpportunity(opportunity, (int)boundary);
        var utterance = store.CommitUtterance(
            opportunity.Id, fixture.Session.Id, fixture.Speaker.Id,
            "The report counted 12 outages.", null, null, DateTimeOffset.UtcNow);
        var job = new VerificationJobIdentity(
            Guid.NewGuid(), utterance.Session, utterance.Opportunity,
            utterance.Id, utterance.Speaker, utterance.Sequence - 1,
            EvidenceVerifierV1.ContractVersion, Hash(utterance.Text));
        store.RegisterV5aJob(job, Evaluation);
        return (utterance, job);
    }

    private static ClaimCandidate Claim(
        VerificationJobIdentity job, Utterance utterance) =>
        new(Guid.NewGuid(), job.JobId, 0, 0, utterance.Text.Length,
            utterance.Text, ClaimCategory.SourceFact,
            ClaimRhetoricalMode.Asserted, ClaimExtractionProvenance.HostRule,
            ClaimValidationStatus.HostValidated);

    private static EvidencePacket Packet(
        Fixture f, VerificationJobIdentity job, Utterance u,
        ClaimCandidate claim)
    {
        var selection = f.Db.SelectVerifierEvidence(
            job, u, ClaimCategory.SourceFact, maxItems: 32);
        return EvidencePacketFactory.Freeze(Guid.NewGuid(),
            new EvidencePacketContent(
                EvidenceVerifierV1.ContractVersion,
                EvidenceVerifierV1.PacketSerializerVersion,
                job.JobId, claim.ClaimId, job.CommittedBoundary,
                Policy().RetrievalSelectorVersion,
                Policy().ExtractionPromptVersion,
                "frozen-verifier-packet-v3-1",
                selection.Completeness,
                selection.Scope.AuthorizedItems.Values.ToArray()));
    }

    private static VerificationClaimResult CleanResult(
        ClaimCandidate claim, EvidencePacket packet) =>
        new(claim.ClaimId, VerificationOperationalStatus.Completed,
            new ClaimJudgment(claim.ClaimId, packet.PacketId, packet.Sha256,
                ClaimVerificationOutcome.NoMaterialIssue,
                Array.Empty<string>(), Array.Empty<string>(), "",
                "No direct material contradiction established."),
            null);

    private static VerificationObservationSummary CleanSummary(Guid jobId) =>
        new(jobId, 1, 1, 0, 1, 0, 0, 0, 0,
            VerificationCompletionStatus.Complete,
            "All identified claims reconciled; not a universal truth certification.");


    private static V5cRuntimePins Runtime() => new(
        Policy().ModelName, Policy().ModelDigest, Policy().RuleVersion,
        Policy().ExtractionPromptVersion, Policy().VerificationPromptVersion,
        Policy().RetrievalSelectorVersion, Policy().VerificationSchemaVersion);

    private static (V5bProcessGate Gate, V5bOwnership Owner)
        Acquire(Fixture f)
    {
        var gate = V5bProcessGate.Acquire(f.Path);
        try
        {
            var owner = f.Db.AcquireV5bSession(f.Session.Id, gate);
            return (gate, owner);
        }
        catch
        {
            gate.Dispose();
            throw;
        }
    }

    private static (ClaimCandidate Claim, EvidencePacket Packet)
        Freeze(Fixture f, VerificationJobIdentity job, Utterance u,
               V5bOwnership owner)
    {
        var claim = Claim(job, u);
        f.Db.SealV5aClaims(job.JobId, new[] { claim }, owner);
        var packet = Packet(f, job, u, claim);
        f.Db.SealV5aPackets(job.JobId, new[] { packet }, owner);
        return (claim, packet);
    }

    [Fact]
    public void Only_successor_epoch_may_attempt_recovery()
    {
        using var f = Setup();
        var (_, job) = Register(f);
        using var gate = V5bProcessGate.Acquire(f.Path);
        var initial = f.Db.AcquireV5bSession(f.Session.Id, gate);
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.InspectV5cJob(initial, job.JobId, Runtime()));
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.ReviewV5cBatch(initial, Runtime()));
        Assert.Null(f.Db.GetV5cStop(job.JobId));
    }

    [Fact]
    public void Missing_frozen_stage_becomes_an_immutable_incomplete_stop()
    {
        using var f = Setup();
        var (_, job) = Register(f);
        var first = Acquire(f);
        first.Gate.Dispose(); // abandoned job before claim extraction
        using var gate = V5bProcessGate.Acquire(f.Path);
        var successor = f.Db.AcquireV5bSession(f.Session.Id, gate);
        var plan = f.Db.InspectV5cJob(successor, job.JobId, Runtime());
        Assert.Equal(V5cRecoveryDisposition.StoppedMissingFrozenStage,
            plan.Disposition);
        var stopped = f.Db.GetV5cStop(job.JobId);
        Assert.NotNull(stopped);
        Assert.Equal(successor.Epoch, stopped.OwnerEpoch);
        using (var reopened = ConversationStore.OpenFile(f.Path))
        {
            reopened.InitializeSchema();
            Assert.Equal(stopped, reopened.GetV5cStop(job.JobId));
            Assert.Single(reopened.GetCommittedUtterances(job.SessionId));
        }
        using (var sql = new SqliteConnection($"Data Source={f.Path}"))
        {
            sql.Open();
            foreach (var statement in new[]
            {
                "UPDATE verifier_v5c_stops SET reason_code='forged';",
                "DELETE FROM verifier_v5c_stops;"
            })
            {
                using var command = sql.CreateCommand();
                command.CommandText = statement;
                Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            }
        }
        Assert.Single(f.Db.ReadV5aCheckpoint(job.JobId).Events);
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.SealV5aClaims(job.JobId, Array.Empty<ClaimCandidate>(), successor));
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.TryReserveV4Attempt(
                job, Guid.NewGuid(), V4AttemptPhase.ClaimExtraction, successor));
        Assert.Equal(V5cRecoveryDisposition.AlreadyStopped,
            f.Db.InspectV5cJob(successor, job.JobId, Runtime()).Disposition);
    }

    [Fact]
    public void Incompatible_model_digest_stops_without_substituting_new_evidence()
    {
        using var f = Setup();
        var (u, job) = Register(f);
        var first = Acquire(f);
        var frozen = Freeze(f, job, u, first.Owner);
        first.Gate.Dispose();
        using var gate = V5bProcessGate.Acquire(f.Path);
        var owner = f.Db.AcquireV5bSession(f.Session.Id, gate);
        var changed = Runtime() with { ModelDigest = "different-digest" };
        var verdict = f.Db.InspectV5cJob(owner, job.JobId, changed);
        Assert.Equal(V5cRecoveryDisposition.StoppedIncompatibleCheckpoint,
            verdict.Disposition);
        Assert.Equal(frozen.Packet.Sha256,
            f.Db.ReadV5aCheckpoint(job.JobId).Packets[0].Sha256);
        Assert.Empty(f.Db.GetModeratorReservations(job.SessionId));
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.TryReserveV4Attempt(
                job, Guid.NewGuid(), V4AttemptPhase.ClaimVerification, owner));
    }

    [Fact]
    public void Old_reserved_attempt_is_charged_until_explicit_audited_interruption()
    {
        using var f = Setup();
        var (u, job) = Register(f);
        var first = Acquire(f);
        var frozen = Freeze(f, job, u, first.Owner);
        var request = Guid.NewGuid();
        Assert.True(f.Db.TryReserveV4Attempt(
            job, request, V4AttemptPhase.ClaimVerification, first.Owner));
        // Crash after charged reservation, before response and checkpoint link.
        first.Gate.Dispose();

        using var gate = V5bProcessGate.Acquire(f.Path);
        var resumed = f.Db.AcquireV5bSession(f.Session.Id, gate);
        var pending = f.Db.InspectV5cJob(resumed, job.JobId, Runtime());
        Assert.Equal(V5cRecoveryDisposition.NeedsOrphanReconciliation,
            pending.Disposition);
        Assert.Equal(request, Assert.Single(pending.ReservedRequestIds));
        Assert.Equal(frozen.Claim.ClaimId,
            Assert.Single(pending.PendingClaimIds));
        Assert.Equal(1, f.Db.GetV4AttemptCounts(job).SessionSharedAttempts);
        Assert.Equal(1, f.Db.GetV4AttemptCounts(job).UtteranceVerificationAttempts);

        Assert.Throws<InvalidOperationException>(() =>
            f.Db.FinalizeModeratorAttempt(job.SessionId, request,
                ModerationAttemptStatus.Completed, resumed));

        var interrupted = f.Db.InterruptV5cAbandonedRequest(
            resumed, job.JobId, request);
        Assert.Equal("InterruptedAfterCrash", interrupted.OutcomeCode);
        Assert.Equal(first.Owner.Epoch, interrupted.PriorEpoch);
        Assert.Equal(resumed.Epoch, interrupted.RecoveryEpoch);
        Assert.Equal(interrupted,
            f.Db.InterruptV5cAbandonedRequest(resumed, job.JobId, request));

        var followup = f.Db.InspectV5cJob(resumed, job.JobId, Runtime());
        Assert.Equal(V5cRecoveryDisposition.ReadyForFrozenVerification,
            followup.Disposition);
        Assert.True(followup.MayPlanNewInference);
        Assert.Empty(followup.ReservedRequestIds);
        Assert.Equal(1, followup.RemainingVerificationCalls);
        Assert.Equal(frozen.Packet.Sha256,
            Assert.Single(f.Db.ReadV5aCheckpoint(job.JobId).Packets).Sha256);
        Assert.Equal(1, f.Db.GetV4AttemptCounts(job).SessionSharedAttempts);
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.TryReserveV4Attempt(job, request,
                V4AttemptPhase.ClaimVerification, resumed));
        Assert.True(f.Db.TryReserveV4Attempt(job, Guid.NewGuid(),
            V4AttemptPhase.ClaimVerification, resumed));
        Assert.Equal(2, f.Db.GetV4AttemptCounts(job).SessionSharedAttempts);
        Assert.Empty(f.Db.ReadV5aCheckpoint(job.JobId).ClaimResults);
    }

    [Fact]
    public void Model_completed_but_judgment_never_durable_cannot_be_invented_on_recovery()
    {
        using var f = Setup();
        var (utterance, job) = Register(f);
        var initial = Acquire(f);
        var (claim, packet) = Freeze(f, job, utterance, initial.Owner);
        var spent = Guid.NewGuid();
        Assert.True(f.Db.TryReserveV4Attempt(
            job, spent, V4AttemptPhase.ClaimVerification, initial.Owner));
        f.Db.FinalizeModeratorAttempt(
            job.SessionId, spent, ModerationAttemptStatus.Completed, initial.Owner);
        // A response may have been produced but was NOT sealed as a judgment.
        initial.Gate.Dispose();

        using var gate = V5bProcessGate.Acquire(f.Path);
        var current = f.Db.AcquireV5bSession(f.Session.Id, gate);
        var plan = f.Db.InspectV5cJob(current, job.JobId, Runtime());
        Assert.Equal(V5cRecoveryDisposition.ReadyForFrozenVerification,
            plan.Disposition);
        Assert.Single(plan.PendingClaimIds);
        Assert.False(f.Db.ReadV5aCheckpoint(job.JobId).IsTerminal);
        Assert.Empty(f.Db.ReadV5aCheckpoint(job.JobId).ClaimResults);
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.InterruptV5cAbandonedRequest(current, job.JobId, spent));
        Assert.Equal(1, f.Db.GetV4AttemptCounts(job).UtteranceVerificationAttempts);
        Assert.Equal(packet.Sha256,
            Assert.Single(f.Db.ReadV5aCheckpoint(job.JobId).Packets).Sha256);
        Assert.Equal(claim.ClaimId, Assert.Single(plan.PendingClaimIds));
    }

    [Fact]
    public void Restart_does_not_steal_an_inflight_current_epoch_request()
    {
        using var f = Setup();
        var (u, job) = Register(f);
        var first = Acquire(f);
        _ = Freeze(f, job, u, first.Owner);
        f.Db.ReleaseV5bSession(first.Owner);
        first.Gate.Dispose();

        using var gate = V5bProcessGate.Acquire(f.Path);
        var current = f.Db.AcquireV5bSession(f.Session.Id, gate);
        var request = Guid.NewGuid();
        Assert.True(f.Db.TryReserveV4Attempt(
            job, request, V4AttemptPhase.ClaimVerification, current));
        var plan = f.Db.InspectV5cJob(current, job.JobId, Runtime());
        Assert.Equal(V5cRecoveryDisposition.BlockedActiveAttempt,
            plan.Disposition);
        Assert.False(plan.MayPlanNewInference);
        Assert.Single(f.Db.GetModeratorReservations(job.SessionId));
        Assert.Throws<InvalidDataException>(() =>
            f.Db.InterruptV5cAbandonedRequest(current, job.JobId, request));
    }

    [Fact]
    public void Deterministic_recovery_summary_reuses_existing_verified_results()
    {
        using var f = Setup();
        var (u, job) = Register(f);
        var first = Acquire(f);
        var (claim, packet) = Freeze(f, job, u, first.Owner);
        var request = Guid.NewGuid();
        Assert.True(f.Db.TryReserveV4Attempt(
            job, request, V4AttemptPhase.ClaimVerification, first.Owner));
        f.Db.LinkV5aChargedAttempt(job.JobId, request,
            V4AttemptPhase.ClaimVerification,
            new[] { claim.ClaimId }, first.Owner);
        f.Db.FinalizeModeratorAttempt(
            job.SessionId, request, ModerationAttemptStatus.Completed, first.Owner);
        f.Db.RecordV5aClaimResult(
            job.JobId, CleanResult(claim, packet), request, first.Owner);
        // Crash after verdict commit, before summary commit.
        first.Gate.Dispose();

        using var gate = V5bProcessGate.Acquire(f.Path);
        var second = f.Db.AcquireV5bSession(f.Session.Id, gate);
        var plan = f.Db.InspectV5cJob(second, job.JobId, Runtime());
        Assert.Equal(V5cRecoveryDisposition.ReadyForSummary, plan.Disposition);
        Assert.Empty(plan.PendingClaimIds);
        f.Db.SealV5cRecoveredSummary(second, job.JobId, Runtime());

        var checkpoint = f.Db.ReadV5aCheckpoint(job.JobId);
        Assert.True(checkpoint.IsTerminal);
        Assert.Equal(VerificationCompletionStatus.Partial,
            checkpoint.Summary!.CompletionStatus);
        Assert.Equal(1, checkpoint.Summary.NoMaterialIssues);
        Assert.Equal(1, f.Db.GetV4AttemptCounts(job).SessionSharedAttempts);
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.SealV5cRecoveredSummary(second, job.JobId, Runtime()));
    }

    [Fact]
    public void Exhausted_budget_stops_incomplete_instead_of_fabricating_clean_result()
    {
        using var f = Setup();
        var (u, job) = Register(f);
        var first = Acquire(f);
        _ = Freeze(f, job, u, first.Owner);
        for (var index = 0; index < 2; index++)
        {
            var attempt = Guid.NewGuid();
            Assert.True(f.Db.TryReserveV4Attempt(
                job, attempt, V4AttemptPhase.ClaimVerification, first.Owner));
            f.Db.FinalizeModeratorAttempt(
                job.SessionId, attempt, ModerationAttemptStatus.Interrupted,
                first.Owner);
        }
        first.Gate.Dispose();

        using var gate = V5bProcessGate.Acquire(f.Path);
        var second = f.Db.AcquireV5bSession(f.Session.Id, gate);
        var plan = f.Db.InspectV5cJob(second, job.JobId, Runtime());
        Assert.Equal(V5cRecoveryDisposition.StoppedNoRemainingBudget,
            plan.Disposition);
        Assert.NotNull(f.Db.GetV5cStop(job.JobId));
        Assert.Empty(f.Db.ReadV5aCheckpoint(job.JobId).ClaimResults);
        Assert.Equal(2, f.Db.GetV4AttemptCounts(job).UtteranceVerificationAttempts);
    }

    [Fact]
    public void Incompatible_event_hash_fails_closed_with_separate_durable_stop()
    {
        using var f = Setup();
        var (_, job) = Register(f);
        var first = Acquire(f);
        first.Gate.Dispose();

        using (var sql = new SqliteConnection($"Data Source={f.Path}"))
        {
            sql.Open();
            using var cmd = sql.CreateCommand();
            cmd.CommandText = """
                INSERT INTO verifier_v5a_events
                    (job_id, sequence, event_key, stage, payload_json,
                     payload_sha256, previous_sha256, event_sha256, recorded_at)
                VALUES (@job, 2, 'claims:sealed', 'ClaimsSealed', '{}',
                        'bad-payload', 'bad-previous', 'bad-event',
                        '2026-01-01T00:00:00+00:00');
                """;
            cmd.Parameters.AddWithValue("@job", job.JobId.ToString("D"));
            cmd.ExecuteNonQuery();
        }

        using var gate = V5bProcessGate.Acquire(f.Path);
        var second = f.Db.AcquireV5bSession(f.Session.Id, gate);
        var plan = f.Db.InspectV5cJob(second, job.JobId, Runtime());
        Assert.Equal(V5cRecoveryDisposition.StoppedIncompatibleCheckpoint,
            plan.Disposition);
        Assert.NotNull(f.Db.GetV5cStop(job.JobId));
        Assert.Throws<InvalidDataException>(() =>
            f.Db.ReadV5aCheckpoint(job.JobId));
        Assert.Throws<InvalidDataException>(() =>
            f.Db.SealV5aClaims(job.JobId, Array.Empty<ClaimCandidate>(), second));
    }

    [Fact]
    public void Missing_frozen_packet_stage_stops_without_looking_up_new_sources()
    {
        using var f = Setup();
        var (u, job) = Register(f);
        var first = Acquire(f);
        f.Db.SealV5aClaims(job.JobId, new[] { Claim(job, u) }, first.Owner);
        first.Gate.Dispose();
        using var gate = V5bProcessGate.Acquire(f.Path);
        var second = f.Db.AcquireV5bSession(f.Session.Id, gate);
        Assert.Equal(V5cRecoveryDisposition.StoppedMissingFrozenStage,
            f.Db.InspectV5cJob(second, job.JobId, Runtime()).Disposition);
        Assert.Empty(f.Db.ReadV5aCheckpoint(job.JobId).Packets);
    }

    [Fact]
    public void Reservation_without_v5b_owner_stamp_is_not_adopted()
    {
        using var f = Setup();
        var (u, job) = Register(f);
        // Legacy unowned reservation cannot become a newly stamped request.
        Assert.True(f.Db.TryReserveV4Attempt(
            job, Guid.NewGuid(), V4AttemptPhase.ClaimExtraction));
        var first = Acquire(f);
        _ = Freeze(f, job, u, first.Owner);
        first.Gate.Dispose();

        using var gate = V5bProcessGate.Acquire(f.Path);
        var second = f.Db.AcquireV5bSession(f.Session.Id, gate);
        var plan = f.Db.InspectV5cJob(second, job.JobId, Runtime());
        Assert.Equal(V5cRecoveryDisposition.StoppedUnknownAttemptOwner,
            plan.Disposition);
        Assert.Equal(1, f.Db.GetV4AttemptCounts(job).UtteranceExtractionAttempts);
        Assert.NotNull(f.Db.GetV5cStop(job.JobId));
    }

    [Fact]
    public void Recovery_scan_is_keyset_paginated_at_four_jobs_without_inference()
    {
        using var f = Setup();
        var jobs = new List<Guid>();
        for (int i = 0; i < 5; i++)
            jobs.Add(Register(f).Job.JobId);
        var first = Acquire(f);
        first.Gate.Dispose();
        using var gate = V5bProcessGate.Acquire(f.Path);
        var second = f.Db.AcquireV5bSession(f.Session.Id, gate);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            f.Db.ReviewV5cBatch(second, Runtime(), 5));
        var batch = f.Db.ReviewV5cBatch(second, Runtime(), limit: 4);
        Assert.Equal(4, batch.Count);
        Assert.All(batch, x => Assert.Equal(
            V5cRecoveryDisposition.StoppedMissingFrozenStage, x.Disposition));
        Assert.Equal(5, jobs.Count);
        var secondBatch = f.Db.ReviewV5cBatch(second, Runtime());
        Assert.Single(secondBatch);
        Assert.Empty(f.Db.ReviewV5cBatch(second, Runtime()));
        Assert.Equal(5, jobs.Count(id => f.Db.GetV5cStop(id) is not null));
    }

    [Fact]
    public void Recovery_stop_and_interruption_are_immutable_sqlite_records()
    {
        using var f = Setup();
        var (u, job) = Register(f);
        var first = Acquire(f);
        _ = Freeze(f, job, u, first.Owner);
        var orphan = Guid.NewGuid();
        Assert.True(f.Db.TryReserveV4Attempt(
            job, orphan, V4AttemptPhase.ClaimVerification, first.Owner));
        first.Gate.Dispose();

        using var gate = V5bProcessGate.Acquire(f.Path);
        var second = f.Db.AcquireV5bSession(f.Session.Id, gate);
        _ = f.Db.InterruptV5cAbandonedRequest(second, job.JobId, orphan);

        using var sql = new SqliteConnection($"Data Source={f.Path}");
        sql.Open();
        foreach (var statement in new[]
        {
            "UPDATE verifier_v5c_reconciliations SET disposition='Completed';",
            "DELETE FROM verifier_v5c_reconciliations;",
            "UPDATE verifier_v5c_schema SET version='future-v99';"
        })
        {
            using var cmd = sql.CreateCommand();
            cmd.CommandText = statement;
            Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        }
        Assert.Equal(1, f.Db.GetV4AttemptCounts(job).SessionSharedAttempts);
    }

    [Fact]
    public void Partially_installed_recovery_schema_fails_closed()
    {
        var path = TempPath();
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE verifier_v5c_schema
                        (singleton INTEGER PRIMARY KEY, version TEXT NOT NULL);
                    INSERT INTO verifier_v5c_schema
                        VALUES(1, 'verifier-recovery-v5c-1');
                    """;
                cmd.ExecuteNonQuery();
            }
            using var db = ConversationStore.OpenFile(path);
            Assert.Throws<InvalidDataException>(() => db.InitializeSchema());
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void Future_or_partial_recovery_schema_refuses_legacy_migration()
    {
        var path = TempPath();
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE verifier_v5c_schema
                        (singleton INTEGER PRIMARY KEY, version TEXT NOT NULL);
                    INSERT INTO verifier_v5c_schema VALUES(1, 'future-v99');
                    """;
                cmd.ExecuteNonQuery();
            }
            using var db = ConversationStore.OpenFile(path);
            Assert.Throws<InvalidDataException>(() => db.InitializeSchema());
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}

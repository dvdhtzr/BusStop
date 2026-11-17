using System.Security.Cryptography;
using System.Text;
using BusStop.Domain;
using BusStop.Moderation;
using BusStop.Moderation.Verification;
using BusStop.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BusStop.Tests.Moderation;

public sealed class VerifierV5bOwnershipTests
{
    private const string Evaluation = "evidence-evaluation-v5b-ownership";

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
            "busstop-v5b-" + Guid.NewGuid().ToString("N") + ".db");

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static V4SessionPolicy Policy() => new(
        EvidenceVerifierV4.ContractVersion, VerificationOperatingMode.Shadow,
        "claim-cues-v3-1", "claim-extraction-prompt-v3-1",
        EvidenceVerifierV4.VerificationPromptVersion,
        "host-packet-selector-v3-1",
        EvidenceVerifierV4.VerificationSchemaVersion,
        "v5b-test-model", "v5b-test-digest", 0.1, false,
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
            "test", "local-evidence:v5b", "The local report recorded 3 outages.");
        db.SaveBriefing(briefing);
        var session = new Session(SessionId.New(),
            new[] { first.Id, second.Id },
            SessionMode.FreshEncounter, briefing.Id,
            "floor-2", "test-model", "test-context",
            SessionStatus.Active, DateTimeOffset.UtcNow, null);
        var m1 = withM1 ? ModerationSessionPolicy.Disabled with
        {
            Mode = ModerationOperatingMode.Shadow,
            RuleVersion = "m1-rules-test", PromptVersion = "m1-prompt-test",
            ModelName = "m1-test", ModelDigest = "m1-test-digest",
            Temperature = 0.1, Think = false,
            MaxModeratorCalls = 8, ShadowSampleEvery = 1
        } : null;
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

    [Fact]
    public void Os_lock_is_exclusive_and_releases_handle_without_unlinking_path()
    {
        using var f = Setup();
        using (var first = V5bProcessGate.Acquire(f.Path))
        {
            Assert.Throws<IOException>(() => V5bProcessGate.Acquire(f.Path));
            Assert.True(File.Exists(f.Path + ".v5b.lock"));
        }
        Assert.True(File.Exists(f.Path + ".v5b.lock"));
        using var second = V5bProcessGate.Acquire(f.Path);
        Assert.NotNull(second);
    }

    [Fact]
    public void Ownership_requires_an_existing_pinned_registered_v5a_job()
    {
        using var f = Setup();
        using var gate = V5bProcessGate.Acquire(f.Path);
        Assert.Throws<InvalidOperationException>(
            () => f.Db.AcquireV5bSession(f.Session.Id, gate));
        var (_, job) = Register(f);
        var lease = f.Db.AcquireV5bSession(f.Session.Id, gate);
        Assert.Equal(1, lease.Epoch);
        Assert.Equal(f.Session.Id, lease.Session);
        Assert.NotEqual(Guid.Empty, lease.OwnerId);
        Assert.Throws<InvalidOperationException>(
            () => f.Db.AcquireV5bSession(f.Session.Id, gate));
        Assert.Single(f.Db.ReadV5aCheckpoint(job.JobId).Events);
    }

    [Fact]
    public void Epoch_protects_all_stages_and_v4_reservations_from_unowned_calls()
    {
        using var f = Setup();
        var (utterance, job) = Register(f);
        using var gate = V5bProcessGate.Acquire(f.Path);
        var lease = f.Db.AcquireV5bSession(f.Session.Id, gate);
        var claim = Claim(job, utterance);
        var packet = Packet(f, job, utterance, claim);

        Assert.Throws<InvalidOperationException>(
            () => f.Db.RegisterV5aJob(job, Evaluation));
        f.Db.RegisterV5aJob(job, Evaluation, lease);

        Assert.Throws<InvalidOperationException>(
            () => f.Db.SealV5aClaims(job.JobId, new[] { claim }));
        f.Db.SealV5aClaims(job.JobId, new[] { claim }, lease);

        Assert.Throws<InvalidOperationException>(
            () => f.Db.SealV5aPackets(job.JobId, new[] { packet }));
        f.Db.SealV5aPackets(job.JobId, new[] { packet }, lease);
        Assert.Throws<InvalidOperationException>(
            () => f.Db.SealV5aPackets(job.JobId, new[] { packet }));
        f.Db.SealV5aPackets(job.JobId, new[] { packet }, lease);

        Assert.Throws<InvalidOperationException>(() =>
            f.Db.TryReserveV4Attempt(job, Guid.NewGuid(),
                V4AttemptPhase.ClaimVerification));
        var requestId = Guid.NewGuid();
        Assert.True(f.Db.TryReserveV4Attempt(
            job, requestId, V4AttemptPhase.ClaimVerification, lease));

        Assert.Throws<InvalidOperationException>(() =>
            f.Db.LinkV5aChargedAttempt(
                job.JobId, requestId, V4AttemptPhase.ClaimVerification,
                new[] { claim.ClaimId }));
        f.Db.LinkV5aChargedAttempt(
            job.JobId, requestId, V4AttemptPhase.ClaimVerification,
            new[] { claim.ClaimId }, lease);
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.FinalizeModeratorAttempt(job.SessionId, requestId,
                ModerationAttemptStatus.Completed));
        f.Db.FinalizeModeratorAttempt(job.SessionId, requestId,
            ModerationAttemptStatus.Completed, lease);

        Assert.Throws<InvalidOperationException>(() =>
            f.Db.RecordV5aClaimResult(job.JobId,
                CleanResult(claim, packet), requestId));
        f.Db.RecordV5aClaimResult(job.JobId,
            CleanResult(claim, packet), requestId, lease);
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.SealV5aSummary(job.JobId, CleanSummary(job.JobId)));
        f.Db.SealV5aSummary(job.JobId, CleanSummary(job.JobId), lease);

        Assert.True(f.Db.ReadV5aCheckpoint(job.JobId).IsTerminal);
        Assert.Equal(1, f.Db.GetV4AttemptCounts(job).UtteranceVerificationAttempts);
        f.Db.ReleaseV5bSession(lease);
        Assert.Throws<InvalidOperationException>(
            () => f.Db.AssertV5bCanDispatch(lease));
    }

    [Fact]
    public void New_epoch_fences_old_capability_even_while_one_os_gate_is_held()
    {
        using var f = Setup();
        var (u, job) = Register(f);
        using var gate = V5bProcessGate.Acquire(f.Path);
        var first = f.Db.AcquireV5bSession(f.Session.Id, gate);
        f.Db.SealV5aClaims(job.JobId, Array.Empty<ClaimCandidate>(), first);
        f.Db.ReleaseV5bSession(first);
        f.Db.ReleaseV5bSession(first); // idempotent current release
        var second = f.Db.AcquireV5bSession(f.Session.Id, gate);

        Assert.Equal(first.Epoch + 1, second.Epoch);
        Assert.NotEqual(first.OwnerId, second.OwnerId);
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.SealV5aPackets(
                job.JobId, Array.Empty<EvidencePacket>(), first));
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.AssertV5bCanDispatch(first));
        f.Db.AssertV5bCanDispatch(second);
        f.Db.SealV5aPackets(
            job.JobId, Array.Empty<EvidencePacket>(), second);
        Assert.Equal(3, f.Db.ReadV5aCheckpoint(job.JobId).Events.Count);
    }

    [Fact]
    public void Simulated_crash_releases_os_gate_but_never_refunds_charged_attempts()
    {
        using var f = Setup();
        var (_, job) = Register(f);
        var crashedGate = V5bProcessGate.Acquire(f.Path);
        var old = f.Db.AcquireV5bSession(f.Session.Id, crashedGate);
        var charged = Guid.NewGuid();
        Assert.True(f.Db.TryReserveV4Attempt(
            job, charged, V4AttemptPhase.ClaimExtraction, old));
        crashedGate.Dispose(); // Simulate process exit without releasing session

        using var restartedGate = V5bProcessGate.Acquire(f.Path);
        var newer = f.Db.AcquireV5bSession(f.Session.Id, restartedGate);
        Assert.Equal(old.Epoch + 1, newer.Epoch);
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.FinalizeModeratorAttempt(job.SessionId, charged,
                ModerationAttemptStatus.Interrupted, old));
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.AssertV5bCanDispatch(old));

        Assert.Equal(1, f.Db.GetV4AttemptCounts(job).UtteranceExtractionAttempts);
        Assert.False(f.Db.TryReserveV4Attempt(
            job, Guid.NewGuid(), V4AttemptPhase.ClaimExtraction, newer));
        // No V5b automatic retry or charge refund. V5c will classify the
        // orphan Reserved attempt and decide whether a bounded retry is legal.
        Assert.Single(f.Db.GetModeratorReservations(job.SessionId));
    }

    [Fact]
    public void Gate_from_another_database_cannot_own_or_write_to_this_database()
    {
        using var f = Setup();
        using var other = Setup();
        _ = Register(f);
        _ = Register(other);
        using var wrongGate = V5bProcessGate.Acquire(other.Path);
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.AcquireV5bSession(f.Session.Id, wrongGate));
        Assert.Empty(f.Db.ReadV5aCheckpoint(
            // A separate randomized V5a job is still readable without ownership.
            f.Db.GetCommittedUtterances(f.Session.Id)
                .Select(_ => Guid.Empty).FirstOrDefault()) .Events);
    }

    [Fact]
    public void Existing_m1_attempts_remain_unfenced_and_share_the_session_budget()
    {
        using var f = Setup(withM1: true);
        var (_, job) = Register(f);
        using var gate = V5bProcessGate.Acquire(f.Path);
        var lease = f.Db.AcquireV5bSession(f.Session.Id, gate);
        var m1Id = Guid.NewGuid();
        f.Db.ReserveModeratorAttempt(f.Session.Id, m1Id, job.OpportunityId,
            ModerationInferencePurpose.ShadowSampling, DateTimeOffset.UtcNow);
        f.Db.FinalizeModeratorAttempt(f.Session.Id, m1Id,
            ModerationAttemptStatus.Completed);
        Assert.Equal(1, f.Db.GetV4AttemptCounts(job).SessionSharedAttempts);

        Assert.True(f.Db.TryReserveV4Attempt(
            job, Guid.NewGuid(), V4AttemptPhase.ClaimVerification, lease));
        Assert.Equal(2, f.Db.GetV4AttemptCounts(job).SessionSharedAttempts);
    }

    [Fact]
    public void Released_owner_cannot_finalize_even_an_idempotent_verifier_request()
    {
        using var f = Setup();
        var (_, job) = Register(f);
        using var gate = V5bProcessGate.Acquire(f.Path);
        var lease = f.Db.AcquireV5bSession(f.Session.Id, gate);
        var request = Guid.NewGuid();
        Assert.True(f.Db.TryReserveV4Attempt(
            job, request, V4AttemptPhase.ClaimExtraction, lease));
        f.Db.FinalizeModeratorAttempt(
            job.SessionId, request, ModerationAttemptStatus.Interrupted, lease);
        f.Db.ReleaseV5bSession(lease);
        Assert.Throws<InvalidOperationException>(() =>
            f.Db.FinalizeModeratorAttempt(
                job.SessionId, request, ModerationAttemptStatus.Interrupted, lease));
        Assert.Equal(1, f.Db.GetV4AttemptCounts(job).SessionSharedAttempts);
    }

    [Fact]
    public void Unknown_future_v5b_schema_refuses_additive_migration()
    {
        var path = TempPath();
        try
        {
            using (var sqlite = new SqliteConnection($"Data Source={path}"))
            {
                sqlite.Open();
                using var cmd = sqlite.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE verifier_v5b_schema
                        (singleton INTEGER PRIMARY KEY, version TEXT NOT NULL);
                    INSERT INTO verifier_v5b_schema VALUES (1, 'verifier-ownership-v99');
                    """;
                cmd.ExecuteNonQuery();
            }
            using var db = ConversationStore.OpenFile(path);
            Assert.Throws<InvalidDataException>(() => db.InitializeSchema());
            using var sqliteAgain = new SqliteConnection($"Data Source={path}");
            sqliteAgain.Open();
            using var check = sqliteAgain.CreateCommand();
            check.CommandText =
                "SELECT version FROM verifier_v5b_schema WHERE singleton=1;";
            Assert.Equal("verifier-ownership-v99", check.ExecuteScalar());
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using BusStop.Domain;
using BusStop.Moderation;
using BusStop.Moderation.Verification;
using BusStop.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BusStop.Tests.Moderation;

public sealed class VerifierV5aLedgerTests
{
    private const string Evaluation = "evidence-evaluation-v5a-fixture";

    private sealed record Fixture(
        ConversationStore Store, Session Session, CharacterProfile Speaker,
        CharacterProfile Other, Briefing Briefing);

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static V4SessionPolicy Policy() => new(
        EvidenceVerifierV4.ContractVersion, VerificationOperatingMode.Shadow,
        "claim-cues-v3-1", "claim-extraction-prompt-v3-1",
        EvidenceVerifierV4.VerificationPromptVersion,
        "host-packet-selector-v3-1",
        EvidenceVerifierV4.VerificationSchemaVersion,
        "v5a-test-model", "pinned-v5a-digest", 0.1, false,
        8, 3, 2, 2_048, 8_192);

    private static Fixture Setup(string? databasePath = null, bool optIn = true)
    {
        var db = databasePath is null
            ? new ConversationStore("Data Source=:memory:")
            : ConversationStore.OpenFile(databasePath);
        db.InitializeSchema();
        var speaker = new CharacterProfile(
            CharacterId.New(), 1, "Arthur", "Curious about the report.",
            new[] { "curious" });
        var other = new CharacterProfile(
            CharacterId.New(), 1, "Bea", "Practical neighbor.",
            new[] { "practical" });
        db.SaveCharacter(speaker);
        db.SaveCharacter(other);
        var briefing = new Briefing(
            BriefingId.New(), "Local incident report", "test",
            "local-evidence:synthetic-v5a", "The report recorded 3 outages.");
        db.SaveBriefing(briefing);
        var session = new Session(
            SessionId.New(), new[] { speaker.Id, other.Id },
            SessionMode.FreshEncounter, briefing.Id, "floor-2",
            "test-model", "test-context", SessionStatus.Active,
            DateTimeOffset.UtcNow, null);
        db.CreateSession(session);
        if (optIn) db.PinV4VerifierPolicy(session.Id, Policy());
        return new Fixture(db, session, speaker, other, briefing);
    }

    private static Utterance Speak(Fixture fixture,
        string text = "The report counted 12 outages.")
    {
        var db = fixture.Store;
        var boundary = db.MaxSequence(fixture.Session.Id);
        var opportunity = new SpeakingOpportunity(
            OpportunityId.New(), fixture.Session.Id, fixture.Speaker.Id,
            OpportunityReason.FairOrder, boundary, OpportunityStatus.Offered);
        db.SaveOpportunity(opportunity, (int)boundary);
        return db.CommitUtterance(opportunity.Id, fixture.Session.Id,
            fixture.Speaker.Id, text, null, null, DateTimeOffset.UtcNow);
    }

    private static VerificationJobIdentity Job(Utterance u) =>
        new(Guid.NewGuid(), u.Session, u.Opportunity,
            u.Id, u.Speaker, u.Sequence - 1,
            EvidenceVerifierV1.ContractVersion, Hash(u.Text));

    private static ClaimCandidate Claim(VerificationJobIdentity job,
        Utterance utterance, int index = 0, int start = 0, int? length = null) =>
        new(Guid.NewGuid(), job.JobId, index, start,
            length ?? utterance.Text.Length,
            utterance.Text.Substring(start, length ?? utterance.Text.Length),
            ClaimCategory.SourceFact, ClaimRhetoricalMode.Asserted,
            ClaimExtractionProvenance.HostRule, ClaimValidationStatus.HostValidated);

    private static EvidencePacket Packet(
        Fixture f, VerificationJobIdentity job, Utterance utterance,
        ClaimCandidate claim)
    {
        var selected = f.Store.SelectVerifierEvidence(
            job, utterance, ClaimCategory.SourceFact, maxItems: 32);
        return EvidencePacketFactory.Freeze(Guid.NewGuid(),
            new EvidencePacketContent(
                EvidenceVerifierV1.ContractVersion,
                EvidenceVerifierV1.PacketSerializerVersion,
                job.JobId, claim.ClaimId, job.CommittedBoundary,
                Policy().RetrievalSelectorVersion,
                Policy().ExtractionPromptVersion,
                "frozen-verifier-packet-v3-1",
                selected.Completeness,
                selected.Scope.AuthorizedItems.Values.ToArray()));
    }

    private static VerificationClaimResult CleanResult(
        ClaimCandidate claim, EvidencePacket packet) =>
        new(claim.ClaimId, VerificationOperationalStatus.Completed,
            new ClaimJudgment(claim.ClaimId, packet.PacketId, packet.Sha256,
                ClaimVerificationOutcome.NoMaterialIssue,
                Array.Empty<string>(), Array.Empty<string>(), "",
                "No direct material contradiction was established."),
            null);

    private static VerificationObservationSummary Summary(
        Guid jobId, int identified, int verified, int clean, int insufficient,
        int skipped = 0, int failed = 0) => new(
            jobId, identified, verified, 0, clean, insufficient, 0,
            skipped, failed,
            skipped > 0 || failed > 0 ? VerificationCompletionStatus.Incomplete :
                VerificationCompletionStatus.Complete,
            "All identified claim results were explicitly reconciled; no universal truth claim.");

    [Fact]
    public void Registration_requires_pinned_new_session_and_original_committed_utterance()
    {
        var f = Setup(optIn: false);
        using var db = f.Store;
        var u = Speak(f);
        var job = Job(u);
        Assert.Throws<InvalidOperationException>(
            () => db.RegisterV5aJob(job, Evaluation));
        Assert.Throws<InvalidDataException>(
            () => db.RegisterV5aJob(job with { UtteranceSha256 = new string('0', 64) },
                Evaluation));

        var enabled = Setup();
        using var otherDb = enabled.Store;
        var enabledU = Speak(enabled);
        var enabledJob = Job(enabledU);
        Assert.Throws<InvalidDataException>(
            () => otherDb.RegisterV5aJob(enabledJob, "unversioned"));
        otherDb.RegisterV5aJob(enabledJob, Evaluation);
        var checkpoint = otherDb.ReadV5aCheckpoint(enabledJob.JobId);
        Assert.Single(checkpoint.Events);
        Assert.Equal(V5aLedgerStage.Registered, checkpoint.Events[0].Stage);
        Assert.Null(checkpoint.Summary);
        Assert.Equal(Policy(), checkpoint.PinnedPolicy);
    }

    [Fact]
    public void Identical_registration_is_idempotent_but_altered_evaluation_job_is_rejected()
    {
        var f = Setup();
        using var db = f.Store;
        var u = Speak(f);
        var job = Job(u);
        db.RegisterV5aJob(job, Evaluation);
        db.RegisterV5aJob(job, Evaluation);
        Assert.Single(db.ReadV5aCheckpoint(job.JobId).Events);

        Assert.Throws<InvalidDataException>(
            () => db.RegisterV5aJob(job, Evaluation + "-mutated"));
        Assert.ThrowsAny<Exception>(() =>
            db.RegisterV5aJob(job with { JobId = Guid.NewGuid() }, Evaluation));
        Assert.Single(db.ReadV5aCheckpoint(job.JobId).Events);
    }

    [Fact]
    public void Append_only_stage_sequence_reopens_with_frozen_packets_and_summary()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "busstop-v5a-" + Guid.NewGuid().ToString("N") + ".db");
        Guid jobId;
        string packetHash;
        try
        {
            var f = Setup(path);
            using (var db = f.Store)
            {
                var u = Speak(f);
                var job = Job(u);
                jobId = job.JobId;
                db.RegisterV5aJob(job, Evaluation);
                var claim = Claim(job, u);
                db.SealV5aClaims(jobId, new[] { claim });
                var packet = Packet(f, job, u, claim);
                packetHash = packet.Sha256;
                db.SealV5aPackets(jobId, new[] { packet });

                var requestId = Guid.NewGuid();
                Assert.True(db.TryReserveV4Attempt(
                    job, requestId, V4AttemptPhase.ClaimVerification));
                db.LinkV5aChargedAttempt(jobId, requestId,
                    V4AttemptPhase.ClaimVerification, new[] { claim.ClaimId });
                db.FinalizeModeratorAttempt(job.SessionId, requestId,
                    ModerationAttemptStatus.Completed);

                db.RecordV5aClaimResult(jobId, CleanResult(claim, packet),
                    requestId);
                db.SealV5aSummary(jobId, Summary(jobId, 1, 1, 1, 0));
                var staged = db.ReadV5aCheckpoint(jobId);
                Assert.True(staged.IsTerminal);
                Assert.Equal(6, staged.Events.Count);
                Assert.Equal(6, staged.Events.Last().Sequence);
                Assert.Equal(1, staged.AttemptLinks.Count);
            }

            using var reopened = ConversationStore.OpenFile(path);
            reopened.InitializeSchema();
            var checkpoint = reopened.ReadV5aCheckpoint(jobId);
            Assert.True(checkpoint.IsTerminal);
            Assert.Single(checkpoint.Packets);
            Assert.Equal(packetHash, checkpoint.Packets[0].Sha256);
            Assert.Equal(1, checkpoint.Summary!.NoMaterialIssues);
            Assert.Equal(V5aLedgerStage.SummarySealed, checkpoint.Events[^1].Stage);
            for (var i = 1; i < checkpoint.Events.Count; i++)
            {
                Assert.Equal(checkpoint.Events[i - 1].EventSha256,
                    checkpoint.Events[i].PreviousSha256);
                Assert.Equal(i + 1, checkpoint.Events[i].Sequence);
            }
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void Exact_repeated_stage_writes_are_idempotent_even_after_terminal_seal()
    {
        var f = Setup();
        using var db = f.Store;
        var u = Speak(f);
        var job = Job(u);
        var claim = Claim(job, u);
        var packet = Packet(f, job, u, claim);
        var result = CleanResult(claim, packet);
        var summary = Summary(job.JobId, 1, 1, 1, 0);

        db.RegisterV5aJob(job, Evaluation);
        db.SealV5aClaims(job.JobId, new[] { claim });
        db.SealV5aPackets(job.JobId, new[] { packet });
        var requestId = Guid.NewGuid();
        Assert.True(db.TryReserveV4Attempt(
            job, requestId, V4AttemptPhase.ClaimVerification));
        db.LinkV5aChargedAttempt(job.JobId, requestId,
            V4AttemptPhase.ClaimVerification, new[] { claim.ClaimId });
        db.FinalizeModeratorAttempt(job.SessionId, requestId,
            ModerationAttemptStatus.Completed);
        db.RecordV5aClaimResult(job.JobId, result, requestId);
        db.SealV5aSummary(job.JobId, summary);

        db.RegisterV5aJob(job, Evaluation);
        db.SealV5aClaims(job.JobId, new[] { claim });
        db.SealV5aPackets(job.JobId, new[] { packet });
        db.LinkV5aChargedAttempt(job.JobId, requestId,
            V4AttemptPhase.ClaimVerification, new[] { claim.ClaimId });
        db.RecordV5aClaimResult(job.JobId, result, requestId);
        db.SealV5aSummary(job.JobId, summary);

        var stored = db.ReadV5aCheckpoint(job.JobId);
        Assert.Equal(6, stored.Events.Count);
        Assert.True(stored.IsTerminal);
        Assert.Throws<InvalidDataException>(
            () => db.SealV5aClaims(job.JobId, new[] { Claim(job, u) }));
    }

    [Fact]
    public void New_stages_cannot_be_written_out_of_order_or_after_terminal_summary()
    {
        var f = Setup();
        using var db = f.Store;
        var u = Speak(f);
        var job = Job(u);
        var claim = Claim(job, u);
        var packet = Packet(f, job, u, claim);

        db.RegisterV5aJob(job, Evaluation);
        Assert.Throws<InvalidDataException>(
            () => db.SealV5aPackets(job.JobId, new[] { packet }));
        Assert.Throws<InvalidDataException>(
            () => db.RecordV5aClaimResult(job.JobId, CleanResult(claim, packet)));
        db.SealV5aClaims(job.JobId, new[] { claim });
        db.SealV5aPackets(job.JobId, new[] { packet });
        Assert.Throws<InvalidDataException>(
            () => db.SealV5aSummary(job.JobId, Summary(job.JobId, 1, 1, 1, 0)));
        Assert.Throws<InvalidDataException>(
            () => db.RecordV5aClaimResult(job.JobId, CleanResult(claim, packet)));
        var requestId = Guid.NewGuid();
        Assert.True(db.TryReserveV4Attempt(
            job, requestId, V4AttemptPhase.ClaimVerification));
        db.LinkV5aChargedAttempt(job.JobId, requestId,
            V4AttemptPhase.ClaimVerification, new[] { claim.ClaimId });
        db.FinalizeModeratorAttempt(job.SessionId, requestId,
            ModerationAttemptStatus.Completed);
        db.RecordV5aClaimResult(job.JobId, CleanResult(claim, packet), requestId);
        db.SealV5aSummary(job.JobId, Summary(job.JobId, 1, 1, 1, 0));

        Assert.Throws<InvalidDataException>(() =>
            db.LinkV5aChargedAttempt(job.JobId, Guid.NewGuid(),
                V4AttemptPhase.ClaimVerification, new[] { claim.ClaimId }));
    }

    [Fact]
    public void Packet_freeze_rejects_unseen_evidence_and_mutated_hashes()
    {
        var f = Setup();
        using var db = f.Store;
        var u = Speak(f);
        var job = Job(u);
        var claim = Claim(job, u);
        db.RegisterV5aJob(job, Evaluation);
        db.SealV5aClaims(job.JobId, new[] { claim });
        var actual = Packet(f, job, u, claim);
        var forbidden = actual.Content.Items[0] with
        {
            ReferenceId = "private:never-exposed",
            Excerpt = "A secret passage the speaker never received."
        };
        var mutated = EvidencePacketFactory.Freeze(
            Guid.NewGuid(), actual.Content with { Items = new[] { forbidden } });
        Assert.Throws<InvalidDataException>(() =>
            db.SealV5aPackets(job.JobId, new[] { mutated }));
        Assert.Throws<InvalidDataException>(() =>
            db.SealV5aPackets(job.JobId,
                new[] { actual with { Sha256 = new string('0', 64) } }));
        Assert.Empty(db.ReadV5aCheckpoint(job.JobId).Packets);
        db.SealV5aPackets(job.JobId, new[] { actual });
        Assert.Single(db.ReadV5aCheckpoint(job.JobId).Packets);
    }

    [Fact]
    public void Charged_attempt_link_rejects_unknown_request_or_foreign_job()
    {
        var f = Setup();
        using var db = f.Store;
        var u = Speak(f);
        var job = Job(u);
        var claim = Claim(job, u);
        var packet = Packet(f, job, u, claim);
        db.RegisterV5aJob(job, Evaluation);
        db.SealV5aClaims(job.JobId, new[] { claim });
        db.SealV5aPackets(job.JobId, new[] { packet });

        Assert.Throws<InvalidDataException>(() =>
            db.LinkV5aChargedAttempt(job.JobId, Guid.NewGuid(),
                V4AttemptPhase.ClaimVerification, new[] { claim.ClaimId }));

        var extraction = Guid.NewGuid();
        Assert.True(db.TryReserveV4Attempt(job, extraction,
            V4AttemptPhase.ClaimExtraction));
        Assert.Throws<InvalidDataException>(() =>
            db.LinkV5aChargedAttempt(job.JobId, extraction,
                V4AttemptPhase.ClaimVerification, new[] { claim.ClaimId }));

        db.LinkV5aChargedAttempt(job.JobId, extraction,
            V4AttemptPhase.ClaimExtraction, Array.Empty<Guid>());
        db.LinkV5aChargedAttempt(job.JobId, extraction,
            V4AttemptPhase.ClaimExtraction, Array.Empty<Guid>());
        Assert.Single(db.ReadV5aCheckpoint(job.JobId).AttemptLinks);
        Assert.Equal(2, db.ReadV5aCheckpoint(job.JobId).Events
            .Count(e => e.Stage == V5aLedgerStage.AttemptLinked ||
                        e.Stage == V5aLedgerStage.PacketsSealed));
    }

    [Fact]
    public void Failed_or_skipped_claims_remain_explicit_not_automatically_clean()
    {
        var f = Setup();
        using var db = f.Store;
        var u = Speak(f);
        var job = Job(u);
        var c = Claim(job, u);
        db.RegisterV5aJob(job, Evaluation);
        db.SealV5aClaims(job.JobId, new[] { c });
        db.SealV5aPackets(job.JobId, Array.Empty<EvidencePacket>());
        var skipped = new VerificationClaimResult(
            c.ClaimId, VerificationOperationalStatus.SkippedForBudget,
            null, "Session inference budget exhausted.");
        db.RecordV5aClaimResult(job.JobId, skipped);
        Assert.Throws<InvalidDataException>(
            () => db.SealV5aSummary(job.JobId, Summary(job.JobId, 1, 1, 1, 0)));
        db.SealV5aSummary(job.JobId, Summary(job.JobId, 1, 0, 0, 0, skipped: 1));
        var checkpoint = db.ReadV5aCheckpoint(job.JobId);
        Assert.Equal(0, checkpoint.Summary!.VerifiedClaims);
        Assert.Equal(1, checkpoint.Summary.SkippedForBudget);
        Assert.Equal(VerificationCompletionStatus.Incomplete,
            checkpoint.Summary.CompletionStatus);
    }

    [Fact]
    public void Capped_or_interrupted_job_stays_nonterminal_until_explicit_summary()
    {
        var f = Setup();
        using var db = f.Store;
        var u = Speak(f);
        var job = Job(u);
        db.RegisterV5aJob(job, Evaluation);
        db.SealV5aClaims(job.JobId, Array.Empty<ClaimCandidate>());
        db.SealV5aPackets(job.JobId, Array.Empty<EvidencePacket>());
        var open = db.ReadV5aCheckpoint(job.JobId);
        Assert.False(open.IsTerminal);
        Assert.Null(open.Summary);
        Assert.Equal(3, open.Events.Count);
        // No method in V5a starts or retries inference on reopening this job.
    }

    [Fact]
    public void Immutable_sqlite_triggers_block_rewrites_of_frozen_source_and_events()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "busstop-v5a-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var f = Setup(path);
            Guid jobId;
            using (var db = f.Store)
            {
                var u = Speak(f);
                var job = Job(u);
                jobId = job.JobId;
                var claim = Claim(job, u);
                db.RegisterV5aJob(job, Evaluation);
                db.SealV5aClaims(jobId, new[] { claim });
                db.SealV5aPackets(jobId, new[] { Packet(f, job, u, claim) });
            }
            using var sql = new SqliteConnection($"Data Source={path}");
            sql.Open();
            foreach (var statement in new[]
            {
                "UPDATE verifier_v5a_jobs SET utterance_sha256='fake';",
                "DELETE FROM verifier_v5a_events;",
                "UPDATE verifier_v5a_packets SET canonical_json='{}';",
                "DELETE FROM verifier_v5a_claims;",
                "UPDATE verifier_v5a_schema SET version='future-v99';"
            })
            {
                using var cmd = sql.CreateCommand();
                cmd.CommandText = statement;
                Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
            }
            using var reopened = ConversationStore.OpenFile(path);
            reopened.InitializeSchema();
            Assert.Equal(3, reopened.ReadV5aCheckpoint(jobId).Events.Count);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void Altered_event_chain_fails_closed_with_no_automatic_reconstruction()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "busstop-v5a-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var f = Setup(path);
            Guid jobId;
            using (var db = f.Store)
            {
                var u = Speak(f);
                var job = Job(u);
                jobId = job.JobId;
                db.RegisterV5aJob(job, Evaluation);
            }
            using (var sql = new SqliteConnection($"Data Source={path}"))
            {
                sql.Open();
                using var cmd = sql.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO verifier_v5a_events
                      (job_id, sequence, event_key, stage, payload_json,
                       payload_sha256, previous_sha256, event_sha256, recorded_at)
                    VALUES (@job, 2, 'claims:sealed', 'ClaimsSealed', '{}',
                            'fake-payload-sha', 'fake-previous-sha',
                            'fake-event-sha', '2026-01-01T00:00:00+00:00');
                    """;
                cmd.Parameters.AddWithValue("@job", jobId.ToString("D"));
                cmd.ExecuteNonQuery();
            }
            using var reopened = ConversationStore.OpenFile(path);
            reopened.InitializeSchema();
            Assert.Throws<InvalidDataException>(
                () => reopened.ReadV5aCheckpoint(jobId));
            Assert.Throws<InvalidDataException>(
                () => reopened.SealV5aClaims(jobId, Array.Empty<ClaimCandidate>()));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void Incompatible_schema_marker_blocks_upgrade_without_clobbering_database()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "busstop-v5a-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var sql = new SqliteConnection($"Data Source={path}"))
            {
                sql.Open();
                using var cmd = sql.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE verifier_v5a_schema
                        (singleton INTEGER PRIMARY KEY, version TEXT NOT NULL);
                    INSERT INTO verifier_v5a_schema VALUES(1, 'future-v6-schema');
                    """;
                cmd.ExecuteNonQuery();
            }
            using var db = ConversationStore.OpenFile(path);
            Assert.Throws<InvalidDataException>(() => db.InitializeSchema());
            using var sqlAgain = new SqliteConnection($"Data Source={path}");
            sqlAgain.Open();
            using var check = sqlAgain.CreateCommand();
            check.CommandText =
                "SELECT version FROM verifier_v5a_schema WHERE singleton=1;";
            Assert.Equal("future-v6-schema", check.ExecuteScalar());
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}

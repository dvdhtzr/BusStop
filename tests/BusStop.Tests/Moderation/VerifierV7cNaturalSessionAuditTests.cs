using System.Security.Cryptography;
using System.Text;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Engine.Evaluation;
using BusStop.Engine.Verification;
using BusStop.Models;
using BusStop.Moderation;
using BusStop.Moderation.Verification;
using BusStop.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BusStop.Tests.Moderation;

public sealed class VerifierV7cNaturalSessionAuditTests
{
    private sealed class NoisyLocalModel : IModelClient
    {
        public string ModelName => "synthetic-v7c-test";
        public string ModelDigest => "synthetic-v7c-test-digest";
        public int Calls { get; private set; }
        public ModelResponse Decide(ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ModelResponse.TransportError();
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string DatabasePath { get; } = Path.Combine(
            Path.GetTempPath(), "busstop-v7c-" +
                Guid.NewGuid().ToString("N") + ".sqlite");
        public ConversationStore Store { get; }
        public SessionId Session { get; }
        public CharacterProfile[] Cast { get; }
        public NoisyLocalModel Model { get; } = new();
        public V4SessionPolicy Policy { get; }

        public Fixture()
        {
            Store = ConversationStore.OpenFile(DatabasePath);
            Store.InitializeSchema();
            Cast = new[]
            {
                new CharacterProfile(CharacterId.New(), 1, "Arthur",
                    "An inquisitive older commuter", new[] { "curious" }),
                new CharacterProfile(CharacterId.New(), 1, "Bea",
                    "A skeptical commuter", new[] { "skeptical" }),
                new CharacterProfile(CharacterId.New(), 1, "Cal",
                    "A lively commuter", new[] { "jocular" })
            };
            foreach (var person in Cast) Store.SaveCharacter(person);
            var briefing = new Briefing(BriefingId.New(),
                "Fictional city traffic report", "synthetic fixture",
                "local:safe-test", "The report counted three bus delays.");
            Store.SaveBriefing(briefing);
            Session = SessionId.New();
            Store.CreateSession(new Session(Session,
                Cast.Select(x => x.Id).ToArray(),
                SessionMode.FreshEncounter, briefing.Id,
                "floor-v7c", "synthetic-0", "test-only", SessionStatus.Active,
                DateTimeOffset.UtcNow, null));
            Policy = new V4SessionPolicy(
                EvidenceVerifierV4.ContractVersion,
                VerificationOperatingMode.Shadow,
                V3ClaimDetector.RuleVersion,
                V3ModelClaimExtractor.PromptVersion,
                EvidenceVerifierV4.VerificationPromptVersion,
                V3EvidencePacketAssembler.RetrievalSelectorVersion,
                EvidenceVerifierV4.VerificationSchemaVersion,
                Model.ModelName, Model.ModelDigest,
                0.1, false, 8, 3, 2, 2048, 8192);
            Store.PinV4VerifierPolicy(Session, Policy);
        }

        public (Utterance Speech, ShadowObservation Observation)
            Commit(string text, int speaker = 0)
        {
            var actor = Cast[speaker];
            var boundary = Store.MaxSequence(Session);
            var opportunity = new SpeakingOpportunity(
                OpportunityId.New(), Session, actor.Id,
                OpportunityReason.FairOrder, boundary,
                OpportunityStatus.Offered);
            Store.SaveOpportunity(opportunity, (int)boundary);
            var previous = Store.GetCommittedUtterances(Session).ToArray();
            var speech = Store.CommitUtterance(
                opportunity.Id, Session, actor.Id,
                text, null, null, DateTimeOffset.UtcNow);
            var candidate = new ModerationCandidate(
                Guid.NewGuid(), Session, opportunity.Id, actor.Id,
                boundary, text);
            return (speech,
                new ShadowObservation(candidate, speech.Id, previous));
        }

        public (V7cCoverageEnvelope Coverage, string Digest)
            BuildRun(int capacity = 8)
        {
            var worker = new V6cShadowVerifier(
                DatabasePath, Session, Policy, Model,
                sampleEvery: 1, queueCapacity: capacity);
            var t = new[]
            {
                Commit("The official report counted 12 bus delays.", 0),
                Commit("I lived in Elmvale all my life.", 1),
                Commit("Did you see the report, Arthur?", 2)
            };
            var original = Store.GetCommittedUtterances(Session);
            var before = V7cNaturalSessionAuditor.HashTranscript(original);
            foreach (var turn in t)
                worker.TryObserve(turn.Observation);
            Assert.Equal(0, Model.Calls);
            worker.DrainAsync().GetAwaiter().GetResult();
            var after = V7cNaturalSessionAuditor.HashTranscript(
                Store.GetCommittedUtterances(Session));
            var timing = new V7cSessionTiming(
                4500, null, 550, before, after);
            var envelope = new V7cCoverageEnvelope(
                V6cShadowContract.EvaluationKey,
                Session.ToString(), worker.Coverage,
                worker.Faults, timing, "Synthetic V7c model-free fixture");
            return (envelope, before);
        }

        public V7cPrivateAudit Audit(V7cCoverageEnvelope envelope)
        {
            using var reader = ConversationStore.OpenReadOnly(DatabasePath);
            return V7cNaturalSessionAuditor.Audit(reader, Session, envelope);
        }

        public void Dispose()
        {
            Store.Dispose();
            try { File.Delete(DatabasePath); } catch (IOException) { }
            try { File.Delete(DatabasePath + ".v5b.lock"); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Real_three_person_committed_session_can_be_audited_without_model_calls()
    {
        using var f = new Fixture();
        var (cov, digest) = f.BuildRun();
        var callsBefore = f.Model.Calls;
        var audit = f.Audit(cov);
        Assert.Equal(callsBefore, f.Model.Calls);
        Assert.True(audit.ThreeParticipantSession);
        Assert.True(audit.TranscriptSequenceValid);
        Assert.True(audit.TranscriptMutationCheckAvailable);
        Assert.True(audit.TranscriptUnchangedAcrossVerifierDrain);
        Assert.True(audit.CoverageInternallyConsistent);
        Assert.True(audit.CoverageMatchesCommittedCount);
        Assert.True(audit.LedgerMatchesCoverage);
        Assert.Equal(digest, audit.TranscriptSha256);
        Assert.Equal(3, audit.CommittedUtterances);
        Assert.Equal(3, audit.V6cSelected);
        Assert.Equal(3, audit.ValidatedLedgerJobs);
        Assert.Equal(3, audit.TerminalLedgerJobs);
        Assert.Equal(3, audit.ParticipantTurnCounts.Sum());
        Assert.True(audit.HostAutobiographyCandidates >= 1);
    }

    [Fact]
    public void Aggregates_do_not_include_private_transcript_or_speaker_identity()
    {
        using var f = new Fixture();
        var (cov, _) = f.BuildRun();
        var safe = V7cNaturalSessionAuditor.ExportSafe(f.Audit(cov));
        var json = System.Text.Json.JsonSerializer.Serialize(safe);
        Assert.DoesNotContain("Elmvale", json);
        Assert.DoesNotContain("Arthur", json);
        Assert.DoesNotContain("Bea", json);
        Assert.DoesNotContain("12 bus delays", json);
        Assert.DoesNotContain("SpeakerId", json);
        Assert.False(safe.HumanAuthenticityReviewCompleted);
        Assert.False(safe.CrashRecoveryGateTested);
        Assert.False(safe.BaselineLatencyComparisonComplete);
        Assert.False(safe.SemanticAcceptanceGranted);
        Assert.False(safe.EnforcementAuthorized);
    }

    [Fact]
    public void Deliberate_queue_overflow_is_reported_as_unverified_selection()
    {
        using var f = new Fixture();
        var (cov, _) = f.BuildRun(capacity: 1);
        var audit = f.Audit(cov);
        Assert.Equal(2, audit.V6cDropped);
        Assert.Equal(3, audit.V6cSelected);
        Assert.Equal(1, audit.ValidatedLedgerJobs);
        Assert.Equal(1, audit.TerminalLedgerJobs);
        Assert.True(audit.LedgerMatchesCoverage);
        Assert.True(audit.CoverageInternallyConsistent);
        Assert.True(audit.TranscriptUnchangedAcrossVerifierDrain);
    }

    [Fact]
    public void Mismatched_coverage_counts_are_visible_and_never_accepted()
    {
        using var f = new Fixture();
        var (cov, _) = f.BuildRun();
        var forged = cov with
        {
            Coverage = cov.Coverage with { CommittedObserved = 50 }
        };
        var audit = f.Audit(forged);
        Assert.False(audit.CoverageInternallyConsistent);
        Assert.False(audit.CoverageMatchesCommittedCount);
        Assert.False(V7cNaturalSessionAuditor.ExportSafe(audit)
            .SemanticAcceptanceGranted);
    }

    [Fact]
    public void Mutated_before_and_after_hashes_fail_verifier_immutability_check()
    {
        using var f = new Fixture();
        var (cov, _) = f.BuildRun();
        var altered = cov with
        {
            Timing = cov.Timing! with
            {
                TranscriptShaAfterVerifier = new string('a', 64)
            }
        };
        var audit = f.Audit(altered);
        Assert.True(audit.TranscriptMutationCheckAvailable);
        Assert.False(audit.TranscriptUnchangedAcrossVerifierDrain);
    }

    [Fact]
    public void Missing_preverifier_hash_is_explicitly_unmeasured_not_a_pass()
    {
        using var f = new Fixture();
        var (cov, _) = f.BuildRun();
        var old = cov with { Timing = null };
        var report = f.Audit(old);
        Assert.False(report.TranscriptMutationCheckAvailable);
        Assert.False(report.TranscriptUnchangedAcrossVerifierDrain);
        Assert.False(V7cNaturalSessionAuditor.ExportSafe(report)
            .SemanticAcceptanceGranted);
    }

    [Fact]
    public void SQLite_read_only_connection_does_not_allow_committed_speech_mutation()
    {
        using var f = new Fixture();
        var (cov, _) = f.BuildRun();
        using var reader = ConversationStore.OpenReadOnly(f.DatabasePath);
        Assert.Equal(3, reader.GetCommittedUtterances(f.Session).Count);
        Assert.Throws<SqliteException>(() =>
            reader.SaveCharacter(new CharacterProfile(CharacterId.New(),
                1, "Injected", "This must never persist", new[] { "test" })));
        var audit = V7cNaturalSessionAuditor.Audit(reader, f.Session, cov);
        Assert.Equal(3, audit.CommittedUtterances);
    }

    [Fact]
    public void Corrupt_v5a_event_is_rejected_instead_of_counted_clean()
    {
        using var f = new Fixture();
        var (cov, _) = f.BuildRun();
        var jobs = f.Store.ListV7cVerificationJobs(
            f.Session, V6cShadowContract.EvaluationKey);
        Assert.NotEmpty(jobs);
        using (var sql = new SqliteConnection(
            "Data Source=" + f.DatabasePath))
        {
            sql.Open();
            using var cmd = sql.CreateCommand();
            cmd.CommandText = """
                UPDATE verifier_v5a_events
                SET payload_sha256 = @tampered
                WHERE job_id = @job AND sequence = 1;
                """;
            cmd.Parameters.AddWithValue("@tampered", new string('f', 64));
            cmd.Parameters.AddWithValue("@job", jobs[0].ToString("D"));
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        Assert.Throws<InvalidDataException>(() => f.Audit(cov));
    }

    [Fact]
    public void Registered_job_interrupted_before_frozen_claims_is_not_a_completed_verdict()
    {
        using var f = new Fixture();
        var (speech, _) = f.Commit(
            "The official report counted 12 bus delays.", 0);
        var idInput = V6cShadowContract.EvaluationKey + "|" +
            speech.Session + "|" + speech.Opportunity + "|" + speech.Id;
        var jobId = new Guid(SHA256.HashData(
            Encoding.UTF8.GetBytes(idInput)).AsSpan(0, 16));
        var job = new VerificationJobIdentity(
            jobId, speech.Session, speech.Opportunity, speech.Id,
            speech.Speaker, speech.Sequence - 1,
            EvidenceVerifierV1.ContractVersion,
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(speech.Text))).ToLowerInvariant());
        using (var gate = V5bProcessGate.Acquire(f.DatabasePath))
            f.Store.RegisterV5aJob(job, V6cShadowContract.EvaluationKey);

        var hash = V7cNaturalSessionAuditor.HashTranscript(
            f.Store.GetCommittedUtterances(f.Session));
        var coverage = new V6cShadowCoverage(
            "shadow-verifier-v6c-1", f.Session, 1, 0, 1, 1, 0,
            1, 0, 0, 0, 0, "Interrupted after registration");
        var envelope = new V7cCoverageEnvelope(
            V6cShadowContract.EvaluationKey, f.Session.ToString(),
            coverage, Array.Empty<V6cShadowWorkerFault>(),
            new V7cSessionTiming(100, null, 10, hash, hash),
            "Synthetic interruption before claims were sealed");

        var audit = f.Audit(envelope);
        Assert.Equal(1, audit.ValidatedLedgerJobs);
        Assert.Equal(0, audit.TerminalLedgerJobs);
        Assert.Equal(1, audit.NonterminalLedgerJobs);
        Assert.Equal(0, audit.ChargedVerificationAttempts);
        Assert.True(audit.LedgerMatchesCoverage);
        Assert.False(V7cNaturalSessionAuditor.ExportSafe(audit)
            .CrashRecoveryGateTested);
        // V5c's separate explicit recovery workflow owns this job.
    }

    [Fact]
    public void Blank_worksheet_never_self_certifies_human_authenticity()
    {
        using var f = new Fixture();
        var (cov, _) = f.BuildRun();
        var audit = f.Audit(cov);
        var blank = V7cHumanReview.CreateBlankWorksheet(audit);
        Assert.Equal(3, blank.Turns.Count);
        Assert.All(blank.Turns, turn =>
            Assert.Null(turn.InventedPersonalHistory));
        Assert.Throws<InvalidDataException>(() =>
            V7cHumanReview.ValidateCompleted(audit, blank));
    }

    [Fact]
    public void Completed_self_attestation_summarizes_human_judgment_but_never_accepts()
    {
        using var f = new Fixture();
        var (cov, _) = f.BuildRun();
        var audit = f.Audit(cov);
        var blank = V7cHumanReview.CreateBlankWorksheet(audit);
        var filled = blank with
        {
            ReviewerAlias = "owner-local-test",
            ReviewedAtUtc = DateTimeOffset.UtcNow,
            NaturalConversationScore = 3,
            CharacterDistinctnessScore = 4,
            HumorAndSpontaneityScore = 3,
            ConstructiveDisagreementScore = 3,
            HistoryCoherenceScore = 2,
            ModeratorNonIntrusionScore = 4,
            Turns = Array.AsReadOnly(blank.Turns.Select((x, i) =>
                x with
                {
                    InventedPersonalHistory = i == 1,
                    DialogueMisattribution = false,
                    UnsupportedFactualCertainty = false,
                    UnnaturalRepetition = false,
                    TimidOrFormulaicSpeech = false,
                    HealthyCharacterDevelopment = i == 2
                }).ToArray())
        };
        var review = V7cHumanReview.ValidateCompleted(audit, filled);
        Assert.True(review.OwnerAttestedReviewCompleted);
        Assert.False(review.IndependentHumanReviewAuthenticated);
        Assert.Equal(1, review.FlaggedInventedHistories);
        Assert.Equal(1, review.PositiveCharacterDevelopments);
        Assert.False(review.SemanticAcceptanceGranted);
        Assert.False(review.EnforcementAuthorized);

        var altered = filled with
        {
            Turns = Array.AsReadOnly(filled.Turns.Select((x, i) =>
                i == 0 ? x with { UtteranceText = "Altered speech" } : x).ToArray())
        };
        Assert.Throws<InvalidDataException>(() =>
            V7cHumanReview.ValidateCompleted(audit, altered));
    }

    [Fact]
    public void Missing_utterance_in_review_prevents_false_completed_claim()
    {
        using var f = new Fixture();
        var (cov, _) = f.BuildRun();
        var audit = f.Audit(cov);
        var blank = V7cHumanReview.CreateBlankWorksheet(audit);
        Assert.Throws<InvalidDataException>(() =>
            V7cHumanReview.ValidateCompleted(audit,
                blank with
                {
                    Turns = Array.AsReadOnly(blank.Turns.Skip(1).ToArray())
                }));
    }
}

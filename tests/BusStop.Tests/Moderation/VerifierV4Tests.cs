using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Engine.Verification;
using BusStop.Models;
using BusStop.Moderation;
using BusStop.Moderation.Verification;
using BusStop.Persistence;

namespace BusStop.Tests.Moderation;

public sealed class VerifierV4Tests
{
    private sealed class ScriptedVerifier : IModelClient
    {
        public string ModelName => "v4-test";
        public string ModelDigest => "pinned-v4-digest";
        public int Calls { get; private set; }
        public ModelRequest? LastRequest { get; private set; }
        public Func<int, ModelResponse> Reply { get; set; } =
            _ => ModelResponse.Responded("{invalid");
        public ModelResponse Decide(ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Reply(Calls);
        }
    }

    private sealed record Fixture(
        ConversationStore Store, Session Session,
        CharacterProfile Speaker, CharacterProfile Other,
        Briefing Briefing);

    private static Fixture Setup(int maxSession = 8, int maxPerUtterance = 3,
        int maxVerify = 2, ModerationSessionPolicy? m1 = null,
        string briefingText = "The published report recorded 3 outages.")
    {
        var db = new ConversationStore("Data Source=:memory:");
        db.InitializeSchema();
        var arthur = new CharacterProfile(CharacterId.New(), 1, "Arthur",
            "No binding anchors are inferred from this prose.", new[] { "curious" });
        var bea = new CharacterProfile(CharacterId.New(), 1, "Bea",
            "A second fictional participant.", new[] { "warm" });
        db.SaveCharacter(arthur);
        db.SaveCharacter(bea);
        var briefing = new Briefing(BriefingId.New(), "Report", "test",
            "local-evidence:test", briefingText);
        db.SaveBriefing(briefing);
        var session = new Session(
            SessionId.New(), new[] { arthur.Id, bea.Id },
            SessionMode.FreshEncounter, briefing.Id, "floor-2",
            "test-model", "test-context", SessionStatus.Active,
            DateTimeOffset.UtcNow, null);
        db.CreateSession(session, m1);
        db.PinV4VerifierPolicy(session.Id, Policy(maxSession, maxPerUtterance, maxVerify));
        return new Fixture(db, session, arthur, bea, briefing);
    }

    private static V4SessionPolicy Policy(int maxSession = 8,
        int maxUtterance = 3, int maxVerification = 2) => new(
            EvidenceVerifierV4.ContractVersion, VerificationOperatingMode.Shadow,
            V3ClaimDetector.RuleVersion, V3ModelClaimExtractor.PromptVersion,
            EvidenceVerifierV4.VerificationPromptVersion,
            V3EvidencePacketAssembler.RetrievalSelectorVersion,
            EvidenceVerifierV4.VerificationSchemaVersion,
            "v4-test", "pinned-v4-digest", 0.1, false, maxSession,
            maxUtterance, maxVerification, 2_048, 8_192);

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static Utterance Speak(Fixture f, string text,
        CharacterProfile? speaker = null)
    {
        var person = speaker ?? f.Speaker;
        var boundary = f.Store.MaxSequence(f.Session.Id);
        var opportunity = new SpeakingOpportunity(
            OpportunityId.New(), f.Session.Id, person.Id,
            OpportunityReason.FairOrder, boundary, OpportunityStatus.Offered);
        f.Store.SaveOpportunity(opportunity, (int)boundary);
        return f.Store.CommitUtterance(
            opportunity.Id, f.Session.Id, person.Id,
            text, null, null, DateTimeOffset.UtcNow);
    }

    private static VerificationJobIdentity Job(Utterance utterance) => new(
        Guid.NewGuid(), utterance.Session, utterance.Opportunity,
        utterance.Id, utterance.Speaker, utterance.Sequence - 1,
        EvidenceVerifierV1.ContractVersion, Hash(utterance.Text));

    private static (V3ExtractionResult Extraction, V3PacketAssembly Packets)
        Prepare(Fixture f, Utterance utterance, VerificationJobIdentity job)
    {
        var extraction = V3ClaimDetector.Detect(job, utterance);
        var packets = new V3EvidencePacketAssembler(f.Store).Assemble(
            job, utterance, extraction);
        return (extraction, packets);
    }

    private static (V3ExtractionResult Extraction, V3PacketAssembly Packets)
        PrepareOne(Fixture f, Utterance utterance,
        VerificationJobIdentity job, ClaimCategory category,
        ClaimRhetoricalMode mode = ClaimRhetoricalMode.Asserted)
    {
        var claim = new ClaimCandidate(Guid.NewGuid(), job.JobId, 0,
            0, utterance.Text.Length, utterance.Text,
            category, mode, ClaimExtractionProvenance.HostRule,
            ClaimValidationStatus.HostValidated);
        var extraction = new V3ExtractionResult(
            new[] { claim }, V3ExtractionCoverage.DeterministicOnly,
            0, utterance.Text.Length, true, "one fully selected fixture candidate");
        return (extraction, new V3EvidencePacketAssembler(f.Store)
            .Assemble(job, utterance, extraction));
    }

    private static ModelResponse Reply(
        VerificationJobIdentity job, IReadOnlyList<V3PackagedClaim> packages,
        ClaimVerificationOutcome outcome,
        Func<V3PackagedClaim, string[]>? oppose = null,
        Func<V3PackagedClaim, string[]>? support = null)
    {
        var verdicts = packages.Select(p => new
        {
            claimId = p.Claim.ClaimId.ToString("D"),
            packetId = p.Packet.PacketId.ToString("D"),
            packetSha256 = p.Packet.Sha256,
            outcome = outcome.ToString(),
            supportingReferences = support?.Invoke(p) ?? Array.Empty<string>(),
            opposingReferences = oppose?.Invoke(p) ?? Array.Empty<string>(),
            problematicText = outcome == ClaimVerificationOutcome.ConfirmedContradiction
                ? p.Claim.ClaimText : "",
            explanation = "Private model candidate verdict, subject to owner semantic calibration."
        }).ToArray();
        return ModelResponse.Responded(JsonSerializer.Serialize(new
        {
            jobId = job.JobId.ToString("D"),
            judgments = verdicts
        }));
    }

    [Fact]
    public void Unenrolled_or_unsupported_policy_fails_closed_without_inference()
    {
        using var db = new ConversationStore("Data Source=:memory:");
        db.InitializeSchema();
        Assert.Null(db.GetV4VerifierPolicy(SessionId.New()));
        Assert.Throws<InvalidDataException>(() =>
            (Policy() with { Mode = VerificationOperatingMode.Disabled }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (Policy() with { MaxCallsPerUtterance = 4 }).Validate());

        using var f = Setup().Store;
        var policy = f.GetV4VerifierPolicy(SessionId.New());
        Assert.Null(policy);
    }

    [Fact]
    public void Valid_source_contradiction_requires_authorized_source_reference()
    {
        var fixture = Setup();
        using var db = fixture.Store;
        var u = Speak(fixture, "The report counted 12 outages.");
        var job = Job(u);
        var p = PrepareOne(fixture, u, job, ClaimCategory.SourceFact);
        var model = new ScriptedVerifier
        {
            Reply = _ => Reply(job, p.Packets.Packets,
                ClaimVerificationOutcome.ConfirmedContradiction,
                opp => opp.Packet.Content.Items
                    .Where(i => i.Kind == EvidenceItemKind.Briefing)
                    .Select(i => i.ReferenceId).ToArray())
        };

        var report = new V4GroundedVerifier(db).Verify(
            job, u, p.Extraction, p.Packets, model);

        Assert.Equal(1, model.Calls);
        Assert.Equal(1, report.VerificationAttemptsThisRun);
        Assert.True(report.HasMaterialContradiction);
        Assert.Equal(1, report.Summary.ConfirmedContradictions);
        Assert.Equal(1, report.Summary.VerifiedClaims);
        Assert.Equal(VerificationCompletionStatus.Complete,
            report.Summary.CompletionStatus);
        Assert.All(db.GetModeratorReservations(job.SessionId),
            r => Assert.Equal(ModerationAttemptStatus.Completed, r.Status));
        Assert.NotNull(model.LastRequest?.JsonSchema);
    }

    [Fact]
    public void Missing_biographical_fact_is_insufficient_not_material_contradiction()
    {
        var fixture = Setup();
        using var db = fixture.Store;
        var u = Speak(fixture, "I worked as a pilot for years.");
        var job = Job(u);
        var p = PrepareOne(fixture, u, job, ClaimCategory.CharacterIdentity);
        var model = new ScriptedVerifier();
        var report = new V4GroundedVerifier(db).Verify(
            job, u, p.Extraction, p.Packets, model);
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, report.VerificationAttemptsThisRun);
        Assert.False(report.HasMaterialContradiction);
        Assert.Equal(1, report.Summary.InsufficientEvidence);
        Assert.Equal(VerificationOperationalStatus.Completed,
            report.ClaimResults[0].Status);
        Assert.Empty(db.GetModeratorReservations(job.SessionId));
    }

    [Fact]
    public void Questions_humor_and_quotes_cannot_turn_into_material_violations()
    {
        var fixture = Setup();
        using var db = fixture.Store;
        var u = Speak(fixture, "Could there have been 12 outages?");
        var job = Job(u);
        var p = PrepareOne(fixture, u, job, ClaimCategory.SourceFact,
            ClaimRhetoricalMode.Question);
        var model = new ScriptedVerifier();
        var report = new V4GroundedVerifier(db).Verify(
            job, u, p.Extraction, p.Packets, model);
        Assert.Equal(0, model.Calls);
        Assert.Equal(1, report.Summary.NoMaterialIssues);
        Assert.Equal(0, report.Summary.ConfirmedContradictions);
    }

    [Fact]
    public void Malformed_verdict_retry_consumes_two_new_shared_requests()
    {
        var fixture = Setup();
        using var db = fixture.Store;
        var u = Speak(fixture, "The report counted 12 outages.");
        var job = Job(u);
        var p = PrepareOne(fixture, u, job, ClaimCategory.SourceFact);
        var model = new ScriptedVerifier
        {
            Reply = call => call == 1 ? ModelResponse.Responded("{bad json")
                : Reply(job, p.Packets.Packets, ClaimVerificationOutcome.NoMaterialIssue)
        };
        var report = new V4GroundedVerifier(db).Verify(
            job, u, p.Extraction, p.Packets, model);
        Assert.Equal(2, model.Calls);
        Assert.Equal(2, report.VerificationAttemptsThisRun);
        Assert.Equal(2, report.ChargedRequestIds.Distinct().Count());
        Assert.Equal(1, report.Summary.NoMaterialIssues);
        Assert.Equal(2, db.GetModeratorReservations(job.SessionId).Count);
        Assert.Equal(2, db.GetV4AttemptCounts(job).UtteranceVerificationAttempts);

        Assert.False(db.TryReserveV4Attempt(job, Guid.NewGuid(),
            V4AttemptPhase.ClaimVerification));
    }

    [Fact]
    public void Transport_failures_charge_retries_and_never_become_no_issue()
    {
        var fixture = Setup();
        using var db = fixture.Store;
        var u = Speak(fixture, "The report counted 12 outages.");
        var job = Job(u);
        var p = PrepareOne(fixture, u, job, ClaimCategory.SourceFact);
        var model = new ScriptedVerifier { Reply = _ => ModelResponse.Timeout() };
        var report = new V4GroundedVerifier(db).Verify(
            job, u, p.Extraction, p.Packets, model);

        Assert.Equal(2, model.Calls);
        Assert.Equal(2, report.VerificationAttemptsThisRun);
        Assert.False(report.HasMaterialContradiction);
        Assert.Equal(0, report.Summary.VerifiedClaims);
        Assert.Equal(1, report.Summary.FailedClaims);
        Assert.Equal(VerificationOperationalStatus.TransportFailure,
            report.ClaimResults[0].Status);
        Assert.Equal(VerificationCompletionStatus.Incomplete,
            report.Summary.CompletionStatus);
        Assert.Equal(2, db.GetModeratorReservations(job.SessionId).Count);
    }

    [Fact]
    public void Invalid_packet_references_never_create_a_completed_judgment()
    {
        var fixture = Setup();
        using var db = fixture.Store;
        var u = Speak(fixture, "The report counted 12 outages.");
        var job = Job(u);
        var p = PrepareOne(fixture, u, job, ClaimCategory.SourceFact);
        var model = new ScriptedVerifier
        {
            Reply = _ => Reply(job, p.Packets.Packets,
                ClaimVerificationOutcome.ConfirmedContradiction,
                _ => new[] { "fabricated:unseen" })
        };
        var report = new V4GroundedVerifier(db).Verify(
            job, u, p.Extraction, p.Packets, model);
        Assert.Equal(2, model.Calls);
        Assert.Equal(0, report.Summary.ConfirmedContradictions);
        Assert.Equal(1, report.Summary.FailedClaims);
        Assert.Equal(VerificationOperationalStatus.InvalidResponse,
            report.ClaimResults[0].Status);
        Assert.False(report.HasMaterialContradiction);
    }

    [Fact]
    public void Nonbinding_provisional_self_history_cannot_support_a_direct_violation()
    {
        var fixture = Setup();
        using var db = fixture.Store;
        var earlier = Speak(fixture, "I once operated a factory.");
        var eventRecord = db.RecordVerifierHistoryEvent(new CharacterHistoryEvent(
            Guid.NewGuid(), fixture.Speaker.Id, "work", earlier.Text,
            earlier.Session, earlier.Id, earlier.Sequence,
            CharacterHistoryEventKind.Provisional, null, null,
            DateTimeOffset.MinValue));
        var u = Speak(fixture, "I never operated a factory.");
        var job = Job(u);
        var p = PrepareOne(fixture, u, job, ClaimCategory.PersonalHistory);
        Assert.Contains(p.Packets.Packets[0].Packet.Content.Items,
            x => x.AuthorityTier == EvidenceAuthorityTier.ProvisionalRecollection);

        var model = new ScriptedVerifier
        {
            Reply = _ => Reply(job, p.Packets.Packets,
                ClaimVerificationOutcome.ConfirmedContradiction,
                package => new[] { package.Packet.Content.Items[0].ReferenceId })
        };
        var result = new V4GroundedVerifier(db).Verify(
            job, u, p.Extraction, p.Packets, model);
        Assert.False(result.HasMaterialContradiction);
        Assert.Equal(1, result.Summary.FailedClaims);
        Assert.Equal(VerificationOperationalStatus.InvalidResponse,
            result.ClaimResults[0].Status);
    }

    [Fact]
    public void Session_and_utterance_budgets_share_the_legacy_moderator_table()
    {
        var m1 = ModerationSessionPolicy.Disabled with
        {
            Mode = ModerationOperatingMode.Shadow,
            RuleVersion = ShadowModerationWorker.CurrentRuleVersion,
            PromptVersion = ShadowModerationWorker.CurrentPromptVersion,
            ModelName = "v4-test",
            ModelDigest = "pinned-v4-digest",
            Temperature = 0.1, Think = false,
            MaxModeratorCalls = 2, ShadowSampleEvery = 1
        };
        var fixture = Setup(maxSession: 8, m1: m1);
        using var db = fixture.Store;
        var u = Speak(fixture, "The report counted 12 outages.");
        var job = Job(u);

        // One M1 request already occupied a slot in the shared table.
        var m1Id = Guid.NewGuid();
        db.ReserveModeratorAttempt(
            fixture.Session.Id, m1Id, u.Opportunity,
            ModerationInferencePurpose.ShadowSampling, DateTimeOffset.UtcNow);
        db.FinalizeModeratorAttempt(
            fixture.Session.Id, m1Id, ModerationAttemptStatus.Completed);

        var first = Guid.NewGuid();
        Assert.True(db.TryReserveV4Attempt(
            job, first, V4AttemptPhase.ClaimExtraction));
        db.FinalizeModeratorAttempt(
            fixture.Session.Id, first, ModerationAttemptStatus.Interrupted);
        Assert.False(db.TryReserveV4Attempt(
            job, Guid.NewGuid(), V4AttemptPhase.ClaimVerification));
        Assert.Equal(2, db.GetV4AttemptCounts(job).SessionSharedAttempts);
        Assert.Equal(1, db.GetV4AttemptCounts(job).UtteranceExtractionAttempts);
        Assert.Throws<InvalidOperationException>(() =>
            db.TryReserveV4Attempt(job, first, V4AttemptPhase.ClaimExtraction));
    }

    [Fact]
    public void Three_attempt_ceiling_includes_extraction_and_failed_requests_across_job_ids()
    {
        var fixture = Setup();
        using var db = fixture.Store;
        var u = Speak(fixture, "The report counted 12 outages.");
        var job = Job(u);
        var extractionId = Guid.NewGuid();
        Assert.True(db.TryReserveV4Attempt(
            job, extractionId, V4AttemptPhase.ClaimExtraction));
        db.FinalizeModeratorAttempt(job.SessionId, extractionId,
            ModerationAttemptStatus.Interrupted);

        var verify1 = Guid.NewGuid();
        var verify2 = Guid.NewGuid();
        Assert.True(db.TryReserveV4Attempt(job, verify1, V4AttemptPhase.ClaimVerification));
        Assert.True(db.TryReserveV4Attempt(job, verify2, V4AttemptPhase.ClaimVerification));
        db.FinalizeModeratorAttempt(job.SessionId, verify1, ModerationAttemptStatus.Completed);
        db.FinalizeModeratorAttempt(job.SessionId, verify2, ModerationAttemptStatus.Interrupted);

        var newJobSameUtterance = job with { JobId = Guid.NewGuid() };
        Assert.False(db.TryReserveV4Attempt(newJobSameUtterance,
            Guid.NewGuid(), V4AttemptPhase.ClaimVerification));
        Assert.False(db.TryReserveV4Attempt(newJobSameUtterance,
            Guid.NewGuid(), V4AttemptPhase.ClaimExtraction));
        var counts = db.GetV4AttemptCounts(job);
        Assert.Equal(3, counts.UtteranceTotalAttempts);
        Assert.Equal(3, counts.SessionSharedAttempts);
        Assert.Equal(1, counts.UtteranceExtractionAttempts);
        Assert.Equal(2, counts.UtteranceVerificationAttempts);
    }

    [Fact]
    public void Exhausted_shared_budget_marks_unverified_claim_explicitly()
    {
        var fixture = Setup(maxSession: 1);
        using var db = fixture.Store;
        var u = Speak(fixture, "The report counted 12 outages.");
        var job = Job(u);
        var attempt = Guid.NewGuid();
        Assert.True(db.TryReserveV4Attempt(
            job, attempt, V4AttemptPhase.ClaimExtraction));
        db.FinalizeModeratorAttempt(job.SessionId, attempt,
            ModerationAttemptStatus.Interrupted);

        var p = PrepareOne(fixture, u, job, ClaimCategory.SourceFact);
        var model = new ScriptedVerifier();
        var report = new V4GroundedVerifier(db).Verify(
            job, u, p.Extraction, p.Packets, model);
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, report.VerificationAttemptsThisRun);
        Assert.Equal(1, report.Summary.SkippedForBudget);
        Assert.Equal(0, report.Summary.NoMaterialIssues);
        Assert.Equal(VerificationCompletionStatus.Incomplete,
            report.Summary.CompletionStatus);
    }

    [Fact]
    public void Host_reconciler_rejects_missing_or_fabricated_clean_claim_results()
    {
        var fixture = Setup();
        using var db = fixture.Store;
        var u = Speak(fixture, "The report counted 12 outages.");
        var job = Job(u);
        var p = PrepareOne(fixture, u, job, ClaimCategory.SourceFact);
        Assert.Throws<InvalidDataException>(() =>
            V4HostReconciler.Reconcile(job, p.Extraction, p.Packets,
                Array.Empty<VerificationClaimResult>(), 0, Array.Empty<Guid>()));

        var claim = p.Extraction.Claims[0];
        Assert.Throws<InvalidDataException>(() =>
            V4HostReconciler.Reconcile(job, p.Extraction, p.Packets,
                new[] { new VerificationClaimResult(claim.ClaimId,
                    VerificationOperationalStatus.Completed, null, null) },
                0, Array.Empty<Guid>()));
    }
}

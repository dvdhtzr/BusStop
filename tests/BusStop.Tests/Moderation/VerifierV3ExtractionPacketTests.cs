using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Domain;
using BusStop.Engine.Verification;
using BusStop.Models;
using BusStop.Moderation.Verification;
using BusStop.Persistence;

namespace BusStop.Tests.Moderation;

public sealed class VerifierV3ExtractionTests
{
    private sealed class ScriptedModel : IModelClient
    {
        public string ModelName => "v3-test-model";
        public string ModelDigest => "v3-test-digest";
        public int Calls { get; private set; }
        public ModelRequest? LastRequest { get; private set; }
        public Func<ModelRequest, ModelResponse> Respond { get; set; } =
            _ => ModelResponse.Responded("""{"claims": []}""");

        public ModelResponse Decide(ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Respond(request);
        }
    }

    private sealed class OneShotPermit : IV3ExtractionReservation
    {
        public bool Available { get; set; } = true;
        public int AcceptedAttempts { get; private set; }
        public bool TryReserve(VerificationJobIdentity job, out Guid attemptId)
        {
            if (!Available || AcceptedAttempts != 0)
            {
                attemptId = Guid.Empty;
                return false;
            }
            attemptId = Guid.NewGuid();
            AcceptedAttempts++;
            return true;
        }
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static (VerificationJobIdentity Job, Utterance Utterance)
        Synthetic(string text)
    {
        var u = new Utterance(
            UtteranceId.New(), SessionId.New(), OpportunityId.New(),
            CharacterId.New(), 1, text, null, null, DateTimeOffset.UtcNow);
        var job = new VerificationJobIdentity(
            Guid.NewGuid(), u.Session, u.Opportunity, u.Id, u.Speaker,
            0, EvidenceVerifierV1.ContractVersion, Hash(text));
        return (job, u);
    }

    private static VerificationSessionPolicy Pinned() =>
        new(EvidenceVerifierV1.ContractVersion, VerificationOperatingMode.Shadow,
            "v3-test-model", "v3-test-digest", 0.1, false,
            V3ClaimDetector.RuleVersion, V3ModelClaimExtractor.PromptVersion,
            V3EvidencePacketAssembler.RetrievalSelectorVersion, 6, 3);

    [Fact]
    public void Host_first_detection_finds_multi_claim_lines_and_preserves_stable_ids()
    {
        var f = Synthetic(
            "I worked at the FAA for 6 years. The report counted 12 flights; Bea said there were 3 delays.");
        var first = V3ClaimDetector.Detect(f.Job, f.Utterance);
        var replay = V3ClaimDetector.Detect(f.Job, f.Utterance);

        Assert.Contains(first.Claims,
            c => c.Category == ClaimCategory.CharacterIdentity &&
                c.ClaimText.Contains("I worked", StringComparison.Ordinal));
        Assert.Contains(first.Claims,
            c => c.Category == ClaimCategory.SourceFact && c.ClaimText.Contains("12"));
        Assert.Contains(first.Claims,
            c => c.Category == ClaimCategory.DialogueAttribution &&
                c.ClaimText.Contains("Bea said", StringComparison.Ordinal));
        Assert.True(first.Claims.Count >= 5);
        Assert.Equal(first.Claims.Select(x => x.ClaimId),
            replay.Claims.Select(x => x.ClaimId));
        Assert.All(first.Claims, c =>
        {
            Assert.Equal(c.ClaimText,
                f.Utterance.Text.Substring(c.Start, c.Length));
            Assert.Equal(ClaimValidationStatus.HostValidated, c.ValidationStatus);
        });
        Assert.Equal(0, first.ModelAttempts);
    }

    [Fact]
    public void Jokes_questions_conditionals_and_quoted_numbers_are_not_assumed_factual()
    {
        var f = Synthetic(
            "Just kidding, I flew 500 planes! Could there be 20? If 7 were late, that would be odd. Bea said \"12 flights\".");
        var found = V3ClaimDetector.Detect(f.Job, f.Utterance);
        Assert.Contains(found.Claims,
            c => c.ClaimText == "500" &&
                c.RhetoricalMode == ClaimRhetoricalMode.Humorous);
        Assert.Contains(found.Claims,
            c => c.ClaimText == "20" &&
                c.RhetoricalMode == ClaimRhetoricalMode.Question);
        Assert.Contains(found.Claims,
            c => c.ClaimText == "7" &&
                c.RhetoricalMode == ClaimRhetoricalMode.Conditional);
        Assert.Contains(found.Claims,
            c => c.ClaimText == "12" &&
                c.RhetoricalMode == ClaimRhetoricalMode.Quoted);
        // No V3 object can record a contradiction or block an utterance.
        Assert.IsType<V3ExtractionResult>(found);
    }

    [Fact]
    public void Bounded_host_scan_does_not_pretend_full_claim_coverage()
    {
        var f = Synthetic(new string('x', V3ClaimDetector.MaxScannedCharacters + 10) +
            " 999 flights.");
        var found = V3ClaimDetector.Detect(f.Job, f.Utterance);
        Assert.False(found.SelectionComplete);
        Assert.Equal(V3ExtractionCoverage.InputTruncated, found.Coverage);
        Assert.DoesNotContain(found.Claims, x => x.ClaimText.Contains("999"));
    }

    [Fact]
    public void Optional_model_adds_contextual_assertion_but_host_owns_ids_and_spans()
    {
        const string statement = "The factory closed";
        var f = Synthetic("There were 12 delays. " + statement + ".");
        var model = new ScriptedModel();
        model.Respond = _ => ModelResponse.Responded(
            JsonSerializer.Serialize(new
            {
                claims = new[]
                {
                    new {
                        speakerId = f.Job.SpeakerId.ToString(),
                        start = f.Utterance.Text.IndexOf(statement, StringComparison.Ordinal),
                        length = statement.Length,
                        text = statement,
                        category = "SourceFact",
                        rhetoricalMode = "Asserted"
                    }
                }
            }));
        var permit = new OneShotPermit();
        var result = V3ModelClaimExtractor.Extract(
            f.Job, f.Utterance, Pinned(), true, model, permit);

        Assert.Equal(V3ExtractionCoverage.ModelAugmented, result.Coverage);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, permit.AcceptedAttempts);
        Assert.Equal(1, result.ModelAttempts);
        Assert.NotNull(model.LastRequest?.JsonSchema);
        Assert.True(model.LastRequest?.MaxGeneratedTokens > 0);
        Assert.Contains(result.Claims,
            x => x.ClaimText == statement &&
                 x.ExtractionProvenance == ClaimExtractionProvenance.ModelProposed &&
                 x.ClaimId != Guid.Empty);
        Assert.Contains(result.Claims, x => x.ClaimText.Contains("12"));
        EvidenceVerificationValidator.ValidateClaims(result.Claims, f.Job, f.Utterance);

        var noSecondAttempt = V3ModelClaimExtractor.Extract(
            f.Job, f.Utterance, Pinned(), true, model, permit);
        Assert.Equal(V3ExtractionCoverage.ModelSkippedForBudget, noSecondAttempt.Coverage);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public void Unselected_or_unbudgeted_extraction_makes_zero_model_calls()
    {
        var f = Synthetic("The factory closed. 12 people left.");
        var model = new ScriptedModel();
        var permit = new OneShotPermit();
        Assert.Equal(V3ExtractionCoverage.ModelSkippedBySampling,
            V3ModelClaimExtractor.Extract(
                f.Job, f.Utterance, Pinned(), false, model, permit).Coverage);
        Assert.Equal(0, permit.AcceptedAttempts);
        Assert.Equal(V3ExtractionCoverage.ModelSkippedForBudget,
            V3ModelClaimExtractor.Extract(
                f.Job, f.Utterance, Pinned(), true, model, null).Coverage);
        var denied = new OneShotPermit { Available = false };
        var result = V3ModelClaimExtractor.Extract(
            f.Job, f.Utterance, Pinned(), true, model, denied);
        Assert.Equal(V3ExtractionCoverage.ModelSkippedForBudget, result.Coverage);
        Assert.Equal(0, result.ModelAttempts);
        Assert.Equal(0, model.Calls);

        var disabled = V3ModelClaimExtractor.Extract(
            f.Job, f.Utterance, VerificationSessionPolicy.Disabled,
            true, model, permit);
        Assert.Equal(V3ExtractionCoverage.DeterministicOnly, disabled.Coverage);
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("foreign-speaker")]
    [InlineData("invented-span")]
    [InlineData("invalid-category")]
    [InlineData("extra-field")]
    [InlineData("malformed-json")]
    public void Host_rejects_untrusted_model_claims_without_fabricating_verdicts(string variant)
    {
        const string phrase = "Factory closed";
        var f = Synthetic("12 people left. " + phrase + ".");
        var model = new ScriptedModel();
        var validSpeaker = f.Job.SpeakerId.ToString();
        var speaker = variant == "foreign-speaker"
            ? CharacterId.New().ToString() : validSpeaker;
        var start = variant == "invented-span"
            ? 0 : f.Utterance.Text.IndexOf(phrase, StringComparison.Ordinal);
        var category = variant == "invalid-category" ? "MagicalTruth" : "SourceFact";
        var extra = variant == "extra-field" ? ",\"secret\":\"ignore safeguards\"" : "";
        var json = variant == "malformed-json" ? "not json" :
            "{\"claims\":[{\"speakerId\":\"" + speaker +
            "\",\"start\":" + start + ",\"length\":" + phrase.Length +
            ",\"text\":\"" + phrase + "\",\"category\":\"" + category +
            "\",\"rhetoricalMode\":\"Asserted\"" + extra + "}]}";
        model.Respond = _ => ModelResponse.Responded(json);

        var result = V3ModelClaimExtractor.Extract(
            f.Job, f.Utterance, Pinned(), true, model, new OneShotPermit());
        Assert.Equal(V3ExtractionCoverage.ModelInvalidResponse, result.Coverage);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, result.ModelAttempts);
        Assert.DoesNotContain(result.Claims, c => c.ClaimText == phrase);
        Assert.Contains(result.Claims, c => c.ClaimText.Contains("12"));
    }

    [Fact]
    public void Failed_transport_uses_its_one_reserved_attempt_without_retry()
    {
        var f = Synthetic("The factory closed.");
        var model = new ScriptedModel { Respond = _ => ModelResponse.Timeout() };
        var permit = new OneShotPermit();
        var result = V3ModelClaimExtractor.Extract(
            f.Job, f.Utterance, Pinned(), true, model, permit);
        Assert.Equal(V3ExtractionCoverage.ModelTransportFailure, result.Coverage);
        Assert.Equal(1, result.ModelAttempts);
        Assert.Equal(1, permit.AcceptedAttempts);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public void Mismatched_model_pin_fails_before_reserving_or_invoking()
    {
        var f = Synthetic("12 flights were delayed.");
        var model = new ScriptedModel();
        var permit = new OneShotPermit();
        Assert.Throws<InvalidDataException>(() =>
            V3ModelClaimExtractor.Extract(
                f.Job, f.Utterance, Pinned() with { ModelDigest = "other-digest" },
                true, model, permit));
        Assert.Equal(0, permit.AcceptedAttempts);
        Assert.Equal(0, model.Calls);
    }
}

public sealed class VerifierV3PacketTests
{
    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static (ConversationStore Store, Session Session,
        CharacterProfile Speaker, CharacterProfile Other) Setup(
        string briefingText = "Three outages were reported.")
    {
        var store = new ConversationStore("Data Source=:memory:");
        store.InitializeSchema();
        var speaker = new CharacterProfile(
            CharacterId.New(), 1, "Arthur", "My own facts.", new[] { "curious" });
        var other = new CharacterProfile(
            CharacterId.New(), 1, "Bea", "My own history.", new[] { "warm" });
        store.SaveCharacter(speaker);
        store.SaveCharacter(other);
        var briefing = new Briefing(BriefingId.New(), "Official report",
            "test", "local-evidence:test", briefingText);
        store.SaveBriefing(briefing);
        var session = new Session(
            SessionId.New(), new[] { speaker.Id, other.Id },
            SessionMode.FreshEncounter, briefing.Id, "floor-2",
            "stub", "test", SessionStatus.Active, DateTimeOffset.UtcNow, null);
        store.CreateSession(session);
        return (store, session, speaker, other);
    }

    private static Utterance Speak(ConversationStore db, Session session,
        CharacterProfile speaker, string text)
    {
        var boundary = db.MaxSequence(session.Id);
        var opportunity = new SpeakingOpportunity(
            OpportunityId.New(), session.Id, speaker.Id, OpportunityReason.FairOrder,
            boundary, OpportunityStatus.Offered);
        db.SaveOpportunity(opportunity, (int)boundary);
        return db.CommitUtterance(
            opportunity.Id, session.Id, speaker.Id, text, null, null,
            DateTimeOffset.UtcNow);
    }

    private static VerificationJobIdentity Job(Utterance u) => new(
        Guid.NewGuid(), u.Session, u.Opportunity, u.Id, u.Speaker, u.Sequence - 1,
        EvidenceVerifierV1.ContractVersion, Hash(u.Text));

    [Fact]
    public void Frozen_packets_are_hash_stable_and_never_include_unexposed_source_passages()
    {
        var f = Setup();
        using var db = f.Store;

        const string source = "A public passage. A private unpublished passage.";
        var sha = Hash(source);
        var passage1 = new EvidencePassage(
            sha + ":p00001", 1, 0, "A public passage.".Length, "A public passage.");
        var passage2 = new EvidencePassage(
            sha + ":p00002", 2, source.IndexOf("A private", StringComparison.Ordinal),
            "A private unpublished passage.".Length, "A private unpublished passage.");
        var snap = new EvidenceSnapshot(
            sha, new EvidenceSourceDescriptor(
                EvidenceSourceKind.UserDocument, "Source", "fixture", null, null, "test"),
            sha, DateTimeOffset.UtcNow, source, new[] { passage1, passage2 });
        db.SaveEvidenceSnapshot(snap);

        // Session evidence pins require a briefing URL referencing this snapshot.
        // Create another session with correct pin before any speaking opportunities.
        var pinBriefing = new Briefing(
            BriefingId.New(), "Official report", "fixture",
            "local-evidence:" + sha, "A public passage.");
        db.SaveBriefing(pinBriefing);
        var pinned = new Session(
            SessionId.New(), new[] { f.Speaker.Id, f.Other.Id }, SessionMode.FreshEncounter,
            pinBriefing.Id, "floor-2", "stub", "test",
            SessionStatus.Active, DateTimeOffset.UtcNow, null);
        db.CreateSession(pinned);
        db.PinSessionEvidence(pinned.Id, sha, new[] { passage1.PassageId });

        var utterance = Speak(db, pinned, f.Speaker, "The report counted 12 items.");
        var job = Job(utterance);
        var claims = V3ClaimDetector.Detect(job, utterance);
        var assembler = new V3EvidencePacketAssembler(db);
        var a = assembler.Assemble(job, utterance, claims);
        var b = assembler.Assemble(job, utterance, claims);

        Assert.NotEmpty(a.Packets);
        Assert.Equal(a.Packets.Select(x => x.Packet.Sha256),
            b.Packets.Select(x => x.Packet.Sha256));
        Assert.All(a.Packets, p =>
        {
            EvidenceVerificationValidator.ValidatePacket(
                p.Packet, p.Claim, job, utterance, p.HostScope);
            Assert.DoesNotContain(p.Packet.Content.Items,
                x => x.Excerpt.Contains("private unpublished", StringComparison.Ordinal));
            Assert.Contains(p.Packet.Content.Items,
                x => x.Kind == EvidenceItemKind.Passage &&
                     x.Excerpt == "A public passage.");
        });
        Assert.Throws<InvalidDataException>(() =>
        {
            var first = a.Packets[0];
            EvidenceVerificationValidator.ValidatePacket(
                first.Packet with { Sha256 = new string('0', 64) },
                first.Claim, job, utterance, first.HostScope);
        });
    }

    [Fact]
    public void Packet_limits_record_omitted_evidence_and_missing_claims_as_partial()
    {
        var f = Setup(new string('X', 6_000));
        using var db = f.Store;
        var u = Speak(db, f.Session, f.Speaker,
            "The figures were 12, 15, 17, and 19.");
        var job = Job(u);
        var extracted = V3ClaimDetector.Detect(job, u);
        var limited = new V3EvidencePacketAssembler(db).Assemble(job, u, extracted,
            new V3PacketLimits(MaxClaimsPerJob: 1,
                MaxItemsPerClaim: 1,
                MaxEvidenceUtf16CharsPerClaim: 128,
                MaxPacketUtf8Bytes: 1_024,
                MaxTotalPacketUtf8Bytes: 1_024));
        Assert.Single(limited.Packets);
        Assert.NotEmpty(limited.OmittedClaimIds);
        Assert.Empty(limited.Packets[0].Packet.Content.Items);
        Assert.Equal(EvidenceCompletenessStatus.Partial, limited.JobCoverage);
        Assert.Equal(EvidenceCompletenessStatus.Partial,
            limited.Packets[0].Packet.Content.Completeness.Status);
        Assert.True(limited.TotalPacketUtf8Bytes <= 1_024);
    }

    [Fact]
    public void Exact_copy_of_a_prior_heard_line_is_a_zero_call_signal_not_a_verdict()
    {
        var f = Setup();
        using var db = f.Store;
        Speak(db, f.Session, f.Other, "Echo the same line.");
        var repeated = Speak(db, f.Session, f.Speaker, "Echo the same line.");
        var job = Job(repeated);
        var allowed = db.SelectVerifierEvidence(job, repeated,
            ClaimCategory.DialogueAttribution);
        var signal = V3ClaimDetector.CheckExactCopy(repeated.Text, allowed.Scope);
        Assert.NotNull(signal);
        Assert.StartsWith("utterance:", signal.EvidenceReference);
        Assert.Equal(repeated.Text, signal.Text);
    }

    [Fact]
    public void Unresolved_third_person_biography_never_selects_speaker_private_canon()
    {
        var f = Setup();
        using var db = f.Store;
        db.SealVerifierAnchorSet(f.Speaker.Id, 1,
            new[] { new CharacterAnchor(
                f.Speaker.Id, 1, "secret-job", "secret submariner",
                CharacterAnchorExposure.SubjectOnly) },
            "owner:profile");
        db.PinVerifierCharacterVersion(f.Session.Id, f.Speaker.Id, 1);

        var utterance = Speak(db, f.Session, f.Speaker,
            "Bea worked as a nurse for 6 years.");
        var job = Job(utterance);
        var personal = V3ClaimDetector.Detect(job, utterance);
        var built = new V3EvidencePacketAssembler(db).Assemble(job, utterance, personal);

        // Model may label a third-person claim CharacterIdentity. The host must
        // not substitute the observer's own autobiography as "evidence".
        var thirdPerson = new ClaimCandidate(
            Guid.NewGuid(), job.JobId, 0, 0, utterance.Text.Length,
            utterance.Text, ClaimCategory.CharacterIdentity,
            ClaimRhetoricalMode.Asserted, ClaimExtractionProvenance.ModelProposed,
            ClaimValidationStatus.HostValidated);
        var sample = new V3ExtractionResult(
            new[] { thirdPerson }, V3ExtractionCoverage.ModelAugmented,
            1, utterance.Text.Length, true, "host test");
        var isolated = new V3EvidencePacketAssembler(db).Assemble(job, utterance, sample);
        Assert.Single(isolated.Packets);
        Assert.Empty(isolated.Packets[0].Packet.Content.Items);
        Assert.Equal(EvidenceCompletenessStatus.Unknown,
            isolated.Packets[0].Packet.Content.Completeness.Status);
        Assert.DoesNotContain(isolated.Packets[0].Packet.Content.Items,
            x => x.Excerpt.Contains("submariner", StringComparison.Ordinal));
    }
}

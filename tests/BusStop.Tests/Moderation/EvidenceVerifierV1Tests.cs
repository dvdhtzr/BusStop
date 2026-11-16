using System.Security.Cryptography;
using System.Text;
using BusStop.Domain;
using BusStop.Moderation;
using BusStop.Moderation.Verification;

namespace BusStop.Tests.Moderation;

public sealed class EvidenceVerifierV1Tests
{
    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static (
        VerificationJobIdentity Job,
        Utterance Utterance,
        ClaimCandidate Claim,
        EvidenceItem Item,
        VerificationEvidenceScope Scope,
        EvidencePacket Packet) Fixture()
    {
        const string spoken = "I spent five years at the FAA, and the report counted 12 outages.";
        var utterance = new Utterance(
            UtteranceId.New(), SessionId.New(), OpportunityId.New(),
            CharacterId.New(), 1, spoken, null, null, DateTimeOffset.UtcNow);
        var job = new VerificationJobIdentity(
            Guid.NewGuid(), utterance.Session, utterance.Opportunity, utterance.Id,
            utterance.Speaker, 0, EvidenceVerifierV1.ContractVersion, Hash(spoken));
        var text = "12 outages";
        var claim = new ClaimCandidate(
            Guid.NewGuid(), job.JobId, 0,
            spoken.IndexOf(text, StringComparison.Ordinal), text.Length, text,
            ClaimCategory.SourceFact, ClaimRhetoricalMode.Asserted,
            ClaimExtractionProvenance.HostRule);
        var item = new EvidenceItem(
            "briefing:outages-v1", EvidenceItemKind.Briefing,
            EvidenceAuthorityTier.SourceSnapshot, EvidenceVisibility.ExposedToSpeaker,
            "briefing-id", "source-v1", Hash("3 outages were recorded."),
            "3 outages were recorded.", 0);
        var scope = new VerificationEvidenceScope(
            job.SessionId, job.OpportunityId, job.SpeakerId, job.CommittedBoundary,
            new Dictionary<string, EvidenceItem>(StringComparer.Ordinal)
            {
                [item.ReferenceId] = item
            });
        var body = new EvidencePacketContent(
            EvidenceVerifierV1.ContractVersion, EvidenceVerifierV1.PacketSerializerVersion,
            job.JobId, claim.ClaimId, job.CommittedBoundary,
            "selector-v1", "prompt-v1", "schema-v1",
            new EvidenceCompleteness(
                EvidenceCompletenessStatus.CompleteForDeclaredScope,
                "speaker-exposed briefing", "exact briefing lookup", null),
            new[] { item });
        return (job, utterance, claim, item, scope,
            EvidencePacketFactory.Freeze(Guid.NewGuid(), body));
    }

    private static ClaimJudgment Contradiction(
        ClaimCandidate claim, EvidencePacket packet, string evidenceRef) =>
        new(claim.ClaimId, packet.PacketId, packet.Sha256,
            ClaimVerificationOutcome.ConfirmedContradiction,
            Array.Empty<string>(), new[] { evidenceRef },
            claim.ClaimText, "Briefing reports a different outage count.");

    [Fact]
    public void New_verifier_is_disabled_by_default_and_cannot_execute_enforcement()
    {
        EvidenceVerificationValidator.ValidatePolicy(VerificationSessionPolicy.Disabled);
        Assert.Equal(VerificationOperatingMode.Disabled, VerificationSessionPolicy.Disabled.Mode);
        Assert.Equal("shadow-m1-v1", ShadowResponseParser.ContractVersion);
        Assert.NotEqual(EvidenceVerifierV1.ContractVersion, ShadowResponseParser.ContractVersion);
        Assert.Equal(3, EvidenceVerifierV1.MaxAttemptsPerUtterance);

        var shadow = new VerificationSessionPolicy(
            EvidenceVerifierV1.ContractVersion, VerificationOperatingMode.Shadow,
            "local-model", "pinned-digest", 0.1, false,
            "rules-v1", "prompt-v1", "selector-v1", 9, 3);
        EvidenceVerificationValidator.ValidatePolicy(shadow);

        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePolicy(shadow with { MaxUtteranceAttempts = 4 }));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePolicy(shadow with { ModelDigest = null }));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePolicy(shadow with { ContractVersion = "shadow-m1-v1" }));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePolicy(shadow with { Mode = (VerificationOperatingMode)42 }));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePolicy(
                VerificationSessionPolicy.Disabled with { MaxSessionAttempts = 1 }));
    }

    [Fact]
    public void Valid_contract_is_content_addressed_and_scope_checked()
    {
        var f = Fixture();
        EvidenceVerificationValidator.ValidateClaim(f.Claim, f.Job, f.Utterance);
        EvidenceVerificationValidator.ValidatePacket(
            f.Packet, f.Claim, f.Job, f.Utterance, f.Scope);

        var same = EvidencePacketFactory.Freeze(Guid.NewGuid(), f.Packet.Content);
        Assert.Equal(f.Packet.CanonicalJson, same.CanonicalJson);
        Assert.Equal(f.Packet.Sha256, same.Sha256);
        Assert.NotEqual(f.Packet.PacketId, same.PacketId);

        var judgment = Contradiction(f.Claim, f.Packet, f.Item.ReferenceId);
        EvidenceVerificationValidator.ValidateJudgment(
            judgment, f.Packet, f.Claim, f.Job, f.Utterance, f.Scope);
        EvidenceVerificationValidator.ValidateClaimResult(
            new VerificationClaimResult(f.Claim.ClaimId,
                VerificationOperationalStatus.Completed, judgment, null),
            f.Packet, f.Claim, f.Job, f.Utterance, f.Scope);

        EvidenceVerificationValidator.ValidateSummary(
            new VerificationObservationSummary(
                f.Job.JobId, 1, 1, 1, 0, 0, 0, 0, 0,
                VerificationCompletionStatus.Complete, "One selected and verified claim"));
    }

    [Fact]
    public void Invented_changed_and_outside_utf16_spans_fail_closed()
    {
        var f = Fixture();
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateClaim(
                f.Claim with { ClaimText = "13 outages" }, f.Job, f.Utterance));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateClaim(
                f.Claim with { Start = f.Utterance.Text.Length }, f.Job, f.Utterance));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateClaim(
                f.Claim with { JobId = Guid.NewGuid() }, f.Job, f.Utterance));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateClaim(
                f.Claim with { Category = (ClaimCategory)999 }, f.Job, f.Utterance));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateJob(
                f.Job with { UtteranceSha256 = new string('0', 64) }, f.Utterance));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateJob(
                f.Job with { CommittedBoundary = 2 }, f.Utterance));
    }

    [Fact]
    public void Evidence_reference_and_visibility_must_match_host_issued_scope()
    {
        var f = Fixture();
        var unseen = f.Item with { ReferenceId = "passage:unseen" };
        var packet = EvidencePacketFactory.Freeze(
            Guid.NewGuid(), f.Packet.Content with { Items = new[] { unseen } });
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePacket(
                packet, f.Claim, f.Job, f.Utterance, f.Scope));

        var altered = f.Item with { Excerpt = "18 outages were recorded." };
        packet = EvidencePacketFactory.Freeze(
            Guid.NewGuid(), f.Packet.Content with { Items = new[] { altered } });
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePacket(
                packet, f.Claim, f.Job, f.Utterance, f.Scope));

        var wrongVisibility = f.Item with { Visibility = EvidenceVisibility.OwnHistory };
        packet = EvidencePacketFactory.Freeze(
            Guid.NewGuid(), f.Packet.Content with { Items = new[] { wrongVisibility } });
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePacket(
                packet, f.Claim, f.Job, f.Utterance, f.Scope));

        var future = f.Item with { AvailableAtBoundary = 3 };
        var futureScope = f.Scope with
        {
            AuthorizedItems = new Dictionary<string, EvidenceItem>
            {
                [future.ReferenceId] = future
            }
        };
        packet = EvidencePacketFactory.Freeze(
            Guid.NewGuid(), f.Packet.Content with { Items = new[] { future } });
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePacket(
                packet, f.Claim, f.Job, f.Utterance, futureScope));
    }

    [Fact]
    public void Packet_mutation_hash_and_contract_mismatches_fail_closed()
    {
        var f = Fixture();
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePacket(
                f.Packet with { CanonicalJson = "{}" },
                f.Claim, f.Job, f.Utterance, f.Scope));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePacket(
                f.Packet with { Sha256 = new string('0', 64) },
                f.Claim, f.Job, f.Utterance, f.Scope));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePacket(
                EvidencePacketFactory.Freeze(Guid.NewGuid(),
                    f.Packet.Content with { SerializerVersion = "incompatible" }),
                f.Claim, f.Job, f.Utterance, f.Scope));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePacket(
                EvidencePacketFactory.Freeze(Guid.NewGuid(),
                    f.Packet.Content with { ClaimId = Guid.NewGuid() }),
                f.Claim, f.Job, f.Utterance, f.Scope));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePacket(
                f.Packet, f.Claim, f.Job, f.Utterance,
                f.Scope with { AsOfBoundary = 7 }));
    }

    [Fact]
    public void Judgments_cannot_claim_uncited_findings_or_unauthorized_sources()
    {
        var f = Fixture();
        var judgment = Contradiction(f.Claim, f.Packet, f.Item.ReferenceId);
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateJudgment(
                judgment with { OpposingReferences = Array.Empty<string>() },
                f.Packet, f.Claim, f.Job, f.Utterance, f.Scope));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateJudgment(
                judgment with { OpposingReferences = new[] { "passage:never-exposed" } },
                f.Packet, f.Claim, f.Job, f.Utterance, f.Scope));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateJudgment(
                judgment with { PacketSha256 = new string('0', 64) },
                f.Packet, f.Claim, f.Job, f.Utterance, f.Scope));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateJudgment(
                judgment with { ProblematicText = "not in the utterance" },
                f.Packet, f.Claim, f.Job, f.Utterance, f.Scope));

        // Membership in a scope is only technical validation; this test does
        // not assert that the model's contradiction is semantically justified.
        Assert.Equal(ClaimVerificationOutcome.ConfirmedContradiction, judgment.Outcome);
    }

    [Fact]
    public void No_issue_and_uncertainty_remain_separate_from_operational_failures()
    {
        var f = Fixture();
        foreach (var outcome in new[]
        {
            ClaimVerificationOutcome.NoMaterialIssue,
            ClaimVerificationOutcome.InsufficientEvidence
        })
        {
            var judgment = new ClaimJudgment(
                f.Claim.ClaimId, f.Packet.PacketId, f.Packet.Sha256,
                outcome, Array.Empty<string>(), Array.Empty<string>(), "",
                "No direct contradiction established from supplied evidence.");
            EvidenceVerificationValidator.ValidateJudgment(
                judgment, f.Packet, f.Claim, f.Job, f.Utterance, f.Scope);
        }

        EvidenceVerificationValidator.ValidateClaimResult(
            new VerificationClaimResult(f.Claim.ClaimId,
                VerificationOperationalStatus.SkippedForBudget, null, "Budget exhausted"),
            f.Packet, f.Claim, f.Job, f.Utterance, f.Scope);

        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateClaimResult(
                new VerificationClaimResult(f.Claim.ClaimId,
                    VerificationOperationalStatus.TransportFailure,
                    Contradiction(f.Claim, f.Packet, f.Item.ReferenceId), "Ollama unavailable"),
                f.Packet, f.Claim, f.Job, f.Utterance, f.Scope));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateClaimResult(
                new VerificationClaimResult(f.Claim.ClaimId,
                    VerificationOperationalStatus.Completed, null, null),
                f.Packet, f.Claim, f.Job, f.Utterance, f.Scope));

        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateSummary(
                new VerificationObservationSummary(
                    f.Job.JobId, 1, 0, 1, 0, 0, 0, 0, 0,
                    VerificationCompletionStatus.Complete, "Inconsistent")));
    }

    [Fact]
    public void Partial_scope_cannot_be_disguised_as_complete_scope()
    {
        var f = Fixture();
        var partial = f.Packet.Content with
        {
            Completeness = new EvidenceCompleteness(
                EvidenceCompletenessStatus.Partial,
                "five recent turns", "last five visible messages",
                "earlier visible dialogue was not searched")
        };
        EvidenceVerificationValidator.ValidatePacket(
            EvidencePacketFactory.Freeze(Guid.NewGuid(), partial),
            f.Claim, f.Job, f.Utterance, f.Scope);

        var falseComplete = partial with
        {
            Completeness = partial.Completeness with
            {
                Status = EvidenceCompletenessStatus.CompleteForDeclaredScope
            }
        };
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidatePacket(
                EvidencePacketFactory.Freeze(Guid.NewGuid(), falseComplete),
                f.Claim, f.Job, f.Utterance, f.Scope));
    }
}

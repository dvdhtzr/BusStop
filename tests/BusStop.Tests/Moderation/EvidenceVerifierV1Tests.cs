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

        // Freeze must take a snapshot, not retain a mutable caller-owned array.
        var input = new[] { f.Item };
        var frozen = EvidencePacketFactory.Freeze(Guid.NewGuid(),
            f.Packet.Content with { Items = input });
        input[0] = input[0] with { Excerpt = "mutated after freezing" };
        Assert.Equal(f.Item.Excerpt, frozen.Content.Items[0].Excerpt);
        EvidenceVerificationValidator.ValidatePacket(
            frozen, f.Claim, f.Job, f.Utterance, f.Scope);

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
        // Even a claimed "host validated" status cannot bypass a forged span.
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateClaim(
                f.Claim with
                {
                    ClaimText = "13 outages",
                    ValidationStatus = ClaimValidationStatus.HostValidated
                }, f.Job, f.Utterance));
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
    public void Distinct_overlapping_claims_are_allowed_but_duplicate_ids_or_indices_are_not()
    {
        var f = Fixture();
        var start = f.Utterance.Text.IndexOf("outages", StringComparison.Ordinal);
        var overlapping = f.Claim with
        {
            ClaimId = Guid.NewGuid(),
            Index = 1,
            Start = start,
            Length = "outages".Length,
            ClaimText = "outages"
        };
        EvidenceVerificationValidator.ValidateClaims(
            new[] { f.Claim, overlapping }, f.Job, f.Utterance);
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateClaims(
                new[] { f.Claim, overlapping with { Index = 0 } },
                f.Job, f.Utterance));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateClaims(
                new[] { f.Claim, overlapping with { ClaimId = f.Claim.ClaimId } },
                f.Job, f.Utterance));
    }

    [Fact]
    public void Conflicting_evidence_requires_distinct_opposing_and_supporting_references()
    {
        var f = Fixture();
        var other = f.Item with
        {
            ReferenceId = "briefing:second-exposed-report",
            SourceId = "second-report",
            SourceSha256 = Hash("12 outages were recorded."),
            Excerpt = "12 outages were recorded."
        };
        var scope = f.Scope with
        {
            AuthorizedItems = new Dictionary<string, EvidenceItem>(StringComparer.Ordinal)
            {
                [f.Item.ReferenceId] = f.Item,
                [other.ReferenceId] = other
            }
        };
        var packet = EvidencePacketFactory.Freeze(
            Guid.NewGuid(), f.Packet.Content with { Items = new[] { f.Item, other } });
        var conflicting = new ClaimJudgment(
            f.Claim.ClaimId, packet.PacketId, packet.Sha256,
            ClaimVerificationOutcome.ConflictingEvidence,
            new[] { other.ReferenceId }, new[] { f.Item.ReferenceId },
            f.Claim.ClaimText, "The two in-scope reports disagree on the count.");
        EvidenceVerificationValidator.ValidateJudgment(
            conflicting, packet, f.Claim, f.Job, f.Utterance, scope);

        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateJudgment(
                conflicting with { SupportingReferences = Array.Empty<string>() },
                packet, f.Claim, f.Job, f.Utterance, scope));
        Assert.Throws<InvalidDataException>(() =>
            EvidenceVerificationValidator.ValidateJudgment(
                conflicting with { SupportingReferences = new[] { f.Item.ReferenceId } },
                packet, f.Claim, f.Job, f.Utterance, scope));
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

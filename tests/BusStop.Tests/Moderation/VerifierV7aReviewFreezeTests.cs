using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BusStop.Moderation.Evaluation;
using BusStop.Moderation.Verification;
using Xunit;

namespace BusStop.Tests.Moderation;

public sealed class VerifierV7aReviewFreezeTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    // Synthetic unit-test-only reviews. Neither these labels nor their
    // numerical rules are an actual owner-approved acceptance benchmark.
    private static readonly DateTimeOffset ReviewedAt =
        new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static V7ReviewEvidence Evidence(
        string id, EvidenceItemKind kind, EvidenceAuthorityTier authority,
        EvidenceVisibility visibility, string excerpt) =>
        new(id, kind, authority, visibility, excerpt,
            authority == EvidenceAuthorityTier.SupersededRecollection);

    private static V7ReviewedCase Case(
        string partition, string id, ClaimCategory category,
        ClaimRhetoricalMode mode, string claim,
        ClaimVerificationOutcome expected,
        IReadOnlyList<V7ReviewEvidence> sources,
        IReadOnlyList<string>? supporting = null,
        IReadOnlyList<string>? opposing = null) =>
        new(partition + "-" + id, "synthetic-" + id,
            category, mode, claim, Array.AsReadOnly(sources.ToArray()),
            expected, supporting ?? Array.Empty<string>(),
            opposing ?? Array.Empty<string>(),
            "Human reviewer must independently inspect every fictional source before approval.",
            "synthetic-test-reviewer", ReviewedAt);

    private static V7ReviewedCorpus Corpus(V7Partition part)
    {
        var prefix = part == V7Partition.Development ? "dev" : "hold";
        // Deliberately different texts across partitions; any case
        // overlap would leak a holdout example into development work.
        var suffix = part == V7Partition.Development ? "oak" : "pine";
        var c = new[]
        {
            Case(prefix, "source-contradiction", ClaimCategory.SourceFact,
                ClaimRhetoricalMode.Asserted,
                $"The {suffix} report recorded twelve closures.",
                ClaimVerificationOutcome.ConfirmedContradiction,
                new[] { Evidence("src-1", EvidenceItemKind.Briefing,
                    EvidenceAuthorityTier.SourceSnapshot,
                    EvidenceVisibility.ExposedToSpeaker,
                    $"The {suffix} report recorded exactly three closures.") },
                opposing: new[] { "src-1" }),

            Case(prefix, "identity-contradiction", ClaimCategory.CharacterIdentity,
                ClaimRhetoricalMode.Asserted,
                $"I have always been a pilot in {suffix}.",
                ClaimVerificationOutcome.ConfirmedContradiction,
                new[] { Evidence("anchor-1", EvidenceItemKind.BioAnchor,
                    EvidenceAuthorityTier.BindingAnchor,
                    EvidenceVisibility.OwnHistory,
                    $"I never held a pilot license in {suffix}.") },
                opposing: new[] { "anchor-1" }),

            Case(prefix, "history-contradiction", ClaimCategory.PersonalHistory,
                ClaimRhetoricalMode.Asserted,
                $"I never lived in {suffix} village.",
                ClaimVerificationOutcome.ConfirmedContradiction,
                new[] { Evidence("history-1", EvidenceItemKind.History,
                    EvidenceAuthorityTier.EstablishedRecollection,
                    EvidenceVisibility.OwnHistory,
                    $"I lived in {suffix} village for ten years.") },
                opposing: new[] { "history-1" }),

            Case(prefix, "dialogue-contradiction", ClaimCategory.DialogueAttribution,
                ClaimRhetoricalMode.Asserted,
                $"Bea said twelve {suffix} buses were delayed.",
                ClaimVerificationOutcome.ConfirmedContradiction,
                new[] { Evidence("utterance-1", EvidenceItemKind.Utterance,
                    EvidenceAuthorityTier.PreviouslyHeardUtterance,
                    EvidenceVisibility.SharedDialogue,
                    $"Bea: Only three {suffix} buses were delayed, not twelve.") },
                opposing: new[] { "utterance-1" }),

            Case(prefix, "unknown-source", ClaimCategory.SourceFact,
                ClaimRhetoricalMode.Asserted,
                $"The {suffix} airline served hot lunch.",
                ClaimVerificationOutcome.InsufficientEvidence,
                Array.Empty<V7ReviewEvidence>()),

            Case(prefix, "conflicting-source", ClaimCategory.SourceFact,
                ClaimRhetoricalMode.Asserted,
                $"There were seven {suffix} route closures.",
                ClaimVerificationOutcome.ConflictingEvidence,
                new[]
                {
                    Evidence("src-a", EvidenceItemKind.Passage,
                        EvidenceAuthorityTier.SourceSnapshot,
                        EvidenceVisibility.ExposedToSpeaker,
                        $"The first {suffix} report said three route closures."),
                    Evidence("src-b", EvidenceItemKind.Passage,
                        EvidenceAuthorityTier.SourceSnapshot,
                        EvidenceVisibility.ExposedToSpeaker,
                        $"The second {suffix} report said seven route closures.")
                },
                supporting: new[] { "src-b" },
                opposing: new[] { "src-a" }),

            Case(prefix, "opinion", ClaimCategory.Other,
                ClaimRhetoricalMode.Speculative,
                $"I think {suffix} traffic was unpleasant.",
                ClaimVerificationOutcome.NoMaterialIssue,
                Array.Empty<V7ReviewEvidence>()),

            Case(prefix, "question", ClaimCategory.SourceFact,
                ClaimRhetoricalMode.Question,
                $"Did the {suffix} report mention twelve closures?",
                ClaimVerificationOutcome.NoMaterialIssue,
                new[] { Evidence("src-1", EvidenceItemKind.Briefing,
                    EvidenceAuthorityTier.SourceSnapshot,
                    EvidenceVisibility.ExposedToSpeaker,
                    $"The {suffix} report mentioned three closures.") })
        };
        return new V7ReviewedCorpus(
            V7FreezeContract.ReviewVersion, V7FreezeContract.LabelStatus,
            part, V7FreezeContract.ReviewMethod,
            "Synthetic test-only human review declaration; not real holdout.",
            Array.AsReadOnly(c));
    }

    private static V7AcceptanceCriteria Criteria()
    {
        V7LaneCriterion Rule(string lane, bool positives) =>
            new(lane, 1, 1, positives ? 1 : 0,
                0.75m, 0.10m,
                positives ? 0.90m : null,
                positives ? 0.80m : null,
                0.90m,
                positives ? null :
                    "No material-contradiction positives are expected in this protected lane.");

        return new V7AcceptanceCriteria(
            V7FreezeContract.ThresholdVersion, "unit-test-model",
            "test-digest-000001", "verification-prompt-test",
            "verification-schema-test", "reviewed-scorer-test",
            2, 1, 1,
            // Unit-test example values only: no owner has frozen a plan.
            0.90m, 0.90m, 0.90m, 0.80m, 0.80m,
            0.10m, 0.10m, 0.10m, 10000, 0.20m,
            Array.AsReadOnly(new[]
            {
                Rule("category:SourceFact", true),
                Rule("category:CharacterIdentity", true),
                Rule("category:PersonalHistory", true),
                Rule("category:DialogueAttribution", true),
                Rule("category:Other", false),
                Rule("rhetoric:ProtectedNonAsserted", false)
            }),
            new V7IntegrityRequirements(true, true, true, true, true, true, true),
            "The human reviewer must inspect false positives, private transcript drift and natural dialogue pressure.",
            "Unit tests only: these numerical values are illustrative and must never be used as live acceptance criteria.");
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Json);

    private static string Sha(string raw) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    private static (string Development, string Holdout, string Plan, string Approval)
        Inputs(
            V7ReviewedCorpus? development = null,
            V7ReviewedCorpus? holdout = null,
            V7AcceptanceCriteria? criteria = null,
            string? ownerDeclaration = null,
            string? approvalDevelopmentHash = null,
            DateTimeOffset? approvedAt = null)
    {
        var dev = Serialize(development ?? Corpus(V7Partition.Development));
        var hold = Serialize(holdout ?? Corpus(V7Partition.Holdout));
        var plan = Serialize(criteria ?? Criteria());
        var approval = Serialize(new V7OwnerFreezeApproval(
            V7FreezeContract.ApprovalVersion, "synthetic-test-owner",
            approvedAt ?? DateTimeOffset.UtcNow,
            approvalDevelopmentHash ?? Sha(dev), Sha(hold), Sha(plan),
            ownerDeclaration ?? V7FreezeContract.ApprovalDeclaration));
        return (dev, hold, plan, approval);
    }

    [Fact]
    public void Complete_owner_attested_preregistration_only_returns_nonacceptance_receipt()
    {
        var x = Inputs();
        var receipt = V7ReviewFreezeValidator.Validate(
            x.Development, x.Holdout, x.Plan, x.Approval);

        Assert.Equal(V7FreezeContract.ApprovalVersion, receipt.Version);
        Assert.Equal(Sha(x.Development), receipt.DevelopmentSha256);
        Assert.Equal(Sha(x.Holdout), receipt.HoldoutSha256);
        Assert.Equal(Sha(x.Plan), receipt.CriteriaSha256);
        Assert.Equal(Sha(x.Approval), receipt.ApprovalSha256);
        Assert.Equal(8, receipt.DevelopmentCases);
        Assert.Equal(8, receipt.HoldoutCases);
        Assert.False(receipt.SemanticAcceptanceGranted);
        Assert.False(receipt.EnforcementAuthorized);
        Assert.Contains("human attestation", receipt.Limitations);
        var publicJson = Serialize(receipt);
        Assert.DoesNotContain("Bea said", publicJson);
        Assert.DoesNotContain("pine report", publicJson);
        Assert.DoesNotContain("reviewRationale", publicJson);
        Assert.DoesNotContain("caseId", publicJson);
    }

    [Fact]
    public void Provisional_v6a_corpus_cannot_be_promoted_without_explicit_human_review()
    {
        var x = Inputs();
        var provisional = x.Development.Replace(
            V7FreezeContract.LabelStatus,
            V6EvaluationContract.LabelStatus, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                provisional, x.Holdout, x.Plan, x.Approval));
    }

    [Fact]
    public void Raw_threshold_or_review_changes_after_approval_invalidate_commitments()
    {
        var x = Inputs();
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development + " ", x.Holdout, x.Plan, x.Approval));

        var modifiedPlan = x.Plan.Replace(
            "\"minimumSelectionCoverage\":0.75",
            "\"minimumSelectionCoverage\":0.90",
            StringComparison.Ordinal);
        Assert.NotEqual(x.Plan, modifiedPlan);
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, modifiedPlan, x.Approval));
    }

    [Fact]
    public void Owner_attestation_must_use_exact_declaration_and_current_hashes()
    {
        var x = Inputs(ownerDeclaration: "The model said the reviews passed");
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));
        x = Inputs(approvalDevelopmentHash: new string('f', 64));
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));
    }

    [Fact]
    public void Approval_cannot_precede_the_recorded_review()
    {
        var x = Inputs(approvedAt: new DateTimeOffset(
            2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));
    }

    [Fact]
    public void Holdout_exact_cases_or_same_content_under_new_ids_are_rejected()
    {
        var development = Corpus(V7Partition.Development);
        var holdout = Corpus(V7Partition.Holdout);

        var reusedId = holdout.Cases.ToArray();
        reusedId[0] = reusedId[0] with
        {
            CaseId = development.Cases[0].CaseId
        };
        var x = Inputs(holdout: holdout with
        {
            Cases = Array.AsReadOnly(reusedId)
        });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));

        reusedId = holdout.Cases.ToArray();
        reusedId[0] = reusedId[0] with
        {
            ClaimText = development.Cases[0].ClaimText,
            Evidence = development.Cases[0].Evidence
        };
        x = Inputs(holdout: holdout with
        {
            Cases = Array.AsReadOnly(reusedId)
        });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));
    }

    [Fact]
    public void Reviewed_corpus_cannot_omit_categories_or_epistemic_outcomes()
    {
        var holdout = Corpus(V7Partition.Holdout);
        var withoutHistory = holdout with
        {
            Cases = Array.AsReadOnly(holdout.Cases.Where(c =>
                c.Category != ClaimCategory.PersonalHistory).ToArray())
        };
        var x = Inputs(holdout: withoutHistory);
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));

        var withoutUnknown = holdout with
        {
            Cases = Array.AsReadOnly(holdout.Cases.Where(c =>
                c.ReviewedOutcome != ClaimVerificationOutcome.InsufficientEvidence)
                .ToArray())
        };
        x = Inputs(holdout: withoutUnknown);
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));
    }

    [Fact]
    public void Acceptance_metrics_must_have_independent_cases_in_each_partition()
    {
        var plan = Criteria();
        var lanes = plan.Lanes.ToArray();
        lanes[0] = lanes[0] with { MinimumIndependentCases = 20 };
        var x = Inputs(criteria: plan with
        {
            Lanes = Array.AsReadOnly(lanes)
        });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));
    }

    [Fact]
    public void Missing_positive_or_precision_requirement_in_a_material_lane_is_rejected()
    {
        var plan = Criteria();
        var lanes = plan.Lanes.ToArray();
        lanes[0] = lanes[0] with
        {
            MinimumConfirmedPositiveCases = 0,
            MinimumConfirmedPrecision = null,
            MinimumEndToEndConfirmedRecall = null,
            NotApplicableReason = "No positives for fact contradictions."
        };
        var x = Inputs(criteria: plan with { Lanes = Array.AsReadOnly(lanes) });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));
    }

    [Fact]
    public void Missing_technical_or_human_quality_metric_block_freeze()
    {
        var plan = Criteria();
        var x = Inputs(criteria: plan with
        {
            MinimumClaimSpanRecall = -0.01m
        });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));

        x = Inputs(criteria: plan with
        {
            MaximumVerifierP95LatencyMilliseconds = 0
        });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));

        x = Inputs(criteria: plan with
        {
            MaximumParticipantLatencyRegressionFraction = 1.01m
        });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));

        x = Inputs(criteria: plan with
        {
            HumanQualityReviewProcedure = ""
        });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));
    }

    [Fact]
    public void Prepare_produces_only_unsigned_nonacceptance_hash_commitments()
    {
        var x = Inputs();
        var preparation = V7ReviewFreezeValidator.Prepare(
            x.Development, x.Holdout, x.Plan);
        Assert.False(preparation.HumanApprovalReceived);
        Assert.False(preparation.SemanticAcceptanceGranted);
        Assert.False(preparation.EnforcementAuthorized);
        Assert.Equal(Sha(x.Holdout), preparation.HoldoutSha256);
        Assert.Equal(Sha(x.Plan), preparation.CriteriaSha256);
        var serialized = Serialize(preparation);
        Assert.DoesNotContain("ReviewerId", serialized);
        Assert.DoesNotContain("caseId", serialized);
        Assert.DoesNotContain("pine report", serialized);
    }

    [Fact]
    public void Missing_criterion_lane_or_changed_integrity_boolean_blocks_freeze()
    {
        var plan = Criteria();
        var x = Inputs(criteria: plan with
        {
            Lanes = Array.AsReadOnly(plan.Lanes.Skip(1).ToArray())
        });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));

        x = Inputs(criteria: plan with
        {
            HardGates = plan.HardGates with { NoSpeechOrParticipantContextMutation = false }
        });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));
    }

    [Fact]
    public void Unreviewed_or_badly_cited_case_is_not_owner_ground_truth()
    {
        var dev = Corpus(V7Partition.Development);
        var cases = dev.Cases.ToArray();
        cases[0] = cases[0] with
        {
            OpposingReferences = new[] { "nonexistent-reference" }
        };
        var x = Inputs(development: dev with { Cases = Array.AsReadOnly(cases) });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));
        cases = dev.Cases.ToArray();
        cases[0] = cases[0] with { ReviewRationale = "" };
        x = Inputs(development: dev with { Cases = Array.AsReadOnly(cases) });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));
    }

    [Fact]
    public void Strict_json_contract_rejects_duplicate_labels_missing_fields_and_unknown_keys()
    {
        var x = Inputs();
        var doubled = x.Development.Replace(
            "\"labelStatus\":\"owner-reviewed-attested\"",
            "\"labelStatus\":\"owner-reviewed-attested\"," +
            "\"labelStatus\":\"owner-reviewed-attested\"",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                doubled, x.Holdout, x.Plan, x.Approval));

        var missing = x.Development.Replace(
            "\"reviewerId\":\"synthetic-test-reviewer\",", "",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                missing, x.Holdout, x.Plan, x.Approval));

        var extra = x.Plan.Replace(
            "\"thresholdRationale\":",
            "\"thresholdRationale\":\"injected\", \"unknown\":1, \"shadow\":",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, extra, x.Approval));
    }

    [Fact]
    public void Cross_partition_exact_duplicate_check_ignores_case_id_and_evidence_reference_id()
    {
        var dev = Corpus(V7Partition.Development);
        var hold = Corpus(V7Partition.Holdout);
        var same = dev.Cases[0];
        var altered = hold.Cases.ToArray();
        altered[0] = altered[0] with
        {
            ClaimText = same.ClaimText,
            Evidence = Array.AsReadOnly(same.Evidence.Select(ev =>
                ev with { ReferenceId = "renamed-ref" }).ToArray()),
            OpposingReferences = new[] { "renamed-ref" }
        };
        var x = Inputs(holdout: hold with { Cases = Array.AsReadOnly(altered) });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewFreezeValidator.Validate(
                x.Development, x.Holdout, x.Plan, x.Approval));
    }
}

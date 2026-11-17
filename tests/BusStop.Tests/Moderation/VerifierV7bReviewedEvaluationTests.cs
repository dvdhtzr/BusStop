using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BusStop.Engine.Evaluation;
using BusStop.Engine.Verification;
using BusStop.Models;
using BusStop.Moderation.Evaluation;
using BusStop.Moderation.Verification;
using Xunit;

namespace BusStop.Tests.Moderation;

public sealed class VerifierV7bReviewedEvaluationTests
{
    private const string FullDigest =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset ReviewTime =
        new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class ScriptedModel : IModelClient
    {
        private readonly Func<ModelRequest, ModelResponse> _response;
        private readonly string _digest;
        public ScriptedModel(Func<ModelRequest, ModelResponse> response,
            string? digest = null)
        {
            _response = response;
            _digest = digest ?? FullDigest;
        }
        public string ModelName => "synthetic-v7b-model";
        public string ModelDigest => _digest;
        public int Calls { get; private set; }
        public ModelResponse Decide(ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return _response(request);
        }
    }

    private static V7ReviewedCase Case(string part, string key,
        ClaimCategory category, ClaimRhetoricalMode rhetorical,
        string text, string claim,
        ClaimVerificationOutcome expected,
        IReadOnlyList<V7ReviewEvidence> evidence,
        IReadOnlyList<string>? support = null,
        IReadOnlyList<string>? oppose = null,
        bool? extract = null)
    {
        var offset = text.IndexOf(claim, StringComparison.Ordinal);
        Assert.True(offset >= 0);
        return new V7ReviewedCase(
            part + "-" + key, "synthetic-" + key, category, rhetorical,
            text, offset, claim.Length,
            extract ?? rhetorical == ClaimRhetoricalMode.Asserted,
            claim, Array.AsReadOnly(evidence.ToArray()), expected,
            support ?? Array.Empty<string>(),
            oppose ?? Array.Empty<string>(),
            "Fictional synthetic human attestation for unit-test control only.",
            "synthetic-test-reviewer", ReviewTime);
    }

    private static V7ReviewEvidence Evidence(string id, string text,
        EvidenceItemKind kind = EvidenceItemKind.Briefing,
        EvidenceAuthorityTier tier = EvidenceAuthorityTier.SourceSnapshot,
        EvidenceVisibility visibility = EvidenceVisibility.ExposedToSpeaker) =>
        new(id, kind, tier, visibility, text, false);

    private static V7ReviewedCorpus Corpus(V7Partition partition)
    {
        var part = partition == V7Partition.Development ? "dev" : "hold";
        var town = partition == V7Partition.Development ? "Elmvale" : "Pinewick";
        var items = new[]
        {
            Case(part, "source", ClaimCategory.SourceFact,
                ClaimRhetoricalMode.Asserted,
                $"The {town} report recorded 12 outages.",
                "12 outages",
                ClaimVerificationOutcome.ConfirmedContradiction,
                new[] { Evidence("src-1",
                    $"The {town} report recorded 3 outages.") },
                oppose: new[] { "src-1" }),
            Case(part, "identity", ClaimCategory.CharacterIdentity,
                ClaimRhetoricalMode.Asserted,
                $"I have always been a pilot in {town}.",
                $"I have always been a pilot in {town}",
                ClaimVerificationOutcome.ConfirmedContradiction,
                new[] { Evidence("bio-1",
                    $"I have never held a pilot license in {town}.",
                    EvidenceItemKind.BioAnchor, EvidenceAuthorityTier.BindingAnchor,
                    EvidenceVisibility.OwnHistory) },
                oppose: new[] { "bio-1" }),
            Case(part, "history", ClaimCategory.PersonalHistory,
                ClaimRhetoricalMode.Asserted,
                $"I never lived in {town}.",
                $"I never lived in {town}",
                ClaimVerificationOutcome.ConfirmedContradiction,
                new[] { Evidence("history-1",
                    $"I lived in {town} for ten years.",
                    EvidenceItemKind.History,
                    EvidenceAuthorityTier.EstablishedRecollection,
                    EvidenceVisibility.OwnHistory) },
                oppose: new[] { "history-1" }),
            Case(part, "dialogue", ClaimCategory.DialogueAttribution,
                ClaimRhetoricalMode.Asserted,
                $"Bea said there were 12 delayed buses in {town}.",
                $"Bea said there were 12 delayed buses in {town}",
                ClaimVerificationOutcome.ConfirmedContradiction,
                new[] { Evidence("said-1",
                    $"Bea: There were 3 delayed buses in {town}.",
                    EvidenceItemKind.Utterance,
                    EvidenceAuthorityTier.PreviouslyHeardUtterance,
                    EvidenceVisibility.SharedDialogue) },
                oppose: new[] { "said-1" }),
            Case(part, "unknown", ClaimCategory.SourceFact,
                ClaimRhetoricalMode.Asserted,
                $"The airline served soup in {town}.",
                $"The airline served soup in {town}",
                ClaimVerificationOutcome.InsufficientEvidence,
                Array.Empty<V7ReviewEvidence>()),
            Case(part, "conflict", ClaimCategory.SourceFact,
                ClaimRhetoricalMode.Asserted,
                $"There were 7 road closures in {town}.",
                $"There were 7 road closures in {town}",
                ClaimVerificationOutcome.ConflictingEvidence,
                new[]
                {
                    Evidence("src-a", $"Report A: 3 road closures in {town}."),
                    Evidence("src-b", $"Report B: 7 road closures in {town}.")
                },
                support: new[] { "src-b" }, oppose: new[] { "src-a" }),
            Case(part, "opinion", ClaimCategory.Other,
                ClaimRhetoricalMode.Speculative,
                $"My opinion is that {town} traffic is stressful.",
                $"My opinion is that {town} traffic is stressful",
                ClaimVerificationOutcome.NoMaterialIssue,
                Array.Empty<V7ReviewEvidence>(), extract: false),
            Case(part, "question", ClaimCategory.SourceFact,
                ClaimRhetoricalMode.Question,
                $"Did the {town} report mention 12 outages?",
                "12 outages",
                ClaimVerificationOutcome.NoMaterialIssue,
                new[] { Evidence("src-1",
                    $"The {town} report mentioned 3 outages.") },
                extract: false)
        };
        return new V7ReviewedCorpus(
            V7FreezeContract.ReviewVersion, V7FreezeContract.LabelStatus,
            partition, V7FreezeContract.ReviewMethod,
            "Fictional unit-test examples; not an actual owner-reviewed benchmark.",
            Array.AsReadOnly(items));
    }

    private static V7AcceptanceCriteria Criteria()
    {
        V7LaneCriterion Lane(string name, bool positives) => new(
            name, 1, 1, positives ? 1 : 0,
            0.50m, 0.25m,
            positives ? 0.5m : null,
            positives ? 0.5m : null,
            0.5m, positives ? null :
                "No material contradiction is expected in protected/opinion cases.");
        return new V7AcceptanceCriteria(
            V7FreezeContract.ThresholdVersion,
            "synthetic-v7b-model", FullDigest,
            EvidenceVerifierV4.VerificationPromptVersion,
            EvidenceVerifierV4.VerificationSchemaVersion,
            V7FreezeContract.PlannedReviewedScorerVersion,
            2, 1, 1,
            0.50m, 0.50m, 0.50m, 0.50m, 0.50m,
            0.25m, 0.25m, 0.25m, 20000, 0.20m,
            Array.AsReadOnly(new[]
            {
                Lane("category:SourceFact", true),
                Lane("category:CharacterIdentity", true),
                Lane("category:PersonalHistory", true),
                Lane("category:DialogueAttribution", true),
                Lane("category:Other", false),
                Lane("rhetoric:ProtectedNonAsserted", false)
            }),
            new V7IntegrityRequirements(
                true, true, true, true, true, true, true),
            "A human reviews all conversation and all false-positive model findings.",
            "Only test constants, never accepted user thresholds or independent review.");
    }

    private static string J<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static V7ApprovedEvaluationInputs Approved()
    {
        var dev = J(Corpus(V7Partition.Development));
        var hold = J(Corpus(V7Partition.Holdout));
        var criteria = J(Criteria());
        var approval = J(new V7OwnerFreezeApproval(
            V7FreezeContract.ApprovalVersion, "synthetic-test-owner",
            DateTimeOffset.UtcNow, Hash(dev), Hash(hold), Hash(criteria),
            V7FreezeContract.ApprovalDeclaration));
        return V7ReviewFreezeValidator.OpenApproved(
            dev, hold, criteria, approval);
    }

    private static ModelResponse Answer(ModelRequest request,
        ClaimVerificationOutcome outcome, bool withCitation = true)
    {
        Assert.Equal(V4JudgmentParser.JsonSchema, request.JsonSchema);
        Assert.DoesNotContain("reviewedOutcome", request.Prompt);
        Assert.DoesNotContain("reviewRationale", request.Prompt);
        Assert.DoesNotContain("synthetic-test-reviewer", request.Prompt);
        const string startTag =
            "BEGIN FROZEN HOST-AUTHORIZED CLAIM BATCH (DATA):\n";
        const string endTag = "\nEND FROZEN HOST-AUTHORIZED CLAIM BATCH";
        var start = request.Prompt.IndexOf(startTag, StringComparison.Ordinal);
        var end = request.Prompt.LastIndexOf(endTag, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        using var json = JsonDocument.Parse(request.Prompt.Substring(
            start + startTag.Length, end - start - startTag.Length));
        var payload = json.RootElement;
        var claim = payload.GetProperty("claims")[0];
        var source = claim.GetProperty("content")
            .GetProperty("Items").EnumerateArray().FirstOrDefault();
        var refs = withCitation && source.ValueKind == JsonValueKind.Object
            ? new[] { source.GetProperty("ReferenceId").GetString()! }
            : Array.Empty<string>();
        var verdict = JsonSerializer.Serialize(new
        {
            jobId = payload.GetProperty("jobId").GetString(),
            judgments = new[]
            {
                new
                {
                    claimId = claim.GetProperty("claimId").GetString(),
                    packetId = claim.GetProperty("packetId").GetString(),
                    packetSha256 = claim.GetProperty("packetSha256").GetString(),
                    outcome = outcome.ToString(),
                    supportingReferences = Array.Empty<string>(),
                    opposingReferences =
                        outcome == ClaimVerificationOutcome.ConfirmedContradiction
                        ? refs : Array.Empty<string>(),
                    problematicText =
                        outcome == ClaimVerificationOutcome.ConfirmedContradiction
                        ? claim.GetProperty("claimText").GetString() : "",
                    explanation = "Local scripted model-free host parser unit test."
                }
            }
        });
        return ModelResponse.Responded(verdict);
    }

    private static V7CaseObservation Observation(
        string id, int detections, bool exact, bool category, bool rhetoric,
        VerificationOperationalStatus status, ClaimVerificationOutcome? verdict,
        IReadOnlyList<string>? opposition = null, int charged = 1) =>
        new(id, detections, exact, category, rhetoric,
            status, verdict, Array.Empty<string>(),
            opposition ?? Array.Empty<string>(),
            verdict == ClaimVerificationOutcome.ConfirmedContradiction
                ? "12 outages" : "",
            charged, charged == 0 ? 0 : 100);

    [Fact]
    public void Approved_inputs_are_deep_read_only_and_dont_grant_semantic_acceptance()
    {
        var inputs = Approved();
        Assert.False(inputs.Receipt.SemanticAcceptanceGranted);
        Assert.False(inputs.Receipt.EnforcementAuthorized);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<V7ReviewedCase>)inputs.Development.Cases).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<V7ReviewEvidence>)inputs.Development.Cases[0].Evidence).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<V7LaneCriterion>)inputs.Criteria.Lanes).Clear());
        Assert.Equal(8, inputs.Holdout.Cases.Count);
    }

    [Fact]
    public void Reviewed_scorer_preserves_unobserved_cases_in_end_to_end_denominator()
    {
        var a = Approved();
        var dev = a.Development.Cases[0];
        var score = V7ReviewedScorer.Score(a,
            V7Partition.Development, "partial",
            a.Criteria.ModelName, a.Criteria.ModelDigest,
            new[]
            {
                Observation(dev.CaseId, 1, false, false, false,
                    VerificationOperationalStatus.Completed,
                    ClaimVerificationOutcome.ConfirmedContradiction,
                    new[] { "src-1" })
            });
        Assert.Equal(8, score.Overall.IndependentCases);
        Assert.Equal(1, score.Overall.ObservedCases);
        Assert.Equal(1, score.Overall.OracleCompleted);
        Assert.Equal(1, score.Overall.OracleContradictionTruePositives);
        Assert.Equal(0, score.Overall.EndToEndContradictionsFound);
        Assert.Equal(0m, score.Overall.EndToEndConfirmedRecall);
        Assert.True(score.Overall.ReviewedContradictions > 1);
        Assert.False(score.SemanticAcceptanceGranted);
    }

    [Fact]
    public void No_claim_extraction_on_a_protected_question_counts_as_correct_rejection()
    {
        var a = Approved();
        var question = a.Development.Cases.Last();
        var score = V7ReviewedScorer.Score(a,
            V7Partition.Development, "protected",
            a.Criteria.ModelName, a.Criteria.ModelDigest,
            new[]
            {
                Observation(question.CaseId, 0, false, false, false,
                    VerificationOperationalStatus.NotStarted, null,
                    charged: 0)
            });
        Assert.Equal(0, score.Overall.ProtectedExtractionFalsePositives);
        Assert.Equal(0, score.Overall.OracleCompleted);
        Assert.Null(score.Overall.OracleConfirmedPrecision);
    }

    [Fact]
    public void Protected_question_overextraction_is_counted_independently_from_v4_judgment()
    {
        var a = Approved();
        var question = a.Development.Cases.Last();
        var score = V7ReviewedScorer.Score(a,
            V7Partition.Development, "overextract",
            a.Criteria.ModelName, a.Criteria.ModelDigest,
            new[]
            {
                Observation(question.CaseId, 1, true, true, true,
                    VerificationOperationalStatus.NotStarted, null,
                    charged: 0)
            });
        Assert.Equal(1, score.Overall.ProtectedExtractionFalsePositives);
        Assert.Equal(0, score.Overall.OracleCompleted);
    }

    [Fact]
    public void Local_model_is_not_called_for_protected_case_or_inference_before_explicit_run()
    {
        var model = new ScriptedModel(_ => ModelResponse.TransportError());
        var lab = new V7bReviewedModelLab(model);
        Assert.Equal(0, model.Calls);
        var a = Approved();
        var result = lab.Run(a, V7Partition.Development,
            "scripted-cases", maxCases: 8);
        Assert.True(model.Calls <= 6);
        Assert.False(result.Score.SemanticAcceptanceGranted);
        Assert.Equal(8, result.Score.Overall.ObservedCases);
        Assert.Contains(result.PrivateAudit.Cases,
            x => x.Diagnostic == "protected-no-inference");
    }

    [Fact]
    public void Actual_v4_prompt_parser_accepts_scripted_owner_oracle_contradiction()
    {
        var model = new ScriptedModel(request =>
            Answer(request, ClaimVerificationOutcome.ConfirmedContradiction));
        var run = new V7bReviewedModelLab(model).Run(
            Approved(), V7Partition.Development, "oracle-v4", 1);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, run.Score.Overall.OracleCompleted);
        Assert.Equal(1, run.Score.Overall.OracleContradictionTruePositives);
        Assert.True(run.Score.Overall.OracleConfirmedPrecision > 0m);
        Assert.NotNull(Assert.Single(run.PrivateAudit.Cases).OracleV4Prompt);
        Assert.DoesNotContain("reviewedOutcome",
            Assert.Single(run.PrivateAudit.Cases).OracleV4Prompt);
    }

    [Fact]
    public void Invalid_json_is_technical_not_an_epistemic_no_issue()
    {
        var model = new ScriptedModel(_ => ModelResponse.Responded("{}"));
        var run = new V7bReviewedModelLab(model).Run(
            Approved(), V7Partition.Development, "bad-json", 1);
        Assert.Equal(1, model.Calls);
        Assert.Equal(0, run.Score.Overall.OracleCompleted);
        Assert.Equal(1, run.Score.Overall.OracleTechnicalFailures);
        Assert.Equal(0, run.Score.Overall.OracleOutcomeMatches);
        Assert.Equal(1, run.Score.Overall.ChargedLocalModelCalls);
        Assert.Equal(VerificationOperationalStatus.InvalidResponse,
            Assert.Single(run.PrivateAudit.Cases).Status);
    }

    [Fact]
    public void Wrong_model_digest_fails_before_any_inference()
    {
        var model = new ScriptedModel(_ => ModelResponse.TransportError(),
            new string('c', 64));
        var a = Approved();
        Assert.Throws<InvalidDataException>(() =>
            new V7bReviewedModelLab(model).Run(
                a, V7Partition.Development, "wrong-digest", 1));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public void Aggregate_export_has_no_private_case_ids_or_reviewed_passages()
    {
        var a = Approved();
        var model = new ScriptedModel(request =>
            Answer(request, ClaimVerificationOutcome.ConfirmedContradiction));
        var run = new V7bReviewedModelLab(model).Run(
            a, V7Partition.Development, "private-aggregation", 1);
        var safe = V7bSafeExporter.From(run, a);
        var raw = JsonSerializer.Serialize(safe);
        Assert.False(safe.SemanticAcceptanceGranted);
        Assert.False(safe.EnforcementAuthorized);
        Assert.False(safe.NaturalSessionGateTested);
        Assert.Contains("Unscorable", raw);
        Assert.DoesNotContain("dev-source", raw);
        Assert.DoesNotContain("Elmvale", raw);
        Assert.DoesNotContain("reviewRationale", raw);
        Assert.DoesNotContain("OracleV4Prompt", raw);
        Assert.DoesNotContain("RawModelResponse", raw);
        Assert.Equal(a.Receipt.ApprovalSha256, safe.FreezeApprovalSha256);
    }

    [Fact]
    public void Duplicate_unknown_or_inconsistent_case_observations_are_rejected()
    {
        var a = Approved();
        var c = a.Development.Cases[0];
        var obs = Observation(c.CaseId, 1, false, false, false,
            VerificationOperationalStatus.Completed,
            ClaimVerificationOutcome.ConfirmedContradiction,
            new[] { "src-1" });
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewedScorer.Score(a, V7Partition.Development,
                "duplicate", a.Criteria.ModelName, a.Criteria.ModelDigest,
                new[] { obs, obs }));
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewedScorer.Score(a, V7Partition.Development,
                "unknown", a.Criteria.ModelName, a.Criteria.ModelDigest,
                new[] { obs with { CaseId = "invented" } }));
        Assert.Throws<InvalidDataException>(() =>
            V7ReviewedScorer.Score(a, V7Partition.Development,
                "conflicting", a.Criteria.ModelName, a.Criteria.ModelDigest,
                new[] { obs with { ExactSpanDetected = false, CategoryCorrect = true } }));
    }

    [Fact]
    public void Same_corpus_flips_are_not_counted_as_new_holdout_cases()
    {
        var a = Approved();
        var c = a.Development.Cases[0];
        var yes = Observation(c.CaseId, 1, true, true, true,
            VerificationOperationalStatus.Completed,
            ClaimVerificationOutcome.ConfirmedContradiction,
            new[] { "src-1" });
        var no = Observation(c.CaseId, 1, true, true, true,
            VerificationOperationalStatus.Completed,
            ClaimVerificationOutcome.InsufficientEvidence,
            charged: 1);
        var left = V7ReviewedScorer.Score(a, V7Partition.Development,
            "repeat-one", a.Criteria.ModelName, a.Criteria.ModelDigest,
            new[] { yes });
        var right = V7ReviewedScorer.Score(a, V7Partition.Development,
            "repeat-two", a.Criteria.ModelName, a.Criteria.ModelDigest,
            new[] { no });
        var drift = V7ReviewedScorer.Compare(left, right);
        Assert.Equal(a.Development.Cases.Count, drift.IndependentCases);
        Assert.Equal(1, drift.BothOracleCompleted);
        Assert.Equal(c.CaseId, Assert.Single(drift.ChangedVerdictCaseIds));
        var safe = V7bSafeExporter.Drift(
            new V7bLabRun(left, new V7bPrivateRunAudit(
                V7bLabContract.Version, a.Receipt.ApprovalSha256,
                a.Receipt.DevelopmentSha256, a.Receipt.CriteriaSha256,
                V7Partition.Development, a.Criteria.ModelName,
                a.Criteria.ModelDigest, left.RunId,
                ReviewTime, Array.Empty<V7bPrivateCaseAudit>(), "test")),
            new V7bLabRun(right, new V7bPrivateRunAudit(
                V7bLabContract.Version, a.Receipt.ApprovalSha256,
                a.Receipt.DevelopmentSha256, a.Receipt.CriteriaSha256,
                V7Partition.Development, a.Criteria.ModelName,
                a.Criteria.ModelDigest, right.RunId,
                ReviewTime, Array.Empty<V7bPrivateCaseAudit>(), "test")));
        Assert.Equal(1, safe.ChangedOracleOutcomeCount);
        Assert.False(safe.AcceptanceGranted);
        Assert.DoesNotContain(c.CaseId, JsonSerializer.Serialize(safe));
    }

    [Fact]
    public void Frozen_approval_must_precede_reviewed_lab_access()
    {
        var a = Approved();
        Assert.Throws<NotSupportedException>(() =>
            ((IList<V7ReviewedCase>)a.Holdout.Cases).Clear());
        Assert.False(a.Receipt.EnforcementAuthorized);
    }
}

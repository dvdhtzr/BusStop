using System.Text.Json;
using BusStop.Engine.Evaluation;
using BusStop.Engine.Verification;
using BusStop.Models;
using BusStop.Moderation.Evaluation;
using BusStop.Moderation.Verification;
using Xunit;

namespace BusStop.Tests.Moderation;

public sealed class VerifierV6bLocalLabTests
{
    private sealed class StubModel : IModelClient
    {
        private readonly Func<ModelRequest, ModelResponse> _answer;
        public StubModel(Func<ModelRequest, ModelResponse> answer,
            string digest = "synthetic-model-digest-v6b") =>
            (_answer, ModelDigest) = (answer, digest);
        public string ModelName => "synthetic-model";
        public string ModelDigest { get; }
        public int Calls { get; private set; }
        public ModelResponse Decide(
            ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return _answer(request);
        }
    }

    private static V6CorpusFixture Corpus()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null)
        {
            var candidate = Path.Combine(path.FullName,
                "docs", "moderator", "corpus",
                "v6a-synthetic-provisional-v1.json");
            if (File.Exists(candidate))
                return V6CorpusReader.LoadProvisionalSynthetic(
                    File.ReadAllText(candidate));
            path = path.Parent;
        }
        throw new FileNotFoundException("Committed synthetic V6a corpus missing.");
    }

    // Scripted model only. Extracts packet identities from the real V4 prompt,
    // never has access to expected labels, private owner reviews or Ollama.
    private static ModelResponse Reply(
        ModelRequest request, ClaimVerificationOutcome outcome,
        bool citeOpposing)
    {
        Assert.Equal(V4JudgmentParser.JsonSchema, request.JsonSchema);
        Assert.Equal(V6bLabContract.MaxGeneratedTokens, request.MaxGeneratedTokens);
        Assert.Equal(V6bLabContract.ContextTokens, request.ContextTokens);
        const string begin = "BEGIN FROZEN HOST-AUTHORIZED CLAIM BATCH (DATA):\n";
        const string end = "\nEND FROZEN HOST-AUTHORIZED CLAIM BATCH";
        var start = request.Prompt.IndexOf(begin, StringComparison.Ordinal);
        var stop = request.Prompt.LastIndexOf(end, StringComparison.Ordinal);
        Assert.True(start >= 0 && stop > start);
        using var json = JsonDocument.Parse(request.Prompt.Substring(
            start + begin.Length, stop - start - begin.Length));
        var root = json.RootElement;
        var claim = root.GetProperty("claims")[0];
        var item = claim.GetProperty("content").GetProperty("Items")[0];
        var response = JsonSerializer.Serialize(new
        {
            jobId = root.GetProperty("jobId").GetString(),
            judgments = new[]
            {
                new
                {
                    claimId = claim.GetProperty("claimId").GetString(),
                    packetId = claim.GetProperty("packetId").GetString(),
                    packetSha256 = claim.GetProperty("packetSha256").GetString(),
                    outcome = outcome.ToString(),
                    supportingReferences = Array.Empty<string>(),
                    opposingReferences = citeOpposing
                        ? new [] { item.GetProperty("ReferenceId").GetString()! }
                        : Array.Empty<string>(),
                    problematicText = outcome == ClaimVerificationOutcome.ConfirmedContradiction
                        ? claim.GetProperty("claimText").GetString()
                        : "",
                    explanation = "Scripted synthetic model-only test: no claim of real semantic correctness."
                }
            }
        });
        return ModelResponse.Responded(response) with
        {
            Generation = new ModelGenerationTelemetry("stop", 100, 60, 1000)
        };
    }

    [Fact]
    public void Real_v4_prompt_and_host_parser_accept_valid_scripted_model_verdict()
    {
        var model = new StubModel(request => Reply(request,
            ClaimVerificationOutcome.ConfirmedContradiction, true));
        var corpus = Corpus();
        var result = new V6bLocalModelLab(model).Run(
            corpus, "unit-validated", maxCases: 1);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, result.Score.Overall.Selected);
        Assert.Equal(1, result.Score.Overall.Completed);
        Assert.Equal(1, result.Score.Overall.ConfirmedTruePositives);
        Assert.Equal(1, result.Score.Overall.ChargedModelCalls);
        Assert.Equal(19, result.Score.Overall.NotSelected);
        Assert.False(result.Score.AcceptanceAllowed);
        var audit = Assert.Single(result.PrivateAudit.Cases);
        Assert.Equal(VerificationOperationalStatus.Completed, audit.Status);
        Assert.Equal("v4-host-validated", audit.DiagnosticCode);
        Assert.NotNull(audit.HostValidatedJudgment);
        Assert.NotNull(audit.RawModelResponse);
        Assert.Contains("BEGIN FROZEN HOST-AUTHORIZED", audit.Prompt);
        Assert.DoesNotContain("expectedOutcome", audit.Prompt);
        Assert.DoesNotContain("rationale", audit.Prompt);
        Assert.DoesNotContain("expectedOpposingRefs", audit.Prompt);
    }

    [Fact]
    public void Malformed_or_wrong_identity_response_is_invalid_not_correct()
    {
        var model = new StubModel(_ => ModelResponse.Responded("{}"));
        var result = new V6bLocalModelLab(model).Run(Corpus(),
            "unit-malformed", 1);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, result.Score.Overall.InvalidResponses);
        Assert.Equal(0, result.Score.Overall.Completed);
        Assert.Equal(0, result.Score.Overall.ConfirmedTruePositives);
        Assert.Null(result.Score.Overall.ConfirmedPrecision);
        Assert.Equal(1, result.Score.Overall.ChargedModelCalls);
        Assert.Null(Assert.Single(result.PrivateAudit.Cases).HostValidatedJudgment);
    }

    [Fact]
    public void Interrupted_or_failed_model_response_is_not_an_epistemic_judgment()
    {
        var model = new StubModel(_ => ModelResponse.TransportError());
        var result = new V6bLocalModelLab(model).Run(Corpus(),
            "unit-transport-failure", 1);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, result.Score.Overall.TransportFailures);
        Assert.Equal(0, result.Score.Overall.Completed);
        Assert.Equal(1, result.Score.Overall.ChargedModelCalls);
        Assert.Null(Assert.Single(result.PrivateAudit.Cases).RawModelResponse);
    }

    [Fact]
    public void Generation_ceiling_is_reported_as_invalid_response()
    {
        var model = new StubModel(request =>
            Reply(request, ClaimVerificationOutcome.ConfirmedContradiction, true)
            with
            {
                Generation = new ModelGenerationTelemetry("length", 100, 2048, 1000)
            });
        var result = new V6bLocalModelLab(model).Run(Corpus(), "unit-truncated", 1);
        Assert.Equal(1, result.Score.Overall.InvalidResponses);
        Assert.Equal("generation-truncated",
            Assert.Single(result.PrivateAudit.Cases).DiagnosticCode);
    }

    [Fact]
    public void Safe_aggregate_export_contains_no_claim_source_prompt_or_raw_response()
    {
        var model = new StubModel(request => Reply(request,
            ClaimVerificationOutcome.ConfirmedContradiction, true));
        var run = new V6bLocalModelLab(model).Run(Corpus(), "unit-safe", 1);
        var safe = V6bSafeExporter.From(run);
        var exported = JsonSerializer.Serialize(safe);
        Assert.False(safe.AcceptanceAllowed);
        Assert.Equal(1, safe.Overall.Selected);
        Assert.Contains("synthetic", exported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("The official report counted 12 outages.", exported);
        Assert.DoesNotContain("The official report recorded exactly 3 outages.", exported);
        Assert.DoesNotContain("RawModelResponse", exported);
        Assert.DoesNotContain("Prompt", exported);
        Assert.DoesNotContain("src-1", exported);
        Assert.DoesNotContain("explanation", exported);
    }

    [Fact]
    public void Identical_corpus_case_repeats_have_byte_identical_v4_prompts_and_packet_hashes()
    {
        var corpus = Corpus();
        var model = new StubModel(request =>
            Reply(request, ClaimVerificationOutcome.ConfirmedContradiction, true));
        var lab = new V6bLocalModelLab(model);
        var first = lab.Run(corpus, "stable-one", 1);
        var second = lab.Run(corpus, "stable-two", 1);

        var a = Assert.Single(first.PrivateAudit.Cases);
        var b = Assert.Single(second.PrivateAudit.Cases);
        Assert.Equal(a.Prompt, b.Prompt);
        Assert.Equal(a.HostValidatedJudgment!.PacketSha256,
            b.HostValidatedJudgment!.PacketSha256);
        Assert.Equal(a.RawModelResponse, b.RawModelResponse);
        Assert.Equal(2, model.Calls);
        Assert.Equal(0, V6bSafeExporter.Compare(first, second).OutcomeFlipCount);
    }

    [Fact]
    public void Same_digest_runs_compare_without_exporting_case_level_flips()
    {
        var left = new V6bLocalModelLab(new StubModel(request =>
            Reply(request, ClaimVerificationOutcome.ConfirmedContradiction, true)))
            .Run(Corpus(), "run-one", 1);
        var right = new V6bLocalModelLab(new StubModel(request =>
            Reply(request, ClaimVerificationOutcome.InsufficientEvidence, false)))
            .Run(Corpus(), "run-two", 1);
        var drift = V6bSafeExporter.Compare(left, right);
        Assert.Equal(1, drift.ComparableCompletedCases);
        Assert.Equal(1, drift.OutcomeFlipCount);
        Assert.False(drift.AcceptanceAllowed);
        var json = JsonSerializer.Serialize(drift);
        Assert.DoesNotContain("source-exact-contradiction", json);

        var differentModel = new V6bLocalModelLab(
            new StubModel(request =>
                Reply(request, ClaimVerificationOutcome.InsufficientEvidence, false),
                "different-digest"))
            .Run(Corpus(), "different-digest-run", 1);
        Assert.Throws<InvalidDataException>(() =>
            V6bSafeExporter.Compare(left, differentModel));
    }

    [Fact]
    public void Bounded_case_count_refuses_more_than_twenty_model_calls()
    {
        var model = new StubModel(_ => ModelResponse.TransportError());
        var lab = new V6bLocalModelLab(model);
        Assert.Throws<InvalidDataException>(() =>
            lab.Run(Corpus(), "over-limit", 21));
        Assert.Equal(0, model.Calls);

        var result = lab.Run(Corpus(), "twenty-cases", 20);
        Assert.Equal(20, model.Calls);
        Assert.Equal(20, result.Score.Overall.Selected);
        Assert.Equal(20, result.Score.Overall.TransportFailures);
        Assert.False(result.Score.AcceptanceAllowed);
    }

    [Fact]
    public void Unknown_or_missing_digest_is_a_hard_error_before_inference()
    {
        var model = new StubModel(_ => ModelResponse.TransportError(),
            "unknown");
        Assert.Throws<InvalidDataException>(() =>
            new V6bLocalModelLab(model));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public void Scripted_model_exception_is_counted_as_transport_failure()
    {
        var model = new StubModel(_ =>
            throw new InvalidOperationException("local synthetic failure"));
        var result = new V6bLocalModelLab(model).Run(
            Corpus(), "exception", 1);
        Assert.Equal(1, result.Score.Overall.TransportFailures);
        Assert.Equal(0, result.Score.Overall.Completed);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public void Cancelled_before_first_case_makes_no_model_calls()
    {
        var model = new StubModel(_ => ModelResponse.TransportError());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var result = new V6bLocalModelLab(model).Run(
            Corpus(), "cancelled", 20, cancelled.Token);
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, result.Score.Overall.Selected);
        Assert.Equal(0, result.Score.Overall.ChargedModelCalls);
        Assert.Empty(result.PrivateAudit.Cases);
        Assert.Equal(20, result.Score.Overall.NotSelected);
    }
}

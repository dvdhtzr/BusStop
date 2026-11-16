using System.Text.Json;
using System.Text.RegularExpressions;
using BusStop.Console;
using BusStop.Models;
using BusStop.Moderation;

namespace BusStop.Tests.Moderation;

public sealed class M1bChallengeLabTests
{
    private static (M1bChallengeLab.Corpus Corpus, string Raw) Fixture()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null)
        {
            var path = Path.Combine(folder.FullName,
                "docs", "moderator", "corpus", "m1b-challenges-v1.json");
            if (File.Exists(path))
            {
                var raw = File.ReadAllText(path);
                return (M1bChallengeLab.Load(raw), raw);
            }
            folder = folder.Parent;
        }
        throw new FileNotFoundException("Cannot locate versioned M1b synthetic corpus.");
    }

    private sealed class Model : IModelClient
    {
        private readonly Func<ModelRequest, int, ModelResponse> _answer;
        public int Calls { get; private set; }
        public Model(Func<ModelRequest, int, ModelResponse> answer) => _answer = answer;
        public string ModelName => "stub";
        public string ModelDigest => "stub-digest";

        public ModelResponse Decide(ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.NotNull(request.JsonSchema);
            Assert.Equal(768, request.MaxGeneratedTokens);
            Assert.Equal(8192, request.ContextTokens);
            return _answer(request, Calls);
        }
    }

    private static string Identity(ModelRequest request)
    {
        var match = Regex.Match(request.Prompt,
            @"Candidate ID: (?<id>[a-f0-9-]{36})", RegexOptions.IgnoreCase);
        Assert.True(match.Success);
        return match.Groups["id"].Value;
    }

    private static ModelResponse Accept(ModelRequest request)
        => ModelResponse.Responded(JsonSerializer.Serialize(new
        {
            contractVersion = "shadow-m1-v1",
            candidateId = Identity(request),
            disposition = "Accept",
            severity = "None",
            finding = (object?)null
        })) with
        {
            Generation = new ModelGenerationTelemetry("stop", 1000, 80, 12345)
        };

    [Fact]
    public void Synthetic_corpus_has_36_unique_balanced_and_explicitly_provisional_cases()
    {
        var (corpus, _) = Fixture();
        Assert.Equal("m1b-challenges-v1", corpus.Version);
        Assert.Equal("provisional-owner-review-required", corpus.LabelStatus);
        Assert.Equal(36, corpus.Cases.Length);
        Assert.Equal(18, corpus.Cases.Count(c => c.Expected == "Allow"));
        Assert.Equal(14, corpus.Cases.Count(c => c.Expected == "Finding"));
        Assert.Equal(4, corpus.Cases.Count(c => c.Expected == "ExactCopy"));
        Assert.All(corpus.Cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Rationale)));
    }

    [Fact]
    public void Always_accept_is_not_a_successful_evaluation_and_copies_cost_no_model_calls()
    {
        var (corpus, raw) = Fixture();
        var model = new Model((request, _) => Accept(request));
        var report = M1bChallengeLab.Run(corpus, raw, model);

        Assert.False(report.Partial);
        Assert.Equal(36, report.Summary.Total);
        Assert.Equal(32, model.Calls);
        Assert.Equal(32, report.Summary.ModelCalls);
        Assert.Equal(4, report.Summary.DeterministicCases);
        Assert.Equal(32, report.Summary.ValidModelResponses);
        Assert.Equal(4, report.Summary.TruePositive);
        Assert.Equal(14, report.Summary.FalseNegative);
        Assert.Equal(18, report.Summary.TrueNegative);
        Assert.Equal(0, report.Summary.FalsePositive);
        Assert.Equal(0, report.Summary.Unscorable);
        Assert.All(report.Cases.Where(x => x.Expected == "ExactCopy"), r =>
        {
            Assert.Equal("Deterministic", r.Status);
            Assert.Empty(r.Attempts);
            Assert.True(r.Correct);
        });
    }

    [Fact]
    public void Short_interleaved_smoke_contains_positive_negative_near_and_exact_copy_cases()
    {
        var (corpus, raw) = Fixture();
        var model = new Model((request, _) => Accept(request));
        var report = M1bChallengeLab.Run(corpus, raw, model, maxCases: 8, modelBudget: 16);

        Assert.True(report.Partial);
        Assert.Equal(8, report.Summary.Total);
        Assert.Equal(6, model.Calls);
        Assert.Equal(2, report.Summary.DeterministicCases);
        Assert.Equal(2, report.Summary.TruePositive);
        Assert.Equal(2, report.Summary.FalseNegative);
        Assert.Equal(4, report.Summary.TrueNegative);
    }

    [Fact]
    public void Invalid_model_output_cannot_count_as_allow_or_hide_budget_exhaustion()
    {
        var (corpus, raw) = Fixture();
        var model = new Model((request, _) =>
            ModelResponse.Responded("{invalid JSON"));
        var report = M1bChallengeLab.Run(corpus, raw, model, maxCases: 2, modelBudget: 1);

        Assert.Equal(1, model.Calls);
        Assert.Equal(2, report.Summary.Unscorable);
        Assert.Equal(1, report.Summary.InvalidModelResponses);
        Assert.Equal(1, report.Summary.BudgetExhausted);
        Assert.Equal(0, report.Summary.TrueNegative);
        Assert.Equal(0, report.Summary.FalseNegative);
    }

    [Fact]
    public void One_recovery_retry_is_charged_and_both_attempts_are_audited()
    {
        var (corpus, raw) = Fixture();
        var model = new Model((request, n) => n == 1
            ? ModelResponse.Responded("{\"contractVersion\":")
            : Accept(request));
        var report = M1bChallengeLab.Run(corpus, raw, model,
            maxCases: 1, modelBudget: 2);

        Assert.Equal(2, report.Summary.ModelCalls);
        var c = Assert.Single(report.Cases);
        Assert.Equal("Reviewed", c.Status);
        Assert.Equal("Allow", c.Predicted);
        Assert.True(c.Correct);
        Assert.Equal(2, c.Attempts.Count);
        Assert.Equal(ShadowReviewStatus.InvalidResponse, c.Attempts[0].Status);
        Assert.Equal(ShadowReviewStatus.Reviewed, c.Attempts[1].Status);
    }

    [Fact]
    public void Strict_parser_accepts_an_evidence_anchored_nonnull_finding()
    {
        var (corpus, raw) = Fixture();
        var model = new Model((request, _) =>
        {
            if (!request.Prompt.Contains(
                "Proposed text (already committed, audit only): The report confirms",
                StringComparison.Ordinal))
                return Accept(request);

            var reference = Regex.Match(request.Prompt,
                @"\[(briefing:[^\]]+)\]").Groups[1].Value;
            Assert.False(string.IsNullOrWhiteSpace(reference));
            return ModelResponse.Responded(JsonSerializer.Serialize(new
            {
                contractVersion = "shadow-m1-v1",
                candidateId = Identity(request),
                disposition = "RequestRevision",
                severity = "Medium",
                finding = new
                {
                    kind = "ContradictedFactualClaim",
                    claimSpan = "The report confirms an aircraft crashed during the outage.",
                    explanation = "The synthetic briefing explicitly reports no crash.",
                    evidenceReferences = new[] { reference },
                    uncertain = false,
                    narrowFeedback = "Remove the unsupported crash claim."
                }
            }));
        });
        var report = M1bChallengeLab.Run(corpus, raw, model,
            maxCases: 2, modelBudget: 4);

        Assert.Equal(2, report.Summary.ValidModelResponses);
        Assert.Equal(1, report.Summary.TruePositive);
        Assert.Equal(1, report.Summary.TrueNegative);
        Assert.Equal(0, report.Summary.FalseNegative);
        Assert.Equal(0, report.Summary.FalsePositive);
        Assert.Equal("Finding", report.Cases[1].Predicted);
    }

    [Fact]
    public void Corpus_rejects_label_swaps_or_invalid_copy_controls()
    {
        var (corpus, _) = Fixture();
        var cases = corpus.Cases.ToArray();
        cases[3] = cases[3] with { Expected = "Allow" };
        var tampered = JsonSerializer.Serialize(corpus with { Cases = cases });
        Assert.Throws<InvalidDataException>(() => M1bChallengeLab.Load(tampered));
    }
}

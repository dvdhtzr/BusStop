using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Models;
using BusStop.Moderation;

namespace BusStop.Console;

/// <summary>Isolated, synthetic, read-only evaluation of the actual M1a prompt,
/// parser, evidence scope, and deterministic duplicate path. NO conversation engine,
/// NO SQLite, NO participant model, NO enforcement, NO live speech modification.</summary>
public static class M1bChallengeLab
{
    public const string Version = "m1b-challenges-v1";

    public sealed record Prior(string Speaker, string Text);
    public sealed record Profile(string Name, string Persona, string[] Traits);
    public sealed record Source(string Title, string SourceName, string Url, string Body);
    public sealed record Challenge(
        string Id, string Category, string Speaker, string Candidate,
        Prior[] Prior, string Rationale, string Expected);
    public sealed record Corpus(
        string Version, string LabelStatus, string Origin,
        Source Briefing, Profile[] Profiles, Challenge[] Cases);

    public sealed record CaseResult(
        string Id, string Category, string Speaker, string Expected, string Rationale,
        string Predicted, string Status, bool? Correct,
        string Trigger, string CandidateId, string PromptSha256,
        IReadOnlyList<string> AuthorizedReferences,
        ModerationJudgment? Judgment, ModeratorSeverity Severity,
        IReadOnlyList<ShadowInferenceTrace> Attempts, string? Failure);

    public sealed record Counts(
        int Total, int Evaluated, int TruePositive, int FalsePositive,
        int TrueNegative, int FalseNegative, int Unscorable,
        int DeterministicCases, int ModelCases, int ModelCalls,
        int ValidModelResponses, int InvalidModelResponses,
        int TransportFailures, int BudgetExhausted,
        int Abstentions, int InconsistentJudgments,
        double? Precision, double? RecallLowerBound, double? FalsePositiveRate);

    public sealed record Report(
        string CorpusVersion, string LabelStatus, string CorpusSha256,
        string ModelName, string ModelDigest, string RuleVersion, string PromptVersion,
        bool Partial, int ModelBudget, DateTimeOffset RecordedAt,
        Counts Summary, IReadOnlyList<CaseResult> Cases,
        string Warning);

    /// <summary>Reject corpus drift or accidental positive/negative label changes
    /// before any model inference. Labels are not promoted to human gold truth.</summary>
    public static Corpus Load(string json)
    {
        if (json.Length > 400_000)
            throw new InvalidDataException("M1b corpus exceeds maximum size.");
        var c = JsonSerializer.Deserialize<Corpus>(json)
            ?? throw new InvalidDataException("M1b corpus is empty.");
        if (c.Version != Version ||
            c.LabelStatus != "provisional-owner-review-required" ||
            c.Briefing is null || string.IsNullOrWhiteSpace(c.Briefing.Body) ||
            c.Profiles is null || c.Profiles.Length != 3 ||
            c.Cases is null || c.Cases.Length != 36)
            throw new InvalidDataException("M1b challenge version, provenance or count mismatch.");
        if (c.Cases.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != 36 ||
            c.Profiles.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 3)
            throw new InvalidDataException("Duplicate challenge IDs or profile names.");
        if (c.Profiles.Any(x => string.IsNullOrWhiteSpace(x.Name) ||
            string.IsNullOrWhiteSpace(x.Persona) ||
            x.Traits is null || x.Traits.Length > 12))
            throw new InvalidDataException("Invalid challenge profile.");

        foreach (var item in c.Cases)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 64 ||
                string.IsNullOrWhiteSpace(item.Category) || item.Category.Length > 64 ||
                string.IsNullOrWhiteSpace(item.Candidate) || item.Candidate.Length > 700 ||
                string.IsNullOrWhiteSpace(item.Rationale) ||
                !c.Profiles.Any(x => x.Name == item.Speaker) ||
                item.Prior is null || item.Prior.Length > 10 ||
                item.Prior.Any(p => string.IsNullOrWhiteSpace(p.Text) ||
                    p.Text.Length > 700 || !c.Profiles.Any(x => x.Name == p.Speaker)) ||
                item.Expected is not ("Allow" or "Finding" or "ExactCopy"))
                throw new InvalidDataException("Invalid M1b challenge: " + item.Id);

            var exact = item.Prior.Any(p =>
                string.Equals(p.Text.Trim(), item.Candidate.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            if (exact != (item.Expected == "ExactCopy"))
                throw new InvalidDataException("Exact-copy label/content mismatch: " + item.Id);
        }

        if (c.Cases.Count(x => x.Expected == "Allow") != 18 ||
            c.Cases.Count(x => x.Expected == "Finding") != 14 ||
            c.Cases.Count(x => x.Expected == "ExactCopy") != 4 ||
            c.Cases.Count(x => x.Category == "SemanticRepetitionAllowed") != 4)
            throw new InvalidDataException("M1b challenge balance has drifted.");
        return c;
    }

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Version + "/" + value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    public static Report Run(
        Corpus corpus, string corpusJson, IModelClient model,
        int maxCases = 36, int modelBudget = 72)
    {
        if (maxCases is < 1 or > 36 || modelBudget is < 0 or > 72)
            throw new ArgumentOutOfRangeException(nameof(maxCases));
        if (model.ModelDigest is null or "" or "unknown")
            throw new InvalidOperationException("Challenge moderator needs a pinned Ollama digest.");

        var cast = corpus.Profiles
            .Select((p, i) => new CharacterProfile(
                new CharacterId(StableGuid("profile-" + i)), 1, p.Name,
                p.Persona, p.Traits))
            .ToArray();
        var byName = cast.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var byId = cast.ToDictionary(x => x.Id);
        var source = corpus.Briefing;
        var briefing = new Briefing(new BriefingId(StableGuid("briefing")),
            source.Title, source.SourceName, source.Url, source.Body);
        var session = new SessionId(StableGuid("session"));
        var output = new List<CaseResult>();
        var calls = 0;

        foreach (var item in corpus.Cases.Take(maxCases))
        {
            var speaker = byName[item.Speaker];
            var opportunity = new OpportunityId(StableGuid("opportunity/" + item.Id));
            var candidate = new ModerationCandidate(
                StableGuid("candidate/" + item.Id), session, opportunity,
                speaker.Id, item.Prior.Length, item.Candidate);
            var history = item.Prior.Select((p, i) =>
                new Utterance(
                    new UtteranceId(StableGuid("utterance/" + item.Id + "/" + i)),
                    session, new OpportunityId(StableGuid("prior/" + item.Id + "/" + i)),
                    byName[p.Speaker].Id, i + 1, p.Text, null, null,
                    DateTimeOffset.UnixEpoch)).ToArray();
            var observation = new ShadowObservation(candidate,
                new UtteranceId(StableGuid("committed/" + item.Id)), history);
            var trigger = ShadowModerationWorker.DetectTrigger(observation, 3)
                ?? "periodic-sample";
            var (prompt, refs) = ShadowModerationWorker.BuildModeratorPrompt(
                observation, speaker, byId, briefing, Array.Empty<string>(), trigger);
            var scope = new ModerationEvidenceScope(session, opportunity,
                speaker.Id, candidate.CommittedBoundary,
                refs.ToHashSet(StringComparer.Ordinal));

            var traces = new List<ShadowInferenceTrace>();
            ShadowParsedJudgment? parsed = null;
            string? failure = null;
            var status = "Unscorable";
            string prediction = "Unscorable";

            // The same deterministic implementation as the live shadow worker.
            var exact = ShadowModerationWorker.TryBuildExactCopy(candidate, history);
            if (exact is { } proof)
            {
                parsed = proof.Parsed;
                status = "Deterministic";
                prediction = "Finding";
                refs = new[] { proof.Reference };
            }
            else
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (calls >= modelBudget)
                    {
                        status = "BudgetExhausted";
                        failure = "Separate M1b model-call ceiling reached.";
                        break;
                    }
                    calls++; // budget charged BEFORE every real model call
                    var requestId = StableGuid("request/" + item.Id + "/" + attempt);
                    var attemptStatus = ShadowReviewStatus.Error;
                    ModelResponse? response = null;
                    var retry = false;
                    string? attemptError = null;
                    try
                    {
                        response = model.Decide(new ModelRequest(
                            speaker.Id, prompt,
                            JsonSchema: ShadowResponseParser.JsonSchemaFor(candidate.CandidateId),
                            MaxGeneratedTokens: 768, ContextTokens: 8192));
                        if (response.Transport != ModelTransport.Responded ||
                            string.IsNullOrWhiteSpace(response.RawJson))
                        {
                            attemptStatus = ShadowReviewStatus.TransportFailure;
                            attemptError = response.TransportDetail ??
                                response.Transport.ToString();
                            status = "TransportFailure";
                        }
                        else
                        {
                            try
                            {
                                parsed = ShadowResponseParser.Parse(
                                    response.RawJson, candidate, scope);
                                attemptStatus = ShadowReviewStatus.Reviewed;
                                status = "Reviewed";
                                var disposition = parsed.Judgment.Disposition;
                                var hasFinding = parsed.Judgment.Finding is not null;
                                // Accepted shape is not the same as a coherent verdict.
                                // Abstain must NOT inflate true negatives; Accept plus
                                // a finding must NOT earn true-positive credit.
                                if (disposition == ModerationDisposition.Accept && !hasFinding)
                                    prediction = "Allow";
                                else if ((disposition == ModerationDisposition.Advisory ||
                                          disposition == ModerationDisposition.RequestRevision) &&
                                         hasFinding)
                                    prediction = "Finding";
                                else if (disposition == ModerationDisposition.Abstain &&
                                         !hasFinding)
                                    status = "Abstained";
                                else
                                {
                                    status = "InconsistentJudgment";
                                    attemptError = "Disposition/finding disagreement.";
                                }
                            }
                            catch (JsonException ex)
                            {
                                attemptStatus = ShadowReviewStatus.InvalidResponse;
                                attemptError = ex.Message;
                                status = "InvalidResponse";
                                retry = true;
                            }
                            catch (InvalidDataException ex)
                            {
                                attemptStatus = ShadowReviewStatus.InvalidResponse;
                                attemptError = ex.Message;
                                status = "InvalidResponse";
                                retry = true;
                            }
                            catch (Exception ex) when (ex is InvalidOperationException or
                                FormatException or KeyNotFoundException)
                            {
                                attemptStatus = ShadowReviewStatus.InvalidResponse;
                                attemptError = ex.Message;
                                status = "InvalidResponse";
                                retry = ex is FormatException or KeyNotFoundException;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        attemptStatus = ShadowReviewStatus.Error;
                        attemptError = ex.Message;
                        status = "Error";
                    }
                    failure = attemptError;
                    traces.Add(new ShadowInferenceTrace(
                        requestId, attemptStatus, attemptError,
                        response?.Generation?.DoneReason,
                        response?.Generation?.PromptEvalCount,
                        response?.Generation?.EvalCount,
                        response?.Generation?.TotalDurationNs,
                        response?.RawJson));
                    if (status == "Reviewed" || !retry)
                        break;
                }
            }

            bool? correct = prediction == "Unscorable"
                ? null
                : (item.Expected == "Allow" ? prediction == "Allow"
                    : prediction == "Finding");
            output.Add(new CaseResult(
                item.Id, item.Category, item.Speaker, item.Expected,
                item.Rationale, prediction, status, correct, trigger,
                candidate.CandidateId.ToString("D"), Hash(prompt), refs,
                parsed?.Judgment, parsed?.Severity ?? ModeratorSeverity.None,
                traces, failure));
        }

        var eligible = output.Where(r => r.Correct.HasValue).ToArray();
        var tp = eligible.Count(r => r.Expected != "Allow" && r.Predicted == "Finding");
        var fp = eligible.Count(r => r.Expected == "Allow" && r.Predicted == "Finding");
        var tn = eligible.Count(r => r.Expected == "Allow" && r.Predicted == "Allow");
        var fn = eligible.Count(r => r.Expected != "Allow" && r.Predicted == "Allow");
        var totalPos = output.Count(r => r.Expected != "Allow");
        var totalNeg = output.Count(r => r.Expected == "Allow");
        var counts = new Counts(
            output.Count, eligible.Length, tp, fp, tn, fn,
            output.Count - eligible.Length,
            output.Count(r => r.Status == "Deterministic"),
            output.Count(r => r.Status != "Deterministic"),
            calls,
            output.Count(r => r.Attempts.LastOrDefault()?.Status ==
                ShadowReviewStatus.Reviewed),
            output.Count(r => r.Status == "InvalidResponse"),
            output.Count(r => r.Status == "TransportFailure"),
            output.Count(r => r.Status == "BudgetExhausted"),
            output.Count(r => r.Status == "Abstained"),
            output.Count(r => r.Status == "InconsistentJudgment"),
            tp + fp == 0 ? null : (double)tp / (tp + fp),
            totalPos == 0 ? null : (double)tp / totalPos,
            totalNeg == 0 ? null : (double)fp / totalNeg);

        return new Report(
            Version, corpus.LabelStatus, Hash(corpusJson),
            model.ModelName, model.ModelDigest,
            ShadowModerationWorker.CurrentRuleVersion,
            ShadowModerationWorker.CurrentPromptVersion,
            maxCases < corpus.Cases.Length, modelBudget,
            DateTimeOffset.UtcNow, counts, output,
            "SYNTHETIC challenges, PROVISIONAL labels. Neither JSON validity nor a numeric score establishes semantic correctness. No enforcement or automatic promotion.");
    }
}

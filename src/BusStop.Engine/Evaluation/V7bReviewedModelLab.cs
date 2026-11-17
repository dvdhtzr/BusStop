using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Domain;
using BusStop.Engine.Verification;
using BusStop.Models;
using BusStop.Moderation.Evaluation;
using BusStop.Moderation.Verification;

namespace BusStop.Engine.Evaluation;

public static class V7bLabContract
{
    public const string Version = "busstop-v7b-local-reviewed-lab-v1";
    public const int MaxCasesPerRun = 128;
    public const int MaxPromptBytes = 28_000;
    public const int MaxRawResponseChars = 32_000;
    public const int MaxGeneratedTokens = 2048;
    public const int ContextTokens = 8192;
}

/// <summary>
/// PRIVATE case audit: contains raw reviewed utterance/evidence, gold metadata,
/// exact V4 prompt and model answer. Must NEVER leave the owner's local disk.
/// </summary>
public sealed record V7bPrivateCaseAudit(
    string CaseId,
    string UtteranceText,
    string ReviewedClaimText,
    IReadOnlyList<V7ReviewEvidence> ReviewedEvidence,
    string? OracleV4Prompt,
    string? RawModelResponse,
    string Diagnostic,
    VerificationOperationalStatus Status,
    ClaimJudgment? HostValidatedJudgment,
    int ExtractionCandidateCount,
    bool ExactClaimDetected,
    long InferenceMilliseconds);

public sealed record V7bPrivateRunAudit(
    string ContractVersion,
    string ApprovalSha256,
    string CorpusSha256,
    string CriteriaSha256,
    V7Partition Partition,
    string ModelName,
    string ModelDigest,
    string RunId,
    DateTimeOffset StartedUtc,
    IReadOnlyList<V7bPrivateCaseAudit> Cases,
    string Warning);

public sealed record V7bLabRun(
    V7ReviewedRunScore Score,
    V7bPrivateRunAudit PrivateAudit);

/// <summary>
/// Opt-in owner-local read-only component experiment. V3 extraction sees each
/// original utterance; V4 gets the separately owner-annotated EXACT claim.
/// Oracle V4 success is NOT an end-to-end extraction result. No live SQLite,
/// participant prompt, V5 charged ledger, or M1 semantic promotion occurs.
/// </summary>
public sealed class V7bReviewedModelLab
{
    private readonly IModelClient _model;

    public V7bReviewedModelLab(IModelClient model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        if (string.IsNullOrWhiteSpace(model.ModelName) ||
            string.IsNullOrWhiteSpace(model.ModelDigest) ||
            model.ModelDigest == "unknown")
            throw new InvalidDataException(
                "V7b requires a fully pinned, local model and digest.");
    }

    public V7bLabRun Run(
        V7ApprovedEvaluationInputs approved, V7Partition partition,
        string runId, int maxCases = V7bLabContract.MaxCasesPerRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (!Enum.IsDefined(partition) ||
            approved.Criteria.ModelName != _model.ModelName ||
            approved.Criteria.ModelDigest != _model.ModelDigest ||
            approved.Criteria.VerifierPromptVersion !=
                EvidenceVerifierV4.VerificationPromptVersion ||
            approved.Criteria.VerificationSchemaVersion !=
                EvidenceVerifierV4.VerificationSchemaVersion ||
            approved.Criteria.ScorerVersion !=
                V7FreezeContract.PlannedReviewedScorerVersion ||
            maxCases is < 1 or > V7bLabContract.MaxCasesPerRun ||
            string.IsNullOrWhiteSpace(runId) || runId.Length > 128)
            throw new InvalidDataException(
                "V7b requires exact frozen model/prompt/schema/scorer pins and bounded cases.");

        var corpus = partition == V7Partition.Development
            ? approved.Development : approved.Holdout;
        var corpusHash = partition == V7Partition.Development
            ? approved.Receipt.DevelopmentSha256
            : approved.Receipt.HoldoutSha256;
        var started = DateTimeOffset.UtcNow;
        var observations = new List<V7CaseObservation>();
        var audits = new List<V7bPrivateCaseAudit>();

        foreach (var item in corpus.Cases.Take(maxCases))
        {
            if (cancellationToken.IsCancellationRequested)
                break; // All remaining reviewed cases still count as unseen

            LabUnit unit;
            try
            {
                unit = FreezeOwnerReviewed(item, corpusHash);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                observations.Add(new V7CaseObservation(
                    item.CaseId, 0, false, false, false,
                    VerificationOperationalStatus.Error, null,
                    Array.Empty<string>(), Array.Empty<string>(),
                    "", 0, 0));
                audits.Add(new V7bPrivateCaseAudit(
                    item.CaseId, item.UtteranceText, item.ClaimText,
                    Array.AsReadOnly(item.Evidence.ToArray()), null, null,
                    "reviewed-packet-" + ex.GetType().Name,
                    VerificationOperationalStatus.Error, null, 0, false, 0));
                continue;
            }

            var extracted = V3ClaimDetector.Detect(unit.Job, unit.Utterance);
            var exact = extracted.Claims.FirstOrDefault(c =>
                c.Start == item.ClaimStartUtf16 &&
                c.Length == item.ClaimLengthUtf16);
            var exactMatched = exact is not null;
            var correctCategory = exact?.Category == item.Category;
            var correctRhetoric = exact?.RhetoricalMode == item.RhetoricalMode;

            // An annotated NO-EXTRACT protected case is intentionally not
            // sent to V4. Any V3 false selection remains observable in the
            // independent extraction lane, not hidden as model correctness.
            if (!item.ShouldExtractClaim)
            {
                observations.Add(new V7CaseObservation(item.CaseId,
                    extracted.Claims.Count, exactMatched, correctCategory,
                    correctRhetoric, VerificationOperationalStatus.NotStarted,
                    null, Array.Empty<string>(), Array.Empty<string>(),
                    "", 0, 0));
                audits.Add(new V7bPrivateCaseAudit(
                    item.CaseId, item.UtteranceText, item.ClaimText,
                    Array.AsReadOnly(item.Evidence.ToArray()), null, null,
                    "protected-no-inference", VerificationOperationalStatus.NotStarted,
                    null, extracted.Claims.Count, exactMatched, 0));
                continue;
            }

            var prompt = V4GroundedVerifier.BuildPrompt(
                unit.Job, new[] { unit.Package });
            ModelResponse? answer = null;
            ClaimJudgment? judgment = null;
            var status = VerificationOperationalStatus.Error;
            var diagnostic = "prompt-preflight-ceiling";
            var charged = 0;
            long elapsed = 0;
            if (Encoding.UTF8.GetByteCount(prompt) <= V7bLabContract.MaxPromptBytes)
            {
                // One explicit local call. No retry or refund. This is NOT
                // a production V4/V5 charged attempt or session budget proof.
                charged = 1;
                var clock = Stopwatch.StartNew();
                try
                {
                    answer = _model.Decide(new ModelRequest(
                        unit.Job.SpeakerId, prompt,
                        V4JudgmentParser.JsonSchema,
                        V7bLabContract.MaxGeneratedTokens,
                        V7bLabContract.ContextTokens), cancellationToken);
                    if (answer is null ||
                        answer.Transport != ModelTransport.Responded)
                    {
                        status = VerificationOperationalStatus.TransportFailure;
                        diagnostic = "model-transport-failure";
                    }
                    else if (string.Equals(answer.Generation?.DoneReason,
                        "length", StringComparison.OrdinalIgnoreCase) ||
                        answer.RawJson is null ||
                        answer.RawJson.Length > V7bLabContract.MaxRawResponseChars)
                    {
                        status = VerificationOperationalStatus.InvalidResponse;
                        diagnostic = "model-truncated-or-oversized";
                    }
                    else
                    {
                        try
                        {
                            judgment = V4JudgmentParser.Parse(
                                answer.RawJson, unit.Job, unit.Utterance,
                                new[] { unit.Package }).Single();
                            status = VerificationOperationalStatus.Completed;
                            diagnostic = "v4-host-validated";
                        }
                        catch (Exception ex) when (ex is JsonException or
                            InvalidDataException or ArgumentException or
                            InvalidOperationException or FormatException)
                        {
                            status = VerificationOperationalStatus.InvalidResponse;
                            diagnostic = "v4-rejected-" + ex.GetType().Name;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    status = VerificationOperationalStatus.TransportFailure;
                    diagnostic = "model-call-" + ex.GetType().Name;
                }
                finally
                {
                    clock.Stop();
                    elapsed = clock.ElapsedMilliseconds;
                }
            }

            observations.Add(new V7CaseObservation(
                item.CaseId, extracted.Claims.Count,
                exactMatched, correctCategory, correctRhetoric,
                status, judgment?.Outcome,
                judgment?.SupportingReferences ?? Array.Empty<string>(),
                judgment?.OpposingReferences ?? Array.Empty<string>(),
                judgment?.ProblematicText ?? "", charged, elapsed));
            audits.Add(new V7bPrivateCaseAudit(
                item.CaseId, item.UtteranceText, item.ClaimText,
                Array.AsReadOnly(item.Evidence.ToArray()), prompt,
                answer?.RawJson is { Length: <= V7bLabContract.MaxRawResponseChars }
                    ? answer.RawJson : null,
                diagnostic, status, judgment, extracted.Claims.Count,
                exactMatched, elapsed));
        }

        var score = V7ReviewedScorer.Score(
            approved, partition, runId,
            _model.ModelName, _model.ModelDigest, observations);
        var audit = new V7bPrivateRunAudit(
            V7bLabContract.Version, approved.Receipt.ApprovalSha256,
            corpusHash, approved.Receipt.CriteriaSha256, partition,
            _model.ModelName, _model.ModelDigest, runId, started,
            Array.AsReadOnly(audits.ToArray()),
            "PRIVATE OWNER-REVIEWED INPUTS. V4 is tested using owner-supplied " +
            "oracle claim spans, not full real-world host retrieval. V3 extraction " +
            "results are scored separately. No release acceptance or enforcement.");
        return new V7bLabRun(score, audit);
    }

    private static LabUnit FreezeOwnerReviewed(
        V7ReviewedCase item, string corpusSha)
    {
        Guid Stable(string role)
        {
            var material = Encoding.UTF8.GetBytes(
                V7bLabContract.Version + "\n" + corpusSha + "\n" +
                item.CaseId + "\n" + role);
            return new Guid(SHA256.HashData(material).AsSpan(0, 16));
        }

        var session = new SessionId(Stable("session"));
        var opportunity = new OpportunityId(Stable("opportunity"));
        var speaker = new CharacterId(Stable("speaker"));
        var utterance = new Utterance(
            new UtteranceId(Stable("utterance")), session, opportunity,
            speaker, 1, item.UtteranceText, null, null,
            DateTimeOffset.UnixEpoch);
        var job = new VerificationJobIdentity(
            Stable("job"), session, opportunity, utterance.Id, speaker,
            0, EvidenceVerifierV1.ContractVersion,
            Hash(item.UtteranceText));
        var claim = new ClaimCandidate(
            Stable("claim"), job.JobId, 0, item.ClaimStartUtf16,
            item.ClaimLengthUtf16, item.ClaimText,
            item.Category, item.RhetoricalMode,
            ClaimExtractionProvenance.HostRule,
            ClaimValidationStatus.HostValidated);

        var evidence = item.Evidence.Select(e =>
            new EvidenceItem(
                e.ReferenceId, e.Kind, e.AuthorityTier,
                e.Visibility, "private-owner-review:" + item.CaseId,
                V7FreezeContract.ReviewVersion, Hash(e.Excerpt),
                e.Excerpt, 0, e.IsSuperseded,
                e.IsSuperseded ? "reviewed-supersession" : null)).ToArray();
        var completeness = new EvidenceCompleteness(
            evidence.Length == 0
                ? EvidenceCompletenessStatus.Unknown
                : EvidenceCompletenessStatus.CompleteForDeclaredScope,
            "private frozen owner-reviewed V7 evidence items",
            "owner-supplied lab scope, not production V2 retrieval",
            evidence.Length == 0 ? "No supplied reviewed evidence" : null);
        var packet = EvidencePacketFactory.Freeze(
            Stable("packet"), new EvidencePacketContent(
                EvidenceVerifierV1.ContractVersion,
                EvidenceVerifierV1.PacketSerializerVersion,
                job.JobId, claim.ClaimId, 0,
                V3EvidencePacketAssembler.RetrievalSelectorVersion,
                V3ModelClaimExtractor.PromptVersion,
                V3EvidencePacketAssembler.PacketSchemaVersion,
                completeness, evidence));
        var scope = new VerificationEvidenceScope(
            session, opportunity, speaker, 0,
            evidence.ToDictionary(e => e.ReferenceId, StringComparer.Ordinal));
        EvidenceVerificationValidator.ValidatePacket(
            packet, claim, job, utterance, scope);
        return new LabUnit(job, utterance,
            new V3PackagedClaim(claim, packet, scope));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record LabUnit(
        VerificationJobIdentity Job, Utterance Utterance,
        V3PackagedClaim Package);
}

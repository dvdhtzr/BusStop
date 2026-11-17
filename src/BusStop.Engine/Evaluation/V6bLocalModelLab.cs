using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Domain;
using BusStop.Engine.Verification;
using BusStop.Models;
using BusStop.Moderation.Evaluation;
using BusStop.Moderation.Verification;

namespace BusStop.Engine.Evaluation;

/// <summary>V6b remains a separately invoked development-only lab, not V4's
/// live-session or V5 charged inference path.</summary>
public static class V6bLabContract
{
    public const string Version = "busstop-v6b-local-model-lab-v1";
    public const int MaxCasesPerRun = 20;
    public const int MaxPromptUtf8Bytes = 28_000;
    public const int MaxResponseChars = 32_000;
    public const int MaxGeneratedTokens = 2048;
    public const int ContextTokens = 8192;
}

public sealed record V6bPrivateCaseAudit(
    string CaseId,
    string ClaimText,
    IReadOnlyList<V6SyntheticEvidence> FrozenSyntheticEvidence,
    string Prompt,
    string? RawModelResponse,
    VerificationOperationalStatus Status,
    string DiagnosticCode,
    ClaimJudgment? HostValidatedJudgment,
    ModelGenerationTelemetry? Generation,
    int ChargedCalls);

public sealed record V6bPrivateRunAudit(
    string ContractVersion,
    string CorpusVersion,
    string CorpusSha256,
    string LabelStatus,
    string RunId,
    string ModelName,
    string ModelDigest,
    DateTimeOffset StartedAtUtc,
    IReadOnlyList<V6bPrivateCaseAudit> Cases,
    string Warning);

public sealed record V6bLabRun(
    V6EvaluationReport Score,
    V6bPrivateRunAudit PrivateAudit);

/// <summary>
/// The lab uses the EXACT V4 prompt and V4 strict judgment parser/host lane
/// validator, with a wholly synthetic host-issued scope. This is a model-level
/// component test, NOT a real V2 retrieval test, live sampling estimate,
/// V5 budget proof, or release acceptance. At most one Ollama call per case.
/// </summary>
public sealed class V6bLocalModelLab
{
    private readonly IModelClient _model;

    public V6bLocalModelLab(IModelClient model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        if (string.IsNullOrWhiteSpace(model.ModelName) ||
            string.IsNullOrWhiteSpace(model.ModelDigest) ||
            model.ModelDigest.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "V6b requires a locally pinned model name and digest; refuse unpinned inference.");
    }

    public V6bLabRun Run(
        V6CorpusFixture corpus, string runId,
        int maxCases = V6bLabContract.MaxCasesPerRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        if (corpus.Document.CorpusVersion != V6EvaluationContract.CorpusVersion ||
            corpus.Document.LabelStatus != V6EvaluationContract.LabelStatus ||
            corpus.Document.Partition != V6EvaluationContract.Partition ||
            string.IsNullOrWhiteSpace(runId) || runId.Length > 128 ||
            maxCases < 1 || maxCases > V6bLabContract.MaxCasesPerRun)
            throw new InvalidDataException(
                "V6b only accepts bounded provisional synthetic development runs.");

        var started = DateTimeOffset.UtcNow;
        var observations = new List<V6CaseObservation>();
        var audits = new List<V6bPrivateCaseAudit>();
        foreach (var example in corpus.Document.Cases.Take(maxCases))
        {
            if (cancellationToken.IsCancellationRequested)
                break; // remaining cases appear unselected in the V6a denominator

            // Never include the provisional expectedOutcome, expected citation
            // lists, owner rationale or labels in the model prompt.
            var unit = FreezeSynthetic(example, corpus.Sha256);
            var prompt = V4GroundedVerifier.BuildPrompt(unit.Job,
                new[] { unit.Package });
            var promptBytes = Encoding.UTF8.GetByteCount(prompt);

            ModelResponse? response = null;
            ClaimJudgment? judgment = null;
            var status = VerificationOperationalStatus.NotStarted;
            string diagnosis;
            int charged = 0;
            if (promptBytes > V6bLabContract.MaxPromptUtf8Bytes)
            {
                status = VerificationOperationalStatus.Error;
                diagnosis = "synthetic-prompt-byte-ceiling";
            }
            else
            {
                // One direct local model call. This deliberately does NOT
                // reserve V4 shared production budget or write V5 ledger rows.
                charged = 1;
                try
                {
                    response = _model.Decide(new ModelRequest(
                        unit.Job.SpeakerId, prompt,
                        V4JudgmentParser.JsonSchema,
                        V6bLabContract.MaxGeneratedTokens,
                        V6bLabContract.ContextTokens), cancellationToken);
                    if (response is null ||
                        response.Transport != ModelTransport.Responded)
                    {
                        status = VerificationOperationalStatus.TransportFailure;
                        diagnosis = "model-transport-" +
                            (response?.Transport.ToString() ?? "null");
                    }
                    else if (string.Equals(response.Generation?.DoneReason,
                        "length", StringComparison.OrdinalIgnoreCase))
                    {
                        status = VerificationOperationalStatus.InvalidResponse;
                        diagnosis = "generation-truncated";
                    }
                    else if (response.RawJson is null ||
                             response.RawJson.Length > V6bLabContract.MaxResponseChars)
                    {
                        status = VerificationOperationalStatus.InvalidResponse;
                        diagnosis = "invalid-response-length";
                    }
                    else
                    {
                        try
                        {
                            var judgments = V4JudgmentParser.Parse(
                                response.RawJson, unit.Job,
                                unit.Utterance, new[] { unit.Package });
                            judgment = judgments.Single();
                            status = VerificationOperationalStatus.Completed;
                            diagnosis = "v4-host-validated";
                        }
                        catch (Exception ex) when (
                            ex is InvalidDataException or JsonException or
                                ArgumentException or InvalidOperationException)
                        {
                            status = VerificationOperationalStatus.InvalidResponse;
                            // Error *type*, never private raw model text
                            diagnosis = "v4-parser-" + ex.GetType().Name;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    status = VerificationOperationalStatus.TransportFailure;
                    diagnosis = "model-invocation-" + ex.GetType().Name;
                }
            }

            observations.Add(new V6CaseObservation(
                example.CaseId, V6SelectionDisposition.Selected,
                status, judgment?.Outcome,
                judgment?.SupportingReferences ?? Array.Empty<string>(),
                judgment?.OpposingReferences ?? Array.Empty<string>(),
                judgment?.ProblematicText ?? "",
                charged));

            audits.Add(new V6bPrivateCaseAudit(
                example.CaseId, example.ClaimText,
                Array.AsReadOnly(example.Evidence.ToArray()),
                prompt,
                response?.RawJson is { Length: <= V6bLabContract.MaxResponseChars }
                    ? response.RawJson : null,
                status, diagnosis,
                judgment, response?.Generation, charged));
        }

        var score = V6EvaluationScorer.Score(corpus, runId, observations);
        var audit = new V6bPrivateRunAudit(
            V6bLabContract.Version, corpus.Document.CorpusVersion,
            corpus.Sha256, corpus.Document.LabelStatus, runId,
            _model.ModelName, _model.ModelDigest, started,
            Array.AsReadOnly(audits.ToArray()),
            "PRIVATE SYNTHETIC DEVELOPMENT ONLY. This lab bypasses V2 live retrieval, " +
            "V5 budget/session orchestration and automatic rhetorical short-circuiting. " +
            "Labels are unreviewed; V4 host parsing is not semantic proof.");
        return new V6bLabRun(score, audit);
    }

    private static V6bFixtureUnit FreezeSynthetic(
        V6LabeledCase example, string corpusSha256)
    {
        // Stable identities are CRITICAL for valid repeated-input drift:
        // randomized job/claim/packet IDs would change the V4 prompt between
        // runs even if the underlying case had not changed.
        Guid Stable(string role) => StableId(corpusSha256, example.CaseId, role);
        var session = new SessionId(Stable("session"));
        var opportunity = new OpportunityId(Stable("opportunity"));
        var speaker = new CharacterId(Stable("speaker"));
        var speech = new Utterance(
            new UtteranceId(Stable("utterance")), session, opportunity,
            speaker, 1, example.ClaimText, null, null, DateTimeOffset.UnixEpoch);
        var job = new VerificationJobIdentity(
            Stable("job"), session, opportunity,
            speech.Id, speaker, 0, EvidenceVerifierV1.ContractVersion,
            Sha(example.ClaimText));
        var claim = new ClaimCandidate(
            Stable("claim"), job.JobId, 0, 0, example.ClaimText.Length,
            example.ClaimText, example.Category, example.RhetoricalMode,
            ClaimExtractionProvenance.HostRule,
            ClaimValidationStatus.HostValidated);

        var evidence = example.Evidence.Select(source => new EvidenceItem(
            source.ReferenceId, source.Kind, source.AuthorityTier,
            source.Visibility, "synthetic:" + example.CaseId + ":" +
                source.ReferenceId, "synthetic-v6b-1",
            Sha(source.Excerpt), source.Excerpt,
            0, source.IsSuperseded,
            source.IsSuperseded ? "synthetic-current-recollection" : null
        )).ToArray();
        var completeness = new EvidenceCompleteness(
            evidence.Length == 0
                ? EvidenceCompletenessStatus.Unknown
                : EvidenceCompletenessStatus.CompleteForDeclaredScope,
            "synthetic V6a fixture items ONLY",
            "fixed synthetic fixture list, not an exhaustive real-world search",
            evidence.Length == 0 ? "No synthetic items" : null);
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
            evidence.ToDictionary(x => x.ReferenceId, StringComparer.Ordinal));
        EvidenceVerificationValidator.ValidatePacket(
            packet, claim, job, speech, scope);
        return new V6bFixtureUnit(
            job, speech, new V3PackagedClaim(claim, packet, scope));
    }

    private static string Sha(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static Guid StableId(string corpusSha, string caseId, string role)
    {
        var input = Encoding.UTF8.GetBytes(
            V6bLabContract.Version + "\n" + corpusSha + "\n" +
            caseId + "\n" + role);
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed record V6bFixtureUnit(
        VerificationJobIdentity Job, Utterance Utterance,
        V3PackagedClaim Package);
}

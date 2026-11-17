using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Domain;
using BusStop.Engine.Verification;
using BusStop.Models;
using BusStop.Moderation;
using BusStop.Moderation.Verification;
using BusStop.Persistence;

namespace BusStop.Engine.Verification;

/// <summary>
/// V6c is separately opted-in, private-only and POST-COMMIT. It has no veto or
/// feedback into ConversationEngine. The queue is drained only AFTER the
/// authoritative session (and historical M1 drain) has finished, so verifier
/// Ollama calls cannot compete with active participant inference.
/// </summary>
public static class V6cShadowContract
{
    public const string EvaluationKey = "evidence-evaluation-v6c-shadow-v1";
    public const int MaxQueuedUtterances = 32;
    public const int MaxVerificationClaimsPerJob = EvidenceVerifierV4.MaxClaimsPerBatch;
}

public sealed record V6cShadowCoverage(
    string Contract,
    SessionId Session,
    int CommittedObserved,
    int NotSelected,
    int Selected,
    int Queued,
    int Dropped,
    int DurableJobsRegistered,
    int JobsSummarized,
    int IncompleteSummaries,
    int WorkerFailures,
    int SelectedWithoutDurableJob,
    string CoverageCaveat)
{
    public bool AllSelectedDurablyRegistered =>
        Selected == DurableJobsRegistered && Dropped == 0 && WorkerFailures == 0;
}

public sealed record V6cShadowWorkerFault(UtteranceId Utterance, string ExceptionType);

public sealed class V6cShadowVerifier : IShadowModerationSink
{
    private sealed record WorkItem(
        UtteranceId Id, OpportunityId Opportunity,
        CharacterId Speaker, long Boundary, string CommittedText);

    private readonly string _databasePath;
    private readonly SessionId _session;
    private readonly V4SessionPolicy _policy;
    private readonly IModelClient _model;
    private readonly int _sampleEvery;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Queue<WorkItem> _pending = new();
    private readonly List<V6cShadowWorkerFault> _faults = new();
    private bool _closed;
    private int _observed;
    private int _notSelected;
    private int _selected;
    private int _dropped;
    private int _registered;
    private int _summarized;
    private int _incomplete;
    private int _failures;

    public V6cShadowVerifier(
        string databasePath, SessionId session, V4SessionPolicy policy,
        IModelClient model, int sampleEvery = 1, int queueCapacity = 8)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(model);
        policy.Validate();
        if (sampleEvery is < 1 or > 1000 ||
            queueCapacity is < 1 or > V6cShadowContract.MaxQueuedUtterances)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        if (string.IsNullOrWhiteSpace(model.ModelDigest) ||
            string.Equals(model.ModelDigest, "unknown",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "V6c cannot dispatch a verifier with an unpinned local model digest.");
        if (policy.RuleVersion != V3ClaimDetector.RuleVersion ||
            policy.ExtractionPromptVersion != V3ModelClaimExtractor.PromptVersion ||
            policy.VerificationPromptVersion != EvidenceVerifierV4.VerificationPromptVersion ||
            policy.VerificationSchemaVersion != EvidenceVerifierV4.VerificationSchemaVersion ||
            policy.RetrievalSelectorVersion != V3EvidencePacketAssembler.RetrievalSelectorVersion ||
            policy.ModelName != model.ModelName ||
            policy.ModelDigest != model.ModelDigest ||
            policy.MaxVerificationCallsPerUtterance < 1)
            throw new InvalidDataException("V6c requires exactly pinned V3/V4 rules and Ollama model.");
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("V6c requires local file-backed SQLite.", nameof(databasePath));

        _databasePath = databasePath;
        _session = session;
        _policy = policy;
        _model = model;
        _sampleEvery = sampleEvery;
        _capacity = queueCapacity;
    }

    /// <summary>
    /// No I/O, channel wait, task, inference, source lookup, claim selection,
    /// SQLite or participant mutation. Only bounded identifier enqueue.
    /// </summary>
    public bool TryObserve(ShadowObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Candidate.Session != _session)
            return false;
        lock (_gate)
        {
            if (_closed)
                return false;
            _observed++;
            if (_observed % _sampleEvery != 0)
            {
                _notSelected++;
                return true;
            }

            _selected++;
            if (_pending.Count >= _capacity)
            {
                _dropped++;
                return false;
            }
            _pending.Enqueue(new WorkItem(
                observation.CommittedUtterance,
                observation.Candidate.Opportunity,
                observation.Candidate.Speaker,
                observation.Candidate.CommittedBoundary,
                observation.Candidate.Text));
            return true;
        }
    }

    /// <summary>
    /// Must be called AFTER ConversationEngine.Run and AFTER M1.DrainAsync.
    /// Only one caller may drain; the queue is closed irrevocably.
    /// The worker serializes all Ollama calls; never fires a background task
    /// until the owner explicitly invokes this method.
    /// </summary>
    public Task DrainAsync()
    {
        WorkItem[] jobs;
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("V6c queue already drained.");
            _closed = true;
            jobs = _pending.ToArray();
            _pending.Clear();
        }
        if (jobs.Length == 0) return Task.CompletedTask;
        return Task.Run(() => DrainOnWorker(jobs));
    }

    /// <summary>Private, bounded diagnostic codes only; never raw text.</summary>
    public IReadOnlyList<V6cShadowWorkerFault> Faults
    {
        get
        {
            lock (_gate) return Array.AsReadOnly(_faults.ToArray());
        }
    }

    public V6cShadowCoverage Coverage
    {
        get
        {
            lock (_gate)
            {
                return new V6cShadowCoverage(
                    "shadow-verifier-v6c-1", _session,
                    _observed, _notSelected, _selected,
                    _selected - _dropped, _dropped,
                    _registered, _summarized, _incomplete, _failures,
                    Math.Max(0, _selected - _registered),
                    "Best-effort IN-PROCESS observation coverage only. " +
                    "A crash before durable registration is UNKNOWN, not recovered. " +
                    "No verdict is delivered to participants; omitted/nonselected speech " +
                    "is not a clean verification result.");
            }
        }
    }

    private void DrainOnWorker(WorkItem[] queued)
    {
        // A single OS-held gate is required even for the first, pre-enrollment
        // registration. This worker does not participate in active dialogue.
        using var processGate = V5bProcessGate.Acquire(_databasePath);
        using var store = ConversationStore.OpenFile(_databasePath);
        store.InitializeSchema();
        var pinned = store.GetV4VerifierPolicy(_session);
        if (pinned != _policy)
            throw new InvalidDataException("V6c session policy changed since shadow admission.");

        V5bOwnership? owner = null;
        try
        {
            // An earlier worker may have left an epoch and registered jobs.
            // V5c recovery remains EXPLICIT; do not resume its old jobs here.
            if (store.HasV5bOwnershipEpoch(_session))
                owner = store.AcquireV5bSession(_session, processGate);

            foreach (var item in queued)
            {
                try
                {
                    var speech = store.GetCommittedUtterances(_session)
                        .SingleOrDefault(u => u.Id == item.Id)
                        ?? throw new InvalidDataException("Committed shadow speech disappeared.");
                    if (speech.Opportunity != item.Opportunity ||
                        speech.Speaker != item.Speaker ||
                        speech.Sequence - 1 != item.Boundary ||
                        !string.Equals(speech.Text, item.CommittedText,
                            StringComparison.Ordinal))
                        throw new InvalidDataException("Shadow identity/sequence no longer matches.");

                    var job = new VerificationJobIdentity(
                        StableJobId(speech), _session, speech.Opportunity,
                        speech.Id, speech.Speaker, item.Boundary,
                        EvidenceVerifierV1.ContractVersion, Hash(speech.Text));

                    if (owner is null)
                    {
                        // First enrollment only: register under the exclusive
                        // OS gate, THEN create first fencing epoch. This is
                        // required by V5b, which forbids owners with no jobs.
                        store.RegisterV5aJob(job, V6cShadowContract.EvaluationKey);
                        owner = store.AcquireV5bSession(_session, processGate);
                    }
                    else
                        store.RegisterV5aJob(job, V6cShadowContract.EvaluationKey, owner);

                    _registered++;
                    var report = ReviewOne(store, job, speech, owner);
                    _summarized++;
                    if (report.Summary.CompletionStatus != VerificationCompletionStatus.Complete)
                        _incomplete++;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Do not retry, refund, attach a different evidence scope,
                    // or coerce an incomplete job into a NoMaterialIssue.
                    // V5c's explicit recovery gate owns any orphaned stages.
                    _failures++;
                    lock (_gate)
                        _faults.Add(new V6cShadowWorkerFault(
                            item.Id, ex.GetType().Name));
                }
            }
        }
        finally
        {
            if (owner is not null)
                store.ReleaseV5bSession(owner);
        }
    }

    private V4ReviewReport ReviewOne(
        ConversationStore store, VerificationJobIdentity job,
        Utterance speech, V5bOwnership owner)
    {
        var extraction = V3ClaimDetector.Detect(job, speech);
        store.SealV5aClaims(job.JobId, extraction.Claims, owner);
        var assembly = new V3EvidencePacketAssembler(store).Assemble(
            job, speech, extraction);
        store.SealV5aPackets(job.JobId,
            assembly.Packets.Select(p => p.Packet).ToArray(), owner);

        var packages = assembly.Packets.ToDictionary(p => p.Claim.ClaimId);
        var findings = new Dictionary<Guid, VerificationClaimResult>();
        var candidates = new List<V3PackagedClaim>();
        var omitted = assembly.OmittedClaimIds.ToHashSet();

        foreach (var claim in extraction.Claims)
        {
            if (omitted.Contains(claim.ClaimId))
            {
                findings.Add(claim.ClaimId, new VerificationClaimResult(
                    claim.ClaimId, VerificationOperationalStatus.Error, null,
                    "V3 bounded packet assembly omitted this selected claim."));
                continue;
            }
            var pack = packages[claim.ClaimId];
            if (claim.RhetoricalMode != ClaimRhetoricalMode.Asserted)
            {
                findings.Add(claim.ClaimId,
                    Mechanical(pack, ClaimVerificationOutcome.NoMaterialIssue,
                        "Quoted, rhetorical or hypothetical; not treated as a factual assertion."));
                continue;
            }
            if (claim.Category == ClaimCategory.Other ||
                pack.Packet.Content.Items.Count == 0 ||
                pack.Packet.Content.Completeness.Status == EvidenceCompletenessStatus.Unknown)
            {
                findings.Add(claim.ClaimId,
                    Mechanical(pack, ClaimVerificationOutcome.InsufficientEvidence,
                        "Original host-authorized evidence does not support a material finding."));
                continue;
            }
            if (candidates.Count >= V6cShadowContract.MaxVerificationClaimsPerJob)
            {
                findings.Add(claim.ClaimId, new VerificationClaimResult(
                    claim.ClaimId, VerificationOperationalStatus.SkippedForBudget,
                    null, "V6c first-integration per-utterance verification selection cap."));
                continue;
            }
            candidates.Add(pack);
        }

        // Preflight the EXACT V4 prompt before charging a shared inference
        // budget slot. Unsupported oversized packets are explicit errors,
        // never silent successes or charged calls that were not dispatched.
        if (candidates.Count > 0 &&
            Encoding.UTF8.GetByteCount(
                V4GroundedVerifier.BuildPrompt(job, candidates)) > 28_000)
        {
            foreach (var pack in candidates)
                findings.Add(pack.Claim.ClaimId, new VerificationClaimResult(
                    pack.Claim.ClaimId, VerificationOperationalStatus.Error,
                    null, "V4 prompt exceeds the pinned UTF-8 byte ceiling."));
            candidates.Clear();
        }

        var chargedIds = new List<Guid>();
        Guid? charged = null;
        if (candidates.Count > 0)
        {
            var requestId = Guid.NewGuid();
            if (!store.TryReserveV4Attempt(job, requestId,
                    V4AttemptPhase.ClaimVerification, owner))
            {
                foreach (var pack in candidates)
                    findings.Add(pack.Claim.ClaimId, new VerificationClaimResult(
                        pack.Claim.ClaimId,
                        VerificationOperationalStatus.SkippedForBudget,
                        null, "Pinned V4/M1 session or utterance budget exhausted."));
            }
            else
            {
                charged = requestId;
                chargedIds.Add(requestId);
                // Link BEFORE dispatch, so a crash after this point leaves
                // an auditable charged reservation, not an invented judgment.
                store.LinkV5aChargedAttempt(job.JobId, requestId,
                    V4AttemptPhase.ClaimVerification,
                    candidates.Select(c => c.Claim.ClaimId).ToArray(), owner);

                ModelResponse? response = null;
                bool returned = false;
                VerificationOperationalStatus failure =
                    VerificationOperationalStatus.TransportFailure;
                string detail = "Local model invocation did not return a usable verdict.";
                ClaimJudgment[]? parsed = null;
                try
                {
                    var prompt = V4GroundedVerifier.BuildPrompt(job, candidates);
                    if (Encoding.UTF8.GetByteCount(prompt) > 28_000)
                    {
                        failure = VerificationOperationalStatus.Error;
                        detail = "Frozen host-authorized prompt exceeds V4 UTF-8 ceiling.";
                    }
                    else
                    {
                        try
                        {
                            response = _model.Decide(new ModelRequest(
                                job.SpeakerId, prompt,
                                V4JudgmentParser.JsonSchema,
                                _policy.MaxGeneratedTokens,
                                _policy.ContextTokens));
                            returned = true;
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            failure = VerificationOperationalStatus.TransportFailure;
                            detail = "Local Ollama invocation failed (no verdict).";
                        }

                        if (returned)
                        {
                            if (response?.Transport != ModelTransport.Responded)
                            {
                                failure = VerificationOperationalStatus.TransportFailure;
                                detail = "Local Ollama transport returned no verdict.";
                            }
                            else if (string.Equals(response.Generation?.DoneReason,
                                        "length", StringComparison.OrdinalIgnoreCase))
                            {
                                failure = VerificationOperationalStatus.InvalidResponse;
                                detail = "Bounded model response was truncated.";
                            }
                            else
                            {
                                try
                                {
                                    parsed = V4JudgmentParser.Parse(
                                        response.RawJson, job, speech, candidates).ToArray();
                                }
                                catch (Exception ex) when (ex is JsonException or
                                    InvalidDataException or InvalidOperationException or
                                    ArgumentException or FormatException or OverflowException)
                                {
                                    failure = VerificationOperationalStatus.InvalidResponse;
                                    detail = "V4 host rejected the model response.";
                                }
                            }
                        }
                    }
                }
                finally
                {
                    store.FinalizeModeratorAttempt(job.SessionId, requestId,
                        returned ? ModerationAttemptStatus.Completed :
                            ModerationAttemptStatus.Interrupted, owner);
                }

                for (var i = 0; i < candidates.Count; i++)
                {
                    var claim = candidates[i].Claim;
                    findings.Add(claim.ClaimId, parsed is not null
                        ? new VerificationClaimResult(
                            claim.ClaimId, VerificationOperationalStatus.Completed,
                            parsed[i], null)
                        : new VerificationClaimResult(
                            claim.ClaimId, failure, null, detail));
                }
            }
        }

        foreach (var claim in extraction.Claims)
        {
            var result = findings[claim.ClaimId];
            // Only claims in the actual V4 attempt group may cite its request.
            var request = charged.HasValue &&
                candidates.Any(p => p.Claim.ClaimId == claim.ClaimId)
                ? charged : null;
            store.RecordV5aClaimResult(job.JobId, result, request, owner);
        }

        var reconciled = V4HostReconciler.Reconcile(
            job, extraction, assembly,
            extraction.Claims.Select(c => findings[c.ClaimId]).ToArray(),
            chargedIds.Count, chargedIds);
        store.SealV5aSummary(job.JobId, reconciled.Summary, owner);
        return reconciled;
    }

    private static VerificationClaimResult Mechanical(
        V3PackagedClaim package, ClaimVerificationOutcome outcome, string reason)
    {
        var claim = package.Claim;
        var judgment = new ClaimJudgment(
            claim.ClaimId, package.Packet.PacketId, package.Packet.Sha256,
            outcome, Array.Empty<string>(), Array.Empty<string>(), "", reason);
        return new VerificationClaimResult(
            claim.ClaimId, VerificationOperationalStatus.Completed, judgment, null);
    }

    private static Guid StableJobId(Utterance speech)
    {
        var source = V6cShadowContract.EvaluationKey + "|" +
            speech.Session + "|" + speech.Opportunity + "|" + speech.Id;
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(source)).AsSpan(0, 16));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

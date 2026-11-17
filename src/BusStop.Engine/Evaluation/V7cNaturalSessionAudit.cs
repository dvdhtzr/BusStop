using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Domain;
using BusStop.Engine.Verification;
using BusStop.Moderation.Verification;
using BusStop.Persistence;

namespace BusStop.Engine.Evaluation;

/// <summary>
/// Owner-local natural-session observations only. This intentionally has NO
/// entry point to the live floor, moderator or participant inference.
/// </summary>
public static class V7cNaturalContract
{
    public const string Version = "busstop-v7c-natural-audit-v1";
    public const int MaximumAuditedUtterances = 2_000;
    public const int MaximumReviewTextLength = 20_000;
}

public sealed record V7cSessionTiming(
    long? ParticipantPhaseMilliseconds,
    long? M1DrainMilliseconds,
    long? VerifierDrainMilliseconds,
    string? TranscriptShaBeforeVerifier,
    string? TranscriptShaAfterVerifier);

/// <summary>
/// Fields mirror the console's existing PRIVATE v6c-coverage file.
/// Timing fields are additive and may be null on older sessions.
/// </summary>
public sealed record V7cCoverageEnvelope(
    string ContractVersion,
    string Session,
    V6cShadowCoverage Coverage,
    IReadOnlyList<V6cShadowWorkerFault>? WorkerFaults,
    V7cSessionTiming? Timing,
    string? Note);

public sealed record V7cPrivateTurn(
    long Sequence,
    string SpeakerId,
    string Text,
    int HostDetectedClaims,
    int HostAutobiographyCandidates,
    int HostDialogueAttributionCandidates,
    bool ExactFullUtteranceRepeated,
    bool HasDurableVerifierJob);

public sealed record V7cPrivateAudit(
    string ContractVersion,
    string SessionId,
    string TranscriptSha256,
    bool ThreeParticipantSession,
    bool TranscriptSequenceValid,
    bool TranscriptUnchangedAcrossVerifierDrain,
    bool TranscriptMutationCheckAvailable,
    bool CoverageMatchesCommittedCount,
    bool CoverageInternallyConsistent,
    bool LedgerMatchesCoverage,
    int CommittedUtterances,
    int V6cObserved,
    int V6cSelected,
    int V6cNotSelected,
    int V6cDropped,
    int V6cRegistered,
    int ValidatedLedgerJobs,
    int TerminalLedgerJobs,
    int NonterminalLedgerJobs,
    int IncompleteLedgerSummaries,
    int ChargedVerificationAttempts,
    int HostDetectedClaims,
    int HostAutobiographyCandidates,
    int HostDialogueAttributionCandidates,
    int RepeatedExactUtterances,
    IReadOnlyList<int> ParticipantTurnCounts,
    V7cSessionTiming? Timing,
    IReadOnlyList<V7cPrivateTurn> Turns,
    string Warning);

public sealed record V7cSafeOperationalReport(
    string ContractVersion,
    string TranscriptSha256,
    int ParticipantCount,
    int CommittedUtterances,
    int V6cObserved,
    int V6cSelected,
    int V6cNotSelected,
    int V6cDropped,
    int V6cRegistered,
    int TerminalLedgerJobs,
    int NonterminalLedgerJobs,
    int IncompleteLedgerSummaries,
    int ChargedVerificationAttempts,
    int HostAutobiographyCandidates,
    int HostDialogueAttributionCandidates,
    int RepeatedExactUtterances,
    IReadOnlyList<int> ParticipantTurnCounts,
    long? ParticipantPhaseMilliseconds,
    long? M1DrainMilliseconds,
    long? VerifierDrainMilliseconds,
    bool ThreeParticipantSession,
    bool TranscriptMutationCheckAvailable,
    bool TranscriptUnchangedAcrossVerifierDrain,
    bool CoverageMatchesCommittedCount,
    bool CoverageInternallyConsistent,
    bool LedgerMatchesCoverage,
    bool HumanAuthenticityReviewCompleted,
    bool NaturalConversationQualityAccepted,
    bool CrashRecoveryGateTested,
    bool BaselineLatencyComparisonComplete,
    bool SemanticAcceptanceGranted,
    bool EnforcementAuthorized,
    string Interpretation);

public static class V7cNaturalSessionAuditor
{
    /// <summary>
    /// Canonical, exact speaker/utterance/sequence/content digest. A before/
    /// after comparison must use this same method on the SAME session.
    /// </summary>
    public static string HashTranscript(IReadOnlyList<Utterance> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            turns.OrderBy(x => x.Sequence).Select(x => new
            {
                utterance = x.Id.ToString(),
                session = x.Session.ToString(),
                opportunity = x.Opportunity.ToString(),
                speaker = x.Speaker.ToString(),
                x.Sequence,
                x.Text,
                addressee = x.Addressee?.ToString(),
                repliesTo = x.RepliesTo?.ToString(),
                committed = x.CommittedAt.ToUniversalTime().ToString("O")
            }));
        return Convert.ToHexString(SHA256.HashData(payload))
            .ToLowerInvariant();
    }

    public static V7cPrivateAudit Audit(
        ConversationStore readOnlyStore,
        SessionId sessionId,
        V7cCoverageEnvelope coverage)
    {
        ArgumentNullException.ThrowIfNull(readOnlyStore);
        ArgumentNullException.ThrowIfNull(coverage);
        if (coverage.ContractVersion != V6cShadowContract.EvaluationKey ||
            coverage.Coverage is null ||
            coverage.Coverage.Session != sessionId ||
            coverage.Session != sessionId.ToString())
            throw new InvalidDataException(
                "V7c coverage identity or V6c evaluation contract mismatch.");

        var session = readOnlyStore.GetSession(sessionId)
            ?? throw new InvalidDataException("V7c session not found in SQLite.");
        var policy = readOnlyStore.GetV4VerifierPolicy(sessionId);
        if (policy?.Mode != VerificationOperatingMode.Shadow)
            throw new InvalidDataException(
                "V7c requires a session explicitly opted in to V4 shadow verification.");

        var committed = readOnlyStore.GetCommittedUtterances(sessionId);
        if (committed.Count > V7cNaturalContract.MaximumAuditedUtterances)
            throw new InvalidDataException("V7c session exceeds audit size ceiling.");

        var uniqueSpeakers = session.Participants.Distinct().ToArray();
        var three = uniqueSpeakers.Length == 3 &&
            session.Participants.Count == 3;
        var validSequences = committed.Select((x, i) =>
            x.Sequence == i + 1 &&
            x.Session == sessionId &&
            uniqueSpeakers.Contains(x.Speaker) &&
            (x.RepliesTo is null ||
                committed.Any(y => y.Id == x.RepliesTo &&
                    y.Sequence < x.Sequence))).All(x => x) &&
            committed.Select(x => x.Id).Distinct().Count() == committed.Count;

        var c = coverage.Coverage;
        bool internallyConsistent =
            c.CommittedObserved >= 0 && c.NotSelected >= 0 &&
            c.Selected >= 0 && c.Queued >= 0 && c.Dropped >= 0 &&
            c.DurableJobsRegistered >= 0 && c.JobsSummarized >= 0 &&
            c.IncompleteSummaries >= 0 && c.WorkerFailures >= 0 &&
            c.SelectedWithoutDurableJob >= 0 &&
            c.Selected + c.NotSelected == c.CommittedObserved &&
            c.Selected - c.Dropped == c.Queued &&
            c.DurableJobsRegistered <= c.Queued &&
            c.JobsSummarized <= c.DurableJobsRegistered &&
            c.IncompleteSummaries <= c.JobsSummarized &&
            c.SelectedWithoutDurableJob ==
                c.Selected - c.DurableJobsRegistered;

        var jobs = readOnlyStore.ListV7cVerificationJobs(
            sessionId, V6cShadowContract.EvaluationKey);
        var speechById = committed.ToDictionary(x => x.Id);
        var registeredSpeeches = new HashSet<UtteranceId>();
        var terminal = 0;
        var incomplete = 0;
        var charges = 0;
        foreach (var jobId in jobs)
        {
            // This runs the actual V5a identity, stage and hash-chain
            // verifier. A corrupt row raises, never silently gets scored.
            var checkpoint = readOnlyStore.ReadV5aCheckpoint(jobId);
            if (checkpoint.EvaluationKey != V6cShadowContract.EvaluationKey ||
                checkpoint.Job.SessionId != sessionId ||
                !speechById.ContainsKey(checkpoint.Job.UtteranceId) ||
                !registeredSpeeches.Add(checkpoint.Job.UtteranceId))
                throw new InvalidDataException(
                    "V7c found a cross-session or duplicate ledger registration.");
            var speech = speechById[checkpoint.Job.UtteranceId];
            EvidenceVerificationValidator.ValidateJob(
                checkpoint.Job, speech);
            charges += checkpoint.AttemptLinks.Count;
            if (checkpoint.IsTerminal)
            {
                terminal++;
                if (checkpoint.Summary!.CompletionStatus !=
                    VerificationCompletionStatus.Complete)
                    incomplete++;
            }
        }

        var transcriptHash = HashTranscript(committed);
        var timing = coverage.Timing;
        var timingsPresent = timing is not null &&
            IsSha(timing.TranscriptShaBeforeVerifier) &&
            IsSha(timing.TranscriptShaAfterVerifier);
        var unchanged = timingsPresent &&
            timing!.TranscriptShaBeforeVerifier == transcriptHash &&
            timing.TranscriptShaAfterVerifier == transcriptHash;

        var ledgerMatches = jobs.Count == c.DurableJobsRegistered &&
            terminal == c.JobsSummarized &&
            incomplete == c.IncompleteSummaries;
        var matchCount = c.CommittedObserved == committed.Count;

        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        int hostClaims = 0, bio = 0, dialogue = 0, repeated = 0;
        var turnRows = new List<V7cPrivateTurn>(committed.Count);
        foreach (var speech in committed)
        {
            var identity = new VerificationJobIdentity(
                Guid.NewGuid(), speech.Session, speech.Opportunity,
                speech.Id, speech.Speaker, speech.Sequence - 1,
                EvidenceVerifierV1.ContractVersion, Sha(speech.Text));
            var candidates = V3ClaimDetector.Detect(identity, speech).Claims;
            var personal = candidates.Count(x => x.Category is
                ClaimCategory.CharacterIdentity or ClaimCategory.PersonalHistory);
            var attributed = candidates.Count(x =>
                x.Category == ClaimCategory.DialogueAttribution);
            var repeat = !duplicates.Add(speech.Text);
            hostClaims += candidates.Count;
            bio += personal;
            dialogue += attributed;
            if (repeat) repeated++;
            turnRows.Add(new V7cPrivateTurn(
                speech.Sequence, speech.Speaker.ToString(), speech.Text,
                candidates.Count, personal, attributed,
                repeat, registeredSpeeches.Contains(speech.Id)));
        }

        return new V7cPrivateAudit(
            V7cNaturalContract.Version, sessionId.ToString(), transcriptHash,
            three, validSequences, unchanged, timingsPresent, matchCount,
            internallyConsistent, ledgerMatches,
            committed.Count, c.CommittedObserved, c.Selected,
            c.NotSelected, c.Dropped, c.DurableJobsRegistered,
            jobs.Count, terminal, jobs.Count - terminal,
            incomplete, charges, hostClaims, bio, dialogue, repeated,
            Array.AsReadOnly(uniqueSpeakers
                .Select(id => committed.Count(x => x.Speaker == id)).ToArray()),
            timing,
            Array.AsReadOnly(turnRows.ToArray()),
            "PRIVATE audit of real committed speech and V5a ledger. Host cue " +
            "counts are REVIEW CANDIDATES, not fabricated-history or semantic " +
            "error labels. In-memory queue losses before crash remain unknowable.");
    }

    public static V7cSafeOperationalReport ExportSafe(
        V7cPrivateAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return new V7cSafeOperationalReport(
            V7cNaturalContract.Version, audit.TranscriptSha256,
            audit.ParticipantTurnCounts.Count,
            audit.CommittedUtterances,
            audit.V6cObserved, audit.V6cSelected, audit.V6cNotSelected,
            audit.V6cDropped, audit.V6cRegistered,
            audit.TerminalLedgerJobs, audit.NonterminalLedgerJobs,
            audit.IncompleteLedgerSummaries, audit.ChargedVerificationAttempts,
            audit.HostAutobiographyCandidates,
            audit.HostDialogueAttributionCandidates,
            audit.RepeatedExactUtterances, audit.ParticipantTurnCounts,
            audit.Timing?.ParticipantPhaseMilliseconds,
            audit.Timing?.M1DrainMilliseconds,
            audit.Timing?.VerifierDrainMilliseconds,
            audit.ThreeParticipantSession,
            audit.TranscriptMutationCheckAvailable,
            audit.TranscriptUnchangedAcrossVerifierDrain,
            audit.CoverageMatchesCommittedCount,
            audit.CoverageInternallyConsistent,
            audit.LedgerMatchesCoverage,
            HumanAuthenticityReviewCompleted: false,
            NaturalConversationQualityAccepted: false,
            CrashRecoveryGateTested: false,
            BaselineLatencyComparisonComplete: false,
            SemanticAcceptanceGranted: false,
            EnforcementAuthorized: false,
            "Natural-session OPERATIONAL observation only. Manual authenticity " +
            "review not completed. This single run cannot establish a causal " +
            "participant-latency regression, V5c crash safety, human semantic " +
            "truth, or a V7 release pass.");
    }

    private static string Sha(string text) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static bool IsSha(string? text) =>
        text is { Length: 64 } &&
        text.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
}

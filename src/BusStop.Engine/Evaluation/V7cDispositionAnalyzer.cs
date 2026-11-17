using BusStop.Domain;
using BusStop.Engine.Verification;
using BusStop.Moderation.Verification;
using BusStop.Persistence;

namespace BusStop.Engine.Evaluation;

/// <summary>
/// Diagnostic counts from the already-frozen V5a ledger only. This does not
/// classify transcript truth, recover jobs, or initiate inference.
/// </summary>
public sealed record V7cSafeDisposition(
    string ContractVersion,
    string TranscriptSha256,
    int RegisteredJobs,
    int TerminalComplete,
    int TerminalPartial,
    int TerminalIncomplete,
    int Nonterminal,
    int JobsWithNoSelectedClaims,
    int JobsWithOnlyProtectedOrOtherClaims,
    int JobsWithAssertedMaterialButNoEligibleEvidence,
    int JobsEligibleForVerificationWithoutDispatch,
    int JobsWithVerificationDispatch,
    int JobsWithUnknownEvidencePacket,
    int JobsWithPartialEvidencePacket,
    int SelectedClaims,
    int CompletedClaims,
    int ConfirmedContradictions,
    int NoMaterialIssues,
    int InsufficientEvidence,
    int ConflictingEvidence,
    int SkippedForBudget,
    int FailedClaims,
    int ChargedV4Calls,
    bool LedgerAndTranscriptCrossChecked,
    bool HumanReviewCompleted,
    bool SemanticAcceptanceGranted,
    bool EnforcementAuthorized,
    string Interpretation);

/// <summary>Private sequence-based index to assist local human review.</summary>
public sealed record V7cPrivateDispositionTurn(
    long Sequence,
    string CompletionState,
    string Disposition,
    int SelectedClaims,
    int AssertedMaterialClaims,
    int EligibleClaimsWithEvidence,
    int CompletedClaims,
    int FailedOrSkippedClaims);

public sealed record V7cDispositionAnalysis(
    V7cSafeDisposition Safe,
    IReadOnlyList<V7cPrivateDispositionTurn> PrivateIndex);

public static class V7cDispositionAnalyzer
{
    public const string Version = "busstop-v7c-disposition-v1";

    public static V7cDispositionAnalysis Analyze(
        ConversationStore store, SessionId session, V7cPrivateAudit audit)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);
        var speech = store.GetCommittedUtterances(session);
        var hash = V7cNaturalSessionAuditor.HashTranscript(speech);
        if (audit.SessionId != session.ToString() ||
            audit.TranscriptSha256 != hash ||
            audit.CommittedUtterances != speech.Count ||
            !audit.LedgerMatchesCoverage ||
            !audit.CoverageInternallyConsistent)
            throw new InvalidDataException(
                "V7c diagnosis needs the matching original validated audit.");

        var bySpeech = speech.ToDictionary(x => x.Id);
        var jobs = store.ListV7cVerificationJobs(
            session, V6cShadowContract.EvaluationKey);
        if (jobs.Count != audit.ValidatedLedgerJobs ||
            jobs.Count != audit.V6cRegistered)
            throw new InvalidDataException(
                "V7c diagnosis must use the same original job set.");

        int complete = 0, partial = 0, incomplete = 0, nonterminal = 0;
        int noClaims = 0, protectedOnly = 0, noEvidence = 0;
        int eligibleNoCall = 0, dispatched = 0, unknown = 0, clipped = 0;
        int selected = 0, completed = 0, confirmed = 0, noIssues = 0;
        int insufficient = 0, conflicting = 0, skipped = 0, failed = 0;
        int allAttempts = 0, v4Calls = 0;
        var seen = new HashSet<UtteranceId>();
        var privateIndex = new List<V7cPrivateDispositionTurn>();

        foreach (var id in jobs)
        {
            // Existing production code independently validates event chain,
            // frozen packet, registered policy and committed job identity.
            var cp = store.ReadV5aCheckpoint(id);
            if (cp.EvaluationKey != V6cShadowContract.EvaluationKey ||
                cp.Job.SessionId != session ||
                !bySpeech.TryGetValue(cp.Job.UtteranceId, out var utterance) ||
                !seen.Add(cp.Job.UtteranceId))
                throw new InvalidDataException(
                    "Cross-session, missing or duplicated V7c verifier job.");
            EvidenceVerificationValidator.ValidateJob(cp.Job, utterance);

            if (cp.IsTerminal)
            {
                switch (cp.Summary!.CompletionStatus)
                {
                    case VerificationCompletionStatus.Complete: complete++; break;
                    case VerificationCompletionStatus.Partial: partial++; break;
                    case VerificationCompletionStatus.Incomplete: incomplete++; break;
                    default: throw new InvalidDataException("Unknown V5a completion state.");
                }
            }
            else nonterminal++;

            selected += cp.Claims.Count;
            completed += cp.ClaimResults.Count(x =>
                x.Status == VerificationOperationalStatus.Completed);
            confirmed += cp.ClaimResults.Count(x =>
                x.Judgment?.Outcome == ClaimVerificationOutcome.ConfirmedContradiction);
            noIssues += cp.ClaimResults.Count(x =>
                x.Judgment?.Outcome == ClaimVerificationOutcome.NoMaterialIssue);
            insufficient += cp.ClaimResults.Count(x =>
                x.Judgment?.Outcome == ClaimVerificationOutcome.InsufficientEvidence);
            conflicting += cp.ClaimResults.Count(x =>
                x.Judgment?.Outcome == ClaimVerificationOutcome.ConflictingEvidence);
            skipped += cp.ClaimResults.Count(x =>
                x.Status == VerificationOperationalStatus.SkippedForBudget);
            failed += cp.ClaimResults.Count(x =>
                x.Status is not (
                    VerificationOperationalStatus.Completed or
                    VerificationOperationalStatus.SkippedForBudget));
            allAttempts += cp.AttemptLinks.Count;
            v4Calls += cp.AttemptLinks.Count(x =>
                x.Phase == V4AttemptPhase.ClaimVerification);
            if (cp.Packets.Any(p =>
                p.Content.Completeness.Status == EvidenceCompletenessStatus.Unknown))
                unknown++;
            if (cp.Packets.Any(p =>
                p.Content.Completeness.Status == EvidenceCompletenessStatus.Partial))
                clipped++;

            int asserted = cp.Claims.Count(x =>
                x.RhetoricalMode == ClaimRhetoricalMode.Asserted &&
                x.Category != ClaimCategory.Other);
            int eligible = cp.Claims.Count(x =>
                x.RhetoricalMode == ClaimRhetoricalMode.Asserted &&
                x.Category != ClaimCategory.Other &&
                cp.Packets.Any(p => p.Content.ClaimId == x.ClaimId &&
                    p.Content.Items.Count > 0 &&
                    p.Content.Completeness.Status !=
                        EvidenceCompletenessStatus.Unknown));

            string disposition;
            if (!cp.IsTerminal)
                disposition = "NonterminalRequiresSeparateRecovery";
            else if (cp.AttemptLinks.Any(x =>
                x.Phase == V4AttemptPhase.ClaimVerification))
            {
                disposition = "VerificationDispatched";
                dispatched++;
            }
            else if (cp.Claims.Count == 0)
            {
                disposition = "NoHostSelectedClaims";
                noClaims++;
            }
            else if (asserted == 0)
            {
                disposition = "OnlyProtectedOrOtherClaims";
                protectedOnly++;
            }
            else if (eligible == 0)
            {
                disposition = "NoEligibleEvidenceInFrozenPackets";
                noEvidence++;
            }
            else
            {
                disposition = "EligibleButNoDispatchNeedsInvestigation";
                eligibleNoCall++;
            }

            privateIndex.Add(new V7cPrivateDispositionTurn(
                utterance.Sequence,
                cp.Summary?.CompletionStatus.ToString() ?? "Nonterminal",
                disposition, cp.Claims.Count, asserted, eligible,
                cp.ClaimResults.Count(x =>
                    x.Status == VerificationOperationalStatus.Completed),
                cp.ClaimResults.Count(x =>
                    x.Status != VerificationOperationalStatus.Completed)));
        }

        if (complete + partial + incomplete != audit.TerminalLedgerJobs ||
            partial + incomplete != audit.IncompleteLedgerSummaries ||
            nonterminal != audit.NonterminalLedgerJobs ||
            noClaims + protectedOnly + noEvidence + eligibleNoCall +
                dispatched + nonterminal != jobs.Count ||
            allAttempts != audit.ChargedVerificationAttempts ||
            completed != confirmed + noIssues + insufficient + conflicting)
            throw new InvalidDataException(
                "Derived dispositions disagree with the original V7c audit.");

        var safe = new V7cSafeDisposition(
            Version, hash, jobs.Count, complete, partial, incomplete,
            nonterminal, noClaims, protectedOnly, noEvidence,
            eligibleNoCall, dispatched, unknown, clipped,
            selected, completed, confirmed, noIssues,
            insufficient, conflicting, skipped, failed, v4Calls,
            LedgerAndTranscriptCrossChecked: true,
            HumanReviewCompleted: false,
            SemanticAcceptanceGranted: false,
            EnforcementAuthorized: false,
            "V5a ledger-only classifications. Partial means incomplete declared " +
            "source or extraction coverage; Incomplete includes failed or " +
            "budget-skipped claims. No claim, no qualifying evidence, or " +
            "no V4 dispatch is NOT evidence of truthful speech. " +
            "No dialogue text or case IDs appear in this aggregate.");
        return new V7cDispositionAnalysis(safe,
            Array.AsReadOnly(privateIndex.OrderBy(x => x.Sequence).ToArray()));
    }
}

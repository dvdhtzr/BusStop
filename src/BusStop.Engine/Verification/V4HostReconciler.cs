using BusStop.Domain;
using BusStop.Moderation.Verification;

namespace BusStop.Engine.Verification;

/// <summary>
/// Pure deterministic report builder. No model is consulted to reconcile claims.
/// The identified-claims denominator is preserved even when retrieval or
/// inference omitted an item.
/// </summary>
public static class V4HostReconciler
{
    public static V4ReviewReport Reconcile(
        VerificationJobIdentity job,
        V3ExtractionResult extraction,
        V3PacketAssembly assembly,
        IReadOnlyList<VerificationClaimResult> results,
        int verificationAttemptsThisRun,
        IReadOnlyList<Guid> chargedRequestIds)
    {
        if (job is null || extraction?.Claims is null ||
            assembly?.Packets is null || assembly.OmittedClaimIds is null ||
            results is null || chargedRequestIds is null ||
            verificationAttemptsThisRun is < 0 or > EvidenceVerifierV4.MaxVerificationCalls ||
            chargedRequestIds.Count != verificationAttemptsThisRun ||
            chargedRequestIds.Any(id => id == Guid.Empty) ||
            chargedRequestIds.Distinct().Count() != chargedRequestIds.Count)
            throw new InvalidDataException("Invalid host-reconciliation request.");

        var candidates = extraction.Claims.ToDictionary(c => c.ClaimId);
        if (results.Count != candidates.Count ||
            results.Select(r => r.ClaimId).Distinct().Count() != results.Count ||
            results.Any(r => !candidates.ContainsKey(r.ClaimId)))
            throw new InvalidDataException(
                "Host reconciliation requires one explicit result for every identified claim.");

        var packages = assembly.Packets.ToDictionary(p => p.Claim.ClaimId);
        var omitted = assembly.OmittedClaimIds.ToHashSet();
        if (packages.Keys.Intersect(omitted).Any() ||
            packages.Keys.Concat(omitted).Distinct().Count() != candidates.Count ||
            packages.Keys.Concat(omitted).Any(id => !candidates.ContainsKey(id)))
            throw new InvalidDataException(
                "V3 packaged and omitted claims must exactly cover the extracted set.");

        int contradictions = 0, noIssues = 0, insufficient = 0, conflicting = 0;
        int skipped = 0, failed = 0, verified = 0;
        var ordered = new List<VerificationClaimResult>();

        foreach (var candidate in extraction.Claims)
        {
            var result = results.Single(x => x.ClaimId == candidate.ClaimId);
            if (!Enum.IsDefined(typeof(VerificationOperationalStatus), result.Status))
                throw new InvalidDataException("Invalid reconciled operational status.");

            if (result.Status == VerificationOperationalStatus.Completed)
            {
                if (result.Judgment is null || result.FailureDetail is not null ||
                    !packages.TryGetValue(result.ClaimId, out var package))
                    throw new InvalidDataException(
                        "A completed claim must have a frozen packet and a judgment.");

                if (result.Judgment.ClaimId != candidate.ClaimId ||
                    result.Judgment.PacketId != package.Packet.PacketId ||
                    result.Judgment.PacketSha256 != package.Packet.Sha256 ||
                    !Enum.IsDefined(typeof(ClaimVerificationOutcome), result.Judgment.Outcome))
                    throw new InvalidDataException("Completed judgment no longer matches its packet.");

                verified++;
                switch (result.Judgment.Outcome)
                {
                    case ClaimVerificationOutcome.ConfirmedContradiction: contradictions++; break;
                    case ClaimVerificationOutcome.NoMaterialIssue: noIssues++; break;
                    case ClaimVerificationOutcome.InsufficientEvidence: insufficient++; break;
                    case ClaimVerificationOutcome.ConflictingEvidence: conflicting++; break;
                }
            }
            else
            {
                if (result.Judgment is not null)
                    throw new InvalidDataException(
                        "Failed or skipped claims cannot be reconciled as epistemic judgments.");
                if (result.Status == VerificationOperationalStatus.SkippedForBudget)
                    skipped++;
                else
                    failed++;
            }

            ordered.Add(result);
        }

        var completeSelection = extraction.SelectionComplete &&
            assembly.JobCoverage == EvidenceCompletenessStatus.CompleteForDeclaredScope;
        var completion = failed > 0 || skipped > 0
            ? VerificationCompletionStatus.Incomplete
            : completeSelection
                ? VerificationCompletionStatus.Complete
                : VerificationCompletionStatus.Partial;

        // "Complete" is ONLY complete disposition of the IDENTIFIED candidate
        // set and its declared source scope, not proof every meaningful
        // assertion or contradictory source in the world was discovered.
        var detail = completeSelection
            ? "All selected claims and declared evidence scope were dispositioned; semantic coverage not certified."
            : "Partial or unknown extraction/evidence coverage; no universal negative inference permitted.";
        var summary = new VerificationObservationSummary(
            job.JobId, extraction.Claims.Count, verified, contradictions,
            noIssues, insufficient, conflicting, skipped, failed, completion,
            detail);
        EvidenceVerificationValidator.ValidateSummary(summary);

        return new V4ReviewReport(job, Array.AsReadOnly(ordered.ToArray()),
            summary, verificationAttemptsThisRun,
            Array.AsReadOnly(chargedRequestIds.ToArray()), contradictions > 0);
    }
}

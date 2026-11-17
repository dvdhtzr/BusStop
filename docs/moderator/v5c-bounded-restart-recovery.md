# BusStop V5c — Bounded, Explicit Restart Recovery

**Status:** Draft, not Windows-GREEN, unmerged. **Parent:** V5b PR #19 at `af8a7b25facf6b970e6e1a4c35c6120f29b68518`.
**Architecture:** Approved Evidence-Grounded Verifier V1–V7 Specification §11 and §10. V5c completes the persistence/recovery gates, not model orchestration, live event subscriptions, or enforcement.

## Recovery invariants

1. **Successor owner only.** V5c's inspection requires a strictly newer V5b session ownership epoch (2 or above) and the still-held exclusive OS gate. A first-generation owner may still have active work; it is not a recovery worker. Every mutation rechecks ownership inside SQLite's immediate transaction.
2. **Bounded admission.** `ReviewV5cBatch(owner, runtime, limit, afterJobId)` keyset-paginates the private registered nonterminal jobs by durable job ID, at most **four jobs per invocation**. Excludes already stopped and terminal jobs. No timers, repeated polling, parallel dispatch, or inference calls are created.
3. **Original evidence and pinning.** Each job uses `ReadV5aCheckpoint` to validate its hash chain, committed utterance and original boundary, per-claim frozen packet canonical bytes/hashes, immutable V4 policy, and recorded outcomes. The caller supplies current runtime pins: exact model name/digest, rule, extraction/verification prompt, retrieval selector and schema. Any mismatch becomes a private `StoppedIncompatibleCheckpoint` record, not a silent model upgrade or evidence replacement.
4. **No regeneration of missing stages.** No sealed claim snapshot or packet snapshot => `StoppedMissingFrozenStage`; a pending claim lacking an original packet => `StoppedMissingClaimPacket`. These create an immutable, separate incomplete/error `verifier_v5c_stops` row. Neither V5a events nor committed speech is overwritten, and all verifier write/reservation paths reject a stopped job.
5. **Charged attempts retained.** Recovery inspects every V4 phase/reservation for the job, including requests that crashed before V5a attempt-link persistence. Session, utterance, opportunity, phase/purpose, V4 policy version and V5b owner stamp must match. Missing or future ownership fails closed (`StoppedUnknownAttemptOwner`), never assumed to be the current worker.
6. **Operational interruption, not a judgment.** An older-epoch `Reserved` request first yields `NeedsOrphanReconciliation`. Only the current fenced successor may explicitly call `InterruptV5cAbandonedRequest`, which atomically moves the *existing* charged request status from Reserved to Interrupted and appends one immutable `verifier_v5c_reconciliations` row identifying both epochs and the pinned policy hash. Exact repeats by that owner are idempotent; an already Completed request or current-epoch request cannot be interrupted through this method. No refund, reused request ID, fabricated response, or automatic retry.
7. **Active requests cannot be stolen.** A Reserved request stamped with the current epoch yields `BlockedActiveAttempt`. Recovery does not assume the call is abandoned. A new owner epoch must first be acquired after the old OS lock is released.
8. **Safe resumable work.** Once all reservations are final, remaining claims have frozen packets and budget remains, `ReadyForFrozenVerification` identifies claim IDs and remaining session/utterance/verification ceilings. This is an **advisory plan**, not a model call or independent inference permit. Any future caller must explicitly dispatch using existing V4 fresh unique request IDs and V5b fencing; every request charges the original shared budget. If the budget is exhausted, the evaluation receives an immutable `StoppedNoRemainingBudget` incomplete record.
9. **Safe deterministic close.** With all identified claims already durably dispositioned and no Reserved requests, `ReadyForSummary` permits `SealV5cRecoveredSummary`. It reconstructs counts ONLY from existing persisted claim judgments/operational statuses and writes a **Partial** or **Incomplete** V5a terminal summary through the normal fenced V5a stage API. It never certifies exhaustive source/selection completeness, makes a second verification call, or changes an existing result.
10. **No truth from absence.** A missing judgment, skipped claim, interrupted request, corrupt snapshot, model/version mismatch or omitted source cannot be represented as a clean/no-issue factual outcome. Stop records are operational audit metadata, not participant-facing moderation or durable character memory.

## Additive SQLite relations

- `verifier_v5c_schema`: immutable exact version pin `verifier-recovery-v5c-1`; unknown/partial versions block migration.
- `verifier_v5c_reconciliations`: unique request ID, original job, old epoch, recovery epoch, recovery owner, policy hash and `InterruptedAfterCrash`. Append-only; updates and deletes rejected.
- `verifier_v5c_stops`: one immutable operational stop per job with reason code and recovery owner epoch. A separate stopped evaluation never becomes a V5a epistemic summary.

## Fault boundaries and expected behavior

| Boundary | Recovery behavior |
|---|---|
| Crash before V5a registration | Not recoverable; no invented job/guarantee |
| After registration, before claims | Explicit incomplete/error stop, no new extraction |
| After claims, before packet freeze | Explicit stop; no fresh as-of evidence selection |
| After packet freeze | Inspect only frozen packets; plan pending claims when budgets permit |
| After charged reservation, before response | Request remains spent; classify old Reserved as Interrupted with append-only audit; no same-request re-dispatch |
| After response, before durable judgment | No recovered model response is invented; pending claim remains unresolved, next action manual and budget-fenced |
| After recorded judgment, before summary | Preserve judgment and packet; deterministically seal Partial/Incomplete summary without model |
| After terminal summary | No further write/recovery; batch scan excludes terminal jobs |
| Tampered snapshot, changed pins, unknown request owner | Append private stop and block all verifier work for that evaluation |

## Threat model and isolation

V5c is deliberately PRIVATE and **manually invoked**. It does not subscribe to the live SPEAK path, create a background worker, query Ollama, rebuild authorizations from new source content, change participant context, or affect the historical M1 audit/corpus. Existing M1 moderation reservations and finalizations stay independently usable. V5b local file-lock limitations remain; this is not a malicious administrator or database-backup tampering defense.

## Owner Windows acceptance gate

Run from a clean BusStop tree after fetching this draft branch:

```powershell
cd C:\Users\dvdht\source\repos\BusStop
git status --short
git fetch origin
git switch feature/moderator-v5c-bounded-recovery
git pull --ff-only origin feature/moderator-v5c-bounded-recovery
git rev-parse HEAD

dotnet build .\BusStop.sln -c Release
if ($LASTEXITCODE -ne 0) { throw 'V5c Release build FAILED' }
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'V5c regression tests FAILED' }
dotnet build .\tests\BusStop.V4.OllamaProbe\BusStop.V4.OllamaProbe.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'V4 probe build FAILED' }
Write-Host 'V5c LOCAL GREEN' -ForegroundColor Green
```

Required regression checks: M0/M1/V1–V5b all still pass; V5c model-free tests cover first-epoch refusal, missing snapshots, digest mismatch, older/current-epoch Reserved behavior, no refund, exact orphan interruption idempotency, summary-from-results, exhausted budget stop, raw DB corruption fail closed, unknown owner stamp, ≤4 jobs per batch, SQLite reopen, immutable recovery tables and future schema refusal.

**GREEN-before-merge:** This GitHub-only authoring environment has not compiled or executed Windows tests. Keep the PR draft and unmerged until owner reports GREEN and explicitly approves the next merge. No V6 implementation until its separate architecture/acceptance gate.
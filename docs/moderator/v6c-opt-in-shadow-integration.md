# BusStop V6c — Explicit Post-Commit Shadow Verifier

**Status:** Draft, not Windows GREEN, unmerged. **Parent:** merged V6b PR #22 `a30d567cc90b8d7233fe669806af39a470927003`.
**Approval:** V6c opt-in shadow integration; no enforcement or automatic V7 acceptance.

## Design and non-negotiable boundaries

V6c introduces a **second, independently opted-in verifier observer** alongside the existing M1 observer. Legacy sessions are disabled by default. Opt-in is only allowed for a NEW session, through a version-pinned V4 shadow policy persisted **before the first speaking opportunity**. No verifier enrollment is inferred merely from installing SQLite schema or from enabling historical M1.

`ConversationEngine` accepts an additional optional `verifierShadowSink`. It validates the immutable V4 policy and sends a separate best-effort observation only **after authoritative speech is committed**. The existing M1 validation, moderator sink, historical schema, policy and `TryObserve` call remain unchanged. V6c has no access to participant prompt/context assembly, floor choice, SPEAK commits, recollection writes, or enforcement decisions. An observer exception is logged by type only and cannot convert a successful SPEAK into a failure.

## Participant-first scheduling (deferred by design)

The first V6c integrated worker uses a bounded **in-memory** queue (configurable 1..32, default 8) and sampling every Nth committed utterance. `TryObserve` performs only a bounded, synchronous queue operation under a short in-process lock: no SQLite, source retrieval, LLM, waiting, or task creation. Full queues cause **dropped** observations and a false return; non-selected observations are counted separately. It stores the original candidate text for later exact committed-text validation.

**Important architectural tradeoff:** V6c deliberately does **NOT** perform verification inference concurrently with dialogue. The console invokes its explicit drain only **AFTER** ConversationEngine.Run has completed and after historical `ShadowModerationWorker.DrainAsync` has finished. Then it executes a single serialized verifier worker. This guarantees model priority to active participants and M1 for the first opt-in integration. V6c is therefore post-commit shadow monitoring, **not real-time moderator feedback**. It cannot delay an utterance, but it may leave queue drops during long conversations. A future separately approved iteration could add fair background scheduling after a measurable resource-contention lab.

The in-memory queue does not survive process termination. A crash between committed SPEAK and durable verifier job registration remains **unknown/unverified**, not retrospectively classified as selected, accepted, rejected, or complete. `V6cShadowCoverage` reports committed observed, not selected, selected, queued, dropped, registered, summarized, partial/incomplete, worker failures, and selected without a durable job. These counters cover the current process run only; they are not an after-crash proof of complete event capture.

## Durable, bounded admission and the V3–V5 chain

During explicit drain, the worker obtains V5b's local OS gate. It validates each queued record against authoritative SQLite: committed utterance ID, exact text, opportunity, speaker and original committed boundary. A stable job ID derives from session/opportunity/utterance and `evidence-evaluation-v6c-shadow-v1`. On the first job it registers under the exclusive OS lock before acquiring the initial V5b SQLite epoch; later jobs use the current fenced owner token. An older epoch may be superseded but its previously abandoned job is **not** automatically recovered: V5c has a separate explicit owner-controlled recovery path.

Per admitted job:

1. Use `V3ClaimDetector.Detect` only (host cues; model-assisted extraction is deliberately **not** enabled in the first integrated gate) and freeze extracted claims into V5a.
2. Assemble evidence using the actual host-backed `V3EvidencePacketAssembler` at the original boundary. Selection is limited to sources the speaker could already access; V5a freezes packets and source hashes. Missing/omitted packets remain operational failures, never invented negative source evidence.
3. Short-circuit nonasserted questions, hypotheticals, quotes and humor with a no-call **NoMaterialIssue** (not truth certification); unsupported asserted claims yield **InsufficientEvidence**. Unselected excess claims remain explicitly skipped under the initial first-integrated claim limit.
4. For up to **four** asserted, relevant, packet-backed claims, reserve **at most one** charged V4 verification attempt per job through the existing shared M1/V4 reservation table and current V5b fencing. Link this exact request ID in V5a BEFORE dispatch. Submit the unchanged V4 prompt and strict JSON schema to the explicitly pinned local Ollama model, then finalize with the same owner. On an exception, timeout, truncated output, wrong identity/citation or invalid host judgment, preserve operational failure; no automatic retry, refund or replacement evidence.
5. Record every selected claim's result, including skipped/error/insufficient outcomes, in the immutable ledger and derive the summary through `V4HostReconciler`. A full or partial/incomplete private terminal summary never feeds back into dialogue.

**Resource ceilings:** V4 pinned session cap (max 1000 at CLI), 3 total/utterance, 2 verification/utterance remain the authoritative hard ceilings, shared with M1. V6c's narrower first-integrated limit is 1 verification attempt/job and 4 claims/model call. There is no model-assisted extraction. Actual attempt reservations remain charged across process crashes; V5c handles recovery but the worker never silently resumes previously abandoned jobs. No SQLite transaction is held during Ollama inference.

## Console opt-in

New flag `--verifier-shadow` is required, as is `--model ollama`. Optional flags: `--verifier-model`, `--verifier-budget`, `--verifier-sample-every`, `--verifier-queue-capacity`, `--verifier-timeout`. The verifier itself is **restricted to local loopback 127.0.0.1:11434** regardless of participant/model endpoint; unavailable/unknown model digest refuses opt-in. Pinned V3 rule, extraction/verification prompt, selector, schema, model digest, generation/context limits and session budget are persisted BEFORE the session begins. The historical `--shadow-moderator` flag remains independent; both can be explicitly enabled in one session.

Example controlled local smoke (store outputs OUTSIDE the repo):

```powershell
$out = Join-Path $env:TEMP 'BusStop-V6c-Shadow'
dotnet run --project .\src\BusStop.Console\BusStop.Console.csproj -c Release -- `
    --model ollama --ollama-model qwen3.5:9b --no-think `
    --max-opportunities 8 --max-model-calls 18 `
    --verifier-shadow --verifier-model qwen3.5:9b `
    --verifier-budget 8 --verifier-sample-every 1 `
    --verifier-queue-capacity 2 --out $out
if ($LASTEXITCODE -ne 0) { throw 'V6c local shadow smoke FAILED' }
```

Opt-in processing executes after dialogue ends, so a queue size of two intentionally drops all further selected observations from a long run; that is expected and measurable. The private coverage JSON is written to `<out>/v6c-private/v6c-coverage-<session>.private.json`. No private claims, evidence excerpts or raw model responses are exported into the console summary; source and judgments remain in local SQLite. `**/v6c-private/` is ignored by Git, but an external private directory is recommended.

## Required model-free GREEN gate

```powershell
cd C:\Users\dvdht\source\repos\BusStop
git status --short
git fetch origin
git switch feature/moderator-v6c-opt-in-shadow-integration
git pull --ff-only origin feature/moderator-v6c-opt-in-shadow-integration
git rev-parse HEAD

dotnet build .\BusStop.sln -c Release
if ($LASTEXITCODE -ne 0) { throw 'V6c Release build FAILED' }
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'V6c regression tests FAILED' }
dotnet build .\tests\BusStop.V6.OllamaLab\BusStop.V6.OllamaLab.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'V6b probe build FAILED' }
dotnet build .\tests\BusStop.V4.OllamaProbe\BusStop.V4.OllamaProbe.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'V4 probe build FAILED' }
Write-Host 'V6c LOCAL GREEN' -ForegroundColor Green
```

Model-free `VerifierV6cShadowIntegrationTests` cover default disabled/pinned opt-in, M1 isolation, no inference before drain, queue overflow and sampling coverage, actual V3/V4/V5 frozen post-commit route, invalid model response incompleteness, protected rhetorical no-call, shared V4 budget refusal, crash-before-drain unknown interval, and unchanged committed speech.

## Unimplemented gates

- No live parallel model scheduling, per-turn interactive verification, participant-facing findings, external Wikipedia/RAG research, self-certifying owner-reviewed labels, or V7 enforcement.
- No generic extraction LLM in V6c; host-first candidate selection means claim recall is unmeasured and may be low.
- No restoration of a lost pre-registration queue. The V5c ledger-recovery protocol applies only to already registered jobs.
- No retroactive enrichment using passages not visible to a speaker at the committed boundary.

**GREEN-before-merge:** GitHub authoring cannot run Windows .NET or local Ollama. Owner must run the commands and inspect private local audit; this draft remains unmerged until explicit approval. V7 semantic release-readiness requires separately frozen owner-reviewed development/holdout labels, predeclared thresholds, independent natural-session studies and approval.
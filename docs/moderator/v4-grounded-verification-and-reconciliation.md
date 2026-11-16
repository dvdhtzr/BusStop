# BusStop V4 — Grounded verification and deterministic host reconciliation

**Status:** Draft implementation; owner-local GREEN and explicit merge approval required.  
**Base:** `main` after PR #16, merge `74a8f3d7d3a524bb009e299d7bf987138d830474`.  
**Approved specification:** `BusStop_Evidence_Grounded_Verifier_V1-V7_Specification.md`, §§9–10 and V4 in §14.

## What V4 implements

1. **Explicit shadow-only V4 session policy** (`V4SessionPolicy`): one immutable opt-in row per new session, pinned *before* the first opportunity. Missing row means disabled. Model name/digest, temperature, think flag, extraction rule/prompt version, verification prompt/schema/selector, session/utterance ceilings, context and output limits are recorded. The old `moderation-m0-v1` marker and M1 review rows retain their original meanings.
2. **Shared durable pre-dispatch attempt accounting:** V4 adds `VerifierClaimExtraction` and `VerifierClaimVerification` purposes to the EXISTING `moderator_inference_reservations` table and uses a new `v4_verifier_attempt_phases` append-only linkage. Under one BEGIN IMMEDIATE transaction, it checks new request ID uniqueness, the V4 pinned session ceiling, the M1 ceiling when enabled, the per-utterance ceiling (max three, including failed or interrupted calls) and phase ceilings (max one extraction and two verification). No refunded attempts, no borrowed M1 slots, no double charge table. Actual retry always receives a fresh request ID. Original committed text and recorded opportunity boundary must match.
3. **V3 extraction adapter:** `V4SharedExtractionPermit` allows the optional isolated V3 extractor to reserve from the same table. The isolated caller must finalize after use; an abrupt crash leaves a charged `Reserved` row for V5 recovery.
4. **Versioned strict verdict schema:** `V4JudgmentParser` requires exactly one independently keyed outcome per claim with packet ID/hash, outcome, cited support/opposition references, exact problematic text and explanation. Rejects missing/extra/duplicated fields, wrong IDs/hashes, unauthorized citations, malformed JSON, overlong responses and truncation. No confidence score or hidden "acceptance" flag.
5. **Conservative typed evidence authority:** A direct material `ConfirmedContradiction` requires cited opposing evidence of the correct category. For own identity it must be a binding typed anchor; for personal history it must be a binding anchor or non-superseded established self-recollection. A novel statement without that support is **InsufficientEvidence**, not a contradiction. Source reporting remains source-scoped, and prior conversation proves what was said, not necessarily truth. Conflicting sources must be distinct, non-superseded and separately attributable: a briefing plus excerpts from its one source is not two independent reports. Existing V2 permits one pinned source, so full multi-source conflict cases await separately approved exposures.
6. **Batching and retries:** At most four independent claims per model request, two attempted verification calls per utterance **including retry**, and at most three total calls with optional V3 extraction. Host reauthorizes each packet against V2 persisted scope at original boundary before dispatch. If too large, caps/failures/budget exhaustion are explicit operational statuses; no synthetic `NoMaterialIssue` is fabricated.
7. **Model-free deterministic reconciliation** (`V4HostReconciler`): one result per originally extracted claim; count contradicting/no-issue/insufficient/conflicting outcomes and unverified/failed/skipped claims without calling a judge model. `HasMaterialContradiction` is a *private shadow observation*, not permission to revise the speaker.
8. **Isolated local Ollama acceptance probe:** `tests/BusStop.V4.OllamaProbe` is intentionally outside the normal conversational application. It requires an explicit model and local private output path, refuses an unavailable or unpinnable Ollama digest, runs controlled source fact/unanchored biography/dialogue cases, and writes an owner-review-only JSON audit. It never contacts an external model or URL and never writes participant prompts/history.

## Invariants and limits

- No changes to `ConversationEngine`, `ParticipantContextBuilder`, `RecollectionService`, speaking-floor coordination, authoritatively committed speech, M1 shadow worker, historical M1 candidate schema, existing M1b corpus or participant inference priority.
- A valid V1 packet/citation is **technical provenance and authorization, not semantic proof**. V4 checks source kind/authority and rejects unsupported contradiction *classes*, but model interpretation of numerical disagreement, paraphrase, attribution and materiality still needs owner-reviewed semantic calibration at V6–V7. `NoMaterialIssue` never certifies truth.
- An unasserted rhetorical cue can be classified mechanically as no material factual issue without consuming Ollama. Unscoped or absent biography evidence becomes insufficient. No unvalidated positive claim may be generated from a failure.
- No new network research or participant-initiated Wikipedia work is included. Future governed, immutable source snapshots and per-recipient exposure events remain a separate approved extension path; new exposure cannot retroactively alter a prior utterance's verification boundary.
- The generic `IModelClient` interface exposes name and digest but **does not introspect configured temperature/think**. The isolated Ollama probe constructs its adapter with the exact pinned settings. Future V6 integration must enforce these fields at model-client factory creation and refuse unverified settings.
- Prompt byte bound (28,000 UTF-8 bytes) and generation/context bounds are safeguards, **not exact token accounting**. The original V1 packet ceilings still apply separately.
- No live worker starts by default. There is no durable V5 stage event/snapshot/report ledger or fenced recovery in this PR. Inference reservations are durable, including failure and crash. Full checkpoint/resume, idempotent final reporting and recovery policy belong to V5.

## Acceptance gate 1 — Model-free Windows GREEN

From a clean working tree:

```powershell
cd C:\Users\dvdht\source\repos\BusStop
git status --short
git fetch origin
git switch feature/moderator-v4-grounded-verification
git pull --ff-only origin feature/moderator-v4-grounded-verification
git rev-parse HEAD

dotnet build .\BusStop.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "Release build FAILED" }
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Regression tests FAILED" }
dotnet build .\tests\BusStop.V4.OllamaProbe\BusStop.V4.OllamaProbe.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Isolated probe build FAILED" }

Write-Host "V4 LOCAL MODEL-FREE GREEN" -ForegroundColor Green
```

New `VerifierV4Tests` cover source facts, approved identity anchors, personal-history non-promotion, dialogue attribution, rhetorical freedom, malformed responses, forged references, independent evidence, wrong boundaries, duplicate/reserved requests, V3 extraction charging, shared M1/V4 session ceilings and all retries. M0/M1/V1–V3 tests must remain GREEN.

## Acceptance gate 2 — Isolated pinned local Ollama

```powershell
dotnet run --project .\tests\BusStop.V4.OllamaProbe\BusStop.V4.OllamaProbe.csproj -c Release -- `
    --model qwen3.5:9b `
    --audit .\data\v4-private-ollama-audit.json
```

Use an installed local Ollama model tag, and **keep `data/v4-private-ollama-audit.json` private**. The probe does not claim a semantic result is correct just because the JSON is valid; inspect claim-level outcomes and citations. A technical RED is a V4 blocker, not a reason to relax host authority or rewrite an M1b corpus label. The model digest is recorded to make repeat acceptance comparable. You may share a redacted result summary instead of uploading source texts.

## Gate-2 first-run diagnostic addendum (2026-11-16)

Owner's local Windows build, model-free regressions, and standalone probe build all passed, but the first pinned `qwen3.5:9b` run was **V4 OLLAMA TECHNICAL RED**:

- `source-fact`: 0 verified, 1 failed, 2 charged verification attempts.
- `unanchored-autobiography`: 1 insufficient, no model call.
- `dialogue-attribution`: 1 contradiction and 1 insufficient, 1 model call.

The original V4 failure detail was too generic to identify whether the model produced malformed JSON, wrong claim/packet identity, invented citations, a failed transport, or an overlong response. That is an **observability deficiency**, not evidence that the strict host validation should be weakened.

The subsequent *diagnostic-only* PR update adds bounded, private `V4AttemptDiagnostic` records per charged model attempt: request ID, operational status, host-side rejection reason, Ollama done reason and token counts where available, output character count, and SHA-256 **but never raw model text**. The probe prints failure category/reason locally; the private audit keeps this metadata in `Report.AttemptDiagnostics`. Added tests cover malformed JSON, forbidden citations, and transport failures.

**Re-run both gates from the updated PR head.** If Gate 2 is still RED, capture the printed `source-fact: attempt ... reason=...` lines. Do **not** change the verifier's outcomes, evidence scope, retry ceiling, or charge accounting merely to force GREEN. Resolve the identified root cause in a separately reviewable update, then re-run. Owner semantic review remains required even after technical GREEN.

## Merge gate

**No merge, no V5 implementation and no claims of local/Ollama GREEN by the GitHub authoring environment.** Require owner-reported model-free GREEN plus isolated Ollama acceptance evidence, review any failures, then explicit PR merge authorization. If Ollama semantic performance is unsatisfactory, V4 stays isolated and reports the deficiency rather than granting enforcement.

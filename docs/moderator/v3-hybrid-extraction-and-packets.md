# BusStop V3 — Isolated hybrid claim extraction and frozen evidence packet assembly

**Status:** Draft implementation; owner-local GREEN required before any merge.  
**Parent:** PR #15 merged into `main` at `06d5cac8b7eca07c2475874676cd2f8eaa7306dd`.  
**Design baseline:** `BusStop_Evidence_Grounded_Verifier_V1-V7_Specification.md`, especially §§4, 7, 8, 10 and V3 of §14.

## Implemented

### Deterministic host-first claim cues (`V3ClaimDetector`)

- Detect explicit quantities, units, percentages, calendar dates and durations; self-referential biographical statements; quotations; attributed speech. Multiple and overlapping claims have their own indices and stable IDs derived from the same job and text spans.
- Preserve **verbatim UTF-16 start/length**, categories, provenance, rhetorical mode, and `HostValidated` shape status.
- Quoted numbers, jokes, questions and hypotheticals remain **candidates only**; the detector cannot assert contradiction, truth or binding biography.
- A deterministic exact-copy signal requires already authorized, previous committed dialogue. It costs zero model calls and is **not** a verification judgment.
- The host scans at most 12,000 UTF-16 characters and retains at most 24 distinct cue candidates. It explicitly reports incomplete selection if either cap is hit.
- Deterministic rule version: `claim-cues-v3-1`.

### Optional model-assisted extraction (`V3ModelClaimExtractor`)

- Uses existing `IModelClient` and `ModelRequest.JsonSchema`, **only through direct isolated invocation**. No runtime integration.
- JSON requires a `claims` array of at most 12 proposals, each containing `speakerId`, exact `start`, `length`, `text`, `category`, and `rhetoricalMode`. Unexpected/duplicate fields, foreign speaker, mismatched text, malformed JSON and unsupported enums are rejected before promotion to a host-validated candidate.
- Model-proposed new claims augment deterministic cues. Duplicate span/category proposals do not overwrite host cues. Host-obvious rhetorical context overrides an unsafe model suggestion that a question or joke is asserted fact.
- Strict pinned `ModelName`, `ModelDigest`, rule version, prompt version, and retrieval selector version.
- **At most one optional model attempt** per invocation, with no retries; attempted requests must receive a unique pre-dispatch authorization via `IV3ExtractionReservation`. No permit, no call. Transport/invalid JSON/budget/sampling/generation-limit results each retain explicit coverage.
- The V3 `IV3ExtractionReservation` interface ships **without a permissive implementation**. A V5 integrated worker must implement durable pre-dispatch reservation and session-wide attempt ceilings. The V3 tests use a one-shot in-memory permit *only within the isolated test process*.
- Model input is limited to 4,000 UTF-16 characters; request generation cap 1,024 tokens and context limit 4,096 tokens. These are provisional research settings and **not** a complete model-context/token-count proof. No Ollama access is necessary for the model-free regression tests.

### Frozen host-authorized packet assembly (`V3EvidencePacketAssembler`)

- For each host-validated candidate, read a **fresh V2 verifier scope** bound to actual persisted `Utterance`, job/session/speaker, the original committed boundary and the approved retrieval category.
- For an identity/history claim with no unambiguous first-person subject, do not assume the claim refers to the observer. Select the **Unknown** lane, not somebody else's private data.
- Only select records from the V2 `VerificationEvidenceScope`; never repair, extend, trim or rehash an excerpt to evade V1 authorization. The assembler may omit whole records, but must then mark `Partial`.
- Freeze V1-compatible canonical packet bodies and SHA-256 hashes; validate every packet with V1 `EvidenceVerificationValidator`.
- Provisional limits: 12 claims/job, 12 records/claim, 10,000 UTF-16 evidence characters/claim, 20,000 serialized UTF-8 bytes/packet and 80,000 bytes/job. Limits are validated and can be revised via an explicit versioned change. The V1 contract upper bounds remain independently enforced.
- Capped records or omitted claims are explicitly reported. `CompleteForDeclaredScope` means **selection of a declared evidence universe**, never proof of universal truth or semantic extraction completeness.
- No new network calls, Wikipedia adapter, participant source retrieval, speaker context updates, M1 policy changes, or live shadow jobs. The future participant-initiated Wikipedia path remains an additive source-snapshot plus per-recipient exposure extension, requiring separate approval.

## Tests and acceptance

`tests/BusStop.Tests/Moderation/VerifierV3ExtractionPacketTests.cs` covers multi-claim detection, stable offsets/IDs, questions, jokes, conditional and quoted figures, scan truncation, optional model augmentation, one-shot permit accounting, denied/sampled requests, invalid JSON/spans/speakers/categories, transport failure, packet hash stability, actual pinned-passage scope, byte and claim caps, exact-copy signaling, and ambiguous third-person evidence isolation.

Owner Windows acceptance from a clean worktree:

```powershell
git status --short
git fetch origin
git switch feature/moderator-v3-hybrid-extraction-packets
git pull --ff-only origin feature/moderator-v3-hybrid-extraction-packets
git rev-parse HEAD

dotnet build .\BusStop.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "Release build FAILED" }

dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Tests FAILED" }
Write-Host "V3 LOCAL GREEN" -ForegroundColor Green
```

**GREEN gate:** All previously merged M0/M1/V1/V2 tests still pass. New V3 tests pass. No transcript or participant behavior changes. The authoring environment does **not** have Windows/.NET/Ollama execution and does not claim GREEN.

**Merge gate:** Owner explicitly approves merge after reporting local GREEN. V4 is not started before the V3 gate.

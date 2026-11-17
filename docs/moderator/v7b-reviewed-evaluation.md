# BusStop V7b — Reviewed Semantic Scoring and Pinned Local Ollama Evaluation

**Status:** Draft, UNMERGED, owner-local build/test required. Parent is merged V7a PR #24 at `f60dc815242e0f43722167d9f76528d047dc3371`.

**Approved architecture:** `BusStop_Evidence_Grounded_Verifier_V1-V7_Specification.md`, evaluation §12 and V7 §14. V7b is **not** a final acceptance/release or enforcement gate.

## What V7b implements

V7b is a **manually invoked owner-local** evaluator. Four private JSON inputs (`development.json`, `holdout.json`, `criteria.json`, `approval.json`) MUST all pass `V7ReviewFreezeValidator.OpenApproved` (V7a exact-byte SHA-256, disjoint reviewed corpus, per-category and rhetorical sample requirements, matching human attestation and frozen thresholds). An invalid approval or missing file refuses the lab **before any Ollama model call**. No reviewed source examples, holdout, human labels, or thresholds were invented or committed in this PR.

The report separates:

- **V3 independent host extraction:** Apply the production `V3ClaimDetector.Detect` to each full reviewed `utteranceText`. Compare candidate `Start`/`Length` UTF-16 spans against the **exact** human-annotated `claimStartUtf16`/`claimLengthUtf16`. Count extra selections too: a correct detection with several spurious claims is **not** perfect precision. Measure category/rhetorical accuracy only for exact matched spans; protected `shouldExtractClaim=false` questions, jokes and opinions expose unwanted claim detection.
- **V4 oracle-claim component test:** For human-annotated `shouldExtractClaim=true` cases, feed the **owner-annotated exact claim span** to the existing production `V4GroundedVerifier.BuildPrompt` and `V4JudgmentParser.Parse` with a host-frozen, owner-supplied private evidence packet. A completed model response is required; malformed responses, wrong IDs/hashes, unauthorized citations, truncated generation, and transport errors are **technical failures, not clean verdicts**. Exactly one local call max per selected reviewed case, no retries.
- **Separate end-to-end recall:** A confirmed contradiction counts as found end-to-end ONLY when the owner-positive case was **exactly detected by V3**, with correct category/rhetoric, AND V4 returned the reviewed confirmed outcome. Oracle V4 correctness alone CANNOT inflate extraction or end-to-end recall. Missed, unobserved and failed cases remain in the reviewed-positive denominator.
- **All four outcomes and tiered evidence:** Separate `ConfirmedContradiction`, `NoMaterialIssue`, `InsufficientEvidence`, `ConflictingEvidence`; category and rhetorical-mode lanes, invalid shapes, actual authorized citation vs its *human-reviewed* support subset, uncertainty recall, and zero-denominator=null handling.
- **Identical-input repeat drift:** Stable lab session/opportunity/job/claim/packet IDs from the same corpus hash + case ID. Same-case outcome flips and extraction/status changes are reported separately; two repetitions are NOT twice the independent holdout cases.
- **Predeclared diagnostic comparisons:** Frozen numerical thresholds are compared per semantic lane and overall. Every metric is `MetInComponentLab`, `NotMet`, or `Unscorable`, never release-authorized. Crashes, queue drop behavior, participant regression and resource contention are marked **unscorable V7c-pending**.

**Critical scope distinction:** The V7b frozen evidence packet is built from human-supplied reviewed evidence, NOT from a live V2 retrieval or a real production SQLite session. It does not prove that V6c captured all claims, that source visibility is preserved under natural dialogue, that Ollama does not compete with participants, or that V5 restart handling is safe. Those require V7c. Reviews attest source authority; the code does not independently authenticate a reviewer or prove the holdout was unseen.

## Private versus aggregate output

The standalone `tests/BusStop.V7.ReviewedLab` project **only** runs with explicit CLI invocation and local loopback Ollama `127.0.0.1:11434`. The installed model name/digest must match the frozen `criteria.json` exactly (full 64-lowercase-hex digest), and the digest is checked before EACH repeat. No cloud fallback, background service, participant changes, or production V4/V5 reservations.

Run results are written to two disjoint folders using `FileMode.CreateNew`:

- **PRIVATE:** `<run>.private.json` (full reviewed utterance/evidence, host prompt, raw model answer, private diagnosis) and `<run>.score.private.json` (case IDs, reviewed ground truth, outcome audit and detailed drift). **Never upload these to GitHub or post them in chat.**
- **Aggregate-only:** `<run>.aggregate.safe.json` contains corpus/approval/criteria fingerprints, model pins, aggregate category/rhetoric/technical/semantic metrics and threshold diagnostics **without case IDs, reviewer identities, source excerpts, prompt text or raw model JSON**. Multi-run `*.drift.safe.json` contains only aggregate flip and status-change counts, not case identities.

Default output folders named `v7b-private` and `v7b-safe` are Git-ignored, but the owner is strongly advised to keep ALL V7 files outside the repo anyway. Avoid sharing aggregate holdout results publicly without first checking that small-cell counts are acceptable to disclose.

## Holdout discipline

An actual V7b holdout run requires `--partition Holdout` **and** the exact `--unlock-holdout i-authorize-first-holdout-evaluation`. A frozen review must already have been approved before any model evaluation. Running the holdout to guide prompt tuning violates independence; if you change the model, labels, prompts, schema or thresholds afterwards, the holdout must not be reused as unseen. Only owner-approved new independent examples can restore a clean holdout.

The standard V7b development CLI loads and hashes holdout JSON for freeze validation but never runs its cases or outputs its case-level contents. This is a *procedural control*, not a cryptographic access guarantee. No automatic holdout sampling or developer-selected labels is permitted.

## V7b owner-local GREEN build/test gate (NO reviewed private data required)

Run from a clean Windows PowerShell repository working tree:

```powershell
cd C:\Users\dvdht\source\repos\BusStop
git status --short
git fetch origin
git switch feature/moderator-v7b-reviewed-semantic-evaluation
git pull --ff-only origin feature/moderator-v7b-reviewed-semantic-evaluation
git rev-parse HEAD

dotnet build .\BusStop.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "V7b solution build FAILED" }

dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "V7b regression tests FAILED" }

dotnet build .\tests\BusStop.V7.ReviewedLab\BusStop.V7.ReviewedLab.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "V7b lab CLI build FAILED" }

dotnet build .\tests\BusStop.V7.FreezeCheck\BusStop.V7.FreezeCheck.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "V7a freeze CLI build FAILED" }

dotnet build .\tests\BusStop.V6.OllamaLab\BusStop.V6.OllamaLab.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "V6b lab build FAILED" }

Write-Host "V7b LOCAL GREEN (model-free)" -ForegroundColor Green
```

Tests generate **synthetic in-memory mock human approval** ONLY to test the V7a → V7b admission contract; no genuine owner label, benchmark or holdout is checked in. They cover strict frozen-input loading and immutability, oracle V4 parse, V3 exact-span extraction, over-extraction of protected rhetoric, missing-case/false-positive denominators, invalid response statuses, safe aggregate privacy, case-level repeat drift, and pinned model identity.

## Optional REAL local Ollama development run (only AFTER owner review + freeze)

Create and independently review the four private JSON documents using `docs/moderator/v7a-owner-review-freeze.md`. Start with `--partition Development` and **do not** include the holdout unlock phrase. The run makes up to eight local calls when all eight cases are eligible, repeated twice, but output can be fewer if V4 skips due to protected speech or invalid packet inputs.

```powershell
$private = Join-Path $env:USERPROFILE "BusStop-V7-Private"
dotnet run --project .\tests\BusStop.V7.ReviewedLab\BusStop.V7.ReviewedLab.csproj -c Release --no-build -- `
  --development (Join-Path $private "development.json") `
  --holdout (Join-Path $private "holdout.json") `
  --criteria (Join-Path $private "criteria.json") `
  --approval (Join-Path $private "approval.json") `
  --model qwen3.5:9b --partition Development `
  --private-dir (Join-Path $private "v7b-private") `
  --safe-dir (Join-Path $private "v7b-safe") `
  --runs 2 --cases 8

if ($LASTEXITCODE -ne 0) { throw "V7b local development probe FAILED" }
```

Use `--model` set to the EXACT model tag matching the frozen criteria. Real model outcomes and thresholds are **not yet available to ChatGPT**. Do not state V7b semantic acceptance from the tool exiting zero; zero means the private experiment ran, even when thresholds were missed or unscorable.

## Remaining V7c and separate approval

V7c must run controlled full/natural conversations, locate missed candidate claims, measure hallucinated biographies and misattributions, assess self-invention freedom and joke false positives, check context clipping, inference budgets and participant latency, run controlled crash/restart/ownership tests, and inspect qualitative transcript samples. Any unauthorized evidence, uncharged inference, transcript mutation or corrupted recovery is an automatic failure. Only after those gates can an owner grant **semantic acceptance**. No V7 output can enable enforcement automatically; that requires a separately designed and explicitly authorized phase.

**Do not merge on source inspection.** This draft has NOT been run with the owner's Windows .NET SDK or local Ollama and has NOT been semantic-accepted. Repair failures on this branch, run complete GREEN, request explicit merge approval.

# BusStop V6a — Provisional Synthetic Corpus and Deterministic Evaluation Scoring

**Status:** Draft implementation, not yet local GREEN, no merge authorization.
**Parent:** merged V5c PR #20, `9b0f82dc5c872425023efd896e4066f0779a85a8`.
**Design:** approved `BusStop_Evidence_Grounded_Verifier_V1-V7_Specification.md` §12 and V6 in §14.

## Position in the V6 implementation plan

- **V6a (this PR):** versioned fully synthetic provisional development cases, strict schema, data-only technical/semantic scoring and identical-input run drift.
- **V6b (separate future PR):** opt-in, manual local Ollama experiments/CLI, private audit outputs, owner-controlled label/holdout staging, and safe aggregate exports.
- **V6c (separate future PR):** separately opted-in shadow integration and resource-aware post-commit coverage/queue-drop telemetry, with the historical M1 route unchanged.
- **V7 (not authorized here):** owner-reviewed corpus/holdout, predeclared numeric thresholds, natural-session evaluation, multiple runs and release-readiness decision. No enforcement without separate approval.

## Frozen-at-evaluation input and provisional authority

`docs/moderator/corpus/v6a-synthetic-provisional-v1.json` contains 20 wholly fictional, explicitly **provisional-owner-review-required** development examples, spanning:

- Exposed source fact agreement, direct contradiction, absent evidence and conflicting snapshots
- Fictional character identity/binding anchors, self-invention freedom, and provisional biography
- Established, emerging and superseded personal history
- Prior witnessed dialogue, attributed speech, and different-speaker disagreement
- Humor, opinions, questions, quotes and conditional statements that should not trigger factual contradiction

The corpus includes typed evidence kind, authority tier, recipient visibility, invented reference IDs, claim rhetorical mode, expected outcome and provisional explanation. All four V4 epistemic outcomes are represented. This is **not** an owner-reviewed frozen corpus or unseen holdout; no threshold is set or evaluated against it.

`V6CorpusReader.LoadProvisionalSynthetic` rejects any label status except `provisional-owner-review-required` and any partition except `development`. It rejects missing, unknown or repeated JSON keys (including repeated labels), duplicated case IDs, malformed/unsupported enums, missing/unknown evidence IDs, illegal expected citations and incompatible corpus versions. It computes SHA-256 over the exact input bytes and deep-freezes all list backing stores. The SHA records input identity; it is not a signature of human review.

The existing `docs/moderator/corpus/m1b-challenges-v1.json` is untouched. Its historical original SHA-256 is `2c5bedecc0628486103a4fc3eb2b507282a93941d448c6abf0f65346c401cdf4`, and its legacy owner labels remain provisional. Repeated old 36-case runs are NOT 108 independent cases and must not be silently combined with V6 semantic results.

## Model-free scorer

`V6EvaluationScorer.Score(fixture, runId, observations)` reports, separately overall, by claim category, rhetorical mode and scenario group:

- Explicit observations, selected, not-selected, skipped, dropped, completed, operational failures, invalid responses, transport failures, incompatible checkpoints, budget skips and charged calls
- Four-outcome confusion matrix on **completed** cases (not erased by missing/dropped cases)
- Contradiction true/false positives, completed-only precision/recall and **end-to-end** recall with all provisional positive labels in the denominator
- False-positive rate on completed non-contradiction cases, outcome accuracy on completed cases
- Host-reference authorization versus **provisional citation relevance** (they are not the same)
- Duplicate/invalid contradiction span and malformed verdict-shape counters

Rates with zero denominators are null, not fake zero or 100%. Missing observations remain in total coverage/recall denominators. An `InsufficientEvidence`, `ConflictingEvidence`, transport failure, skipped budget or dropped case is not a clean `NoMaterialIssue`.

`Compare(firstRun, secondRun)` requires identical corpus SHA, case IDs and scorer version, then counts same-case flips only where both runs actually returned completed outcomes. Changes between completed and operational failures appear separately. Same-case repeats NEVER become additional independent test cases.

Reports contain case IDs, labels, metrics and safe status/reference-authorization flags, not original claim text, evidence excerpts, local paths, raw model JSON or private prompts. `AcceptanceAllowed` is always **false** for V6a.

### Important interpretation

The provisional rubric can itself be wrong. A result matching these labels is only a developer diagnostic. Authorized citation reference IDs show host scope, **not** semantic support; even an in-scope source may be irrelevant. The owner must examine labeled evidence, freeze reviewed reference sets and untouched holdout examples, and set category-specific thresholds **before** any V7 acceptance test. No scoring result can change participant behavior or enable enforcement.

## Technical and privacy boundaries

This PR only adds two isolated files under `src/BusStop.Moderation/Evaluation/`, a synthetic fixture under `docs/moderator/corpus/` and model-free xUnit tests. It does **not** modify `ConversationEngine`, `ConversationStore`, committed speech, the V4 Ollama adapter, M1 sampling, the participant context builder or memory. It performs zero model/network calls, does not create workers or register listeners, and does not write any participant-visible output.

## Owner-local V6a GREEN gate

Run in Windows PowerShell from a clean BusStop working tree:

```powershell
cd C:\Users\dvdht\source\repos\BusStop
git status --short
git fetch origin
git switch feature/moderator-v6a-evaluation-scoring
git pull --ff-only origin feature/moderator-v6a-evaluation-scoring
git rev-parse HEAD

dotnet build .\BusStop.sln -c Release
if ($LASTEXITCODE -ne 0) { throw 'V6a Release build FAILED' }
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'V6a regression tests FAILED' }
dotnet build .\tests\BusStop.V4.OllamaProbe\BusStop.V4.OllamaProbe.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'V4 isolated probe build FAILED' }
Write-Host 'V6a LOCAL GREEN' -ForegroundColor Green
```

The V6a test class `VerifierV6aEvaluationTests` exercises the 20-case fixture, all four outcomes, source/identity/history/dialogue and rhetoric coverage, provisional-only restrictions, deep-frozen lists, duplicate/missing/extra keys, real-case accounting of selected/dropped/skipped outcomes, benign-joke false positives, irrelevant or forged references, invalid verdict shapes, technical statuses, missing-case preservation and identical-case flips.

**Do not merge on source inspection.** The GitHub-only authoring environment has not run the owner's Windows .NET tests. Fix any failures on this feature branch, repeat the full gate and wait for explicit merge approval. V6b and V6c are independent future gates; no V7 acceptance or enforcement is implied.
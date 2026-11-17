# BusStop V7a — Owner Review and Preregistration Freeze Gate

**Status:** Draft PR, model-free; no owner-review corpus or acceptance criteria have been supplied, reviewed, signed or frozen. **Parent:** merged V6c PR #23, `8e256d73805f418c1ad98fb3212f2d6096087774`.

**Architectural basis:** approved *BusStop Evidence-Grounded Verifier V1–V7 Specification* §12 and §14 V7. This is the **first** V7 stage, not V7 release acceptance and not an enforcement design.

## Sequence

- **V7a (this PR):** Strict, local-only reviewed-case schema; independent development and unseen holdout partitions; exact-file hash commitments; owner-entered categorical, uncertainty, extraction, resource and natural-conversation criteria; explicit self-attested owner approval bound to exact bytes. No model calls and no release pass/fail.
- **V7b (future separate PR after V7a local GREEN + merge approval):** Owner-reviewed scorer and pinned local Ollama comparisons, all four epistemic outcomes, category/rhetoric lanes, case-level drift and protected dialogue quality. It must refuse evaluation on an unfrozen or mismatched manifest. No threshold changes after holdout scores.
- **V7c (future separate PR):** Controlled new natural dialogue runs, owner review of omitted claims, self-invention, joke false positives, participant-naturalness and resource/latency contention; crash/resume fault matrix and final deficiency report. No enforcement.
- **No auto-promotion:** Even an excellent V7c report cannot change moderator modes or provide prompts to participants without separately approved and tested enforcement architecture.

## Review workflow (owner action required)

1. **Human-label DEV independently.** Inspect a substantial set of genuine source contradictions, tiered fiction-persona anchors, provisional/established/superseded memories, dialogue attribution, mixed claims, noncontradictions, insufficient evidence, conflicting evidence, and protected rhetoric. Record the actual claim text, its legal evidence and exposure scope, reviewed outcome, exact supporting/opposing reference IDs, evidence-grounded rationale, reviewer alias, and UTC review time. V6a's 20 developer synthetic examples may be used as **DEV candidates only**, but their provisional expected labels and rationales are NOT approved ground truth. Do not auto-copy them into reviewed labels.
2. **Build a truly independent HOLDOUT.** Source, independently author and review additional cases **not seen by the model developer or used to tune prompts or schema**. Keep the file private and unopened during subsequent prompt/schema tuning. The freeze checker detects exact same-ID and same-claim-and-evidence duplicates across partitions; paraphrased near-duplicates still require human judgment.
3. **Prerecord numerical thresholds**, as actual numeric rates chosen by the owner **before** viewing holdout model results. Both partitions must cover all four outcomes. Every semantic category (SourceFact, CharacterIdentity, PersonalHistory, DialogueAttribution, Other) and protected-nonasserted rhetoric gets independent requirements. Four material factual categories need positive contradiction examples and explicit precision + end-to-end recall minima. For a lane with no positive class, explicitly explain why precision/recall are not applicable; it still requires a meaningful false-positive ceiling. Set owner-reasoned sample sizes and confidence/uncertainty criteria, not tiny convenient numbers. Tests contain illustrative values ONLY.
4. **Run LOCAL `--prepare`**, which checks the documents and displays their SHA-256 hashes and counts, but cannot approve anything. The input JSON must be strict, bounded, BOM-free UTF-8.
5. **Record a separate owner approval** with the exact hashes emitted by preparation and the prescribed declaration. This is an **unverified human attestation**; the code cannot prove who typed it or that the holdout was actually unseen. It is not a digital signature.
6. **Run LOCAL `--validate`**. It rejects wrong bytes, reused holdout cases, incomplete lanes, weakened hard gates, mismatched prompts/models or reviews allegedly completed after approval. It returns a safe receipt with only aggregate counts/hashes. No acceptance verdict is possible at V7a.

**Do not commit or upload reviewed corpora, the holdout, private snapshots, raw transcripts, detailed labels, or owner approval files.** All authoring and validation should occur on the owner's machine outside the Git checkout. Do not share private case-level JSON files in the chat. The safe receipt hashes and aggregate counts are acceptable.

## Required private file contracts

`development.json` and `holdout.json` each have this exact top-level shape; only the partition differs:

```json
{
  "version": "busstop-v7a-review-v1",
  "labelStatus": "owner-reviewed-attested",
  "partition": "Development",
  "reviewMethod": "independent-human-adjudication",
  "sourceDeclaration": "Owner-authored provenance and independence statement",
  "cases": []
}
```

The empty `cases` array in this **illustrative schema** is *intentionally invalid* until reviewed cases are supplied. Case fields are exactly:

`caseId`, `scenarioGroup`, `category`, `rhetoricalMode`, `utteranceText`, `claimStartUtf16`, `claimLengthUtf16`, `shouldExtractClaim`, `claimText`, `evidence`, `reviewedOutcome`, `supportingReferences`, `opposingReferences`, `reviewRationale`, `reviewerId`, `reviewedAtUtc`.

The reviewed case fields `utteranceText`, `claimStartUtf16`, and `claimLengthUtf16` identify the full observed speech and the exact UTF-16 claim span (start 0-based; length in .NET UTF-16 code units). The validator requires the substring to equal `claimText` byte-for-byte after JSON decoding, so claim-extraction precision and recall can later be evaluated. `shouldExtractClaim` is an explicit human label: `false` examples represent questions, jokes and opinions where extracting a material factual claim would be inappropriate. They remain in extraction and false-positive denominators; a negative extraction case must carry a `NoMaterialIssue` review label and no expected citations.

Each evidence item has exactly `referenceId`, `kind`, `authorityTier`, `visibility`, `excerpt`, `isSuperseded`. Namespaced V3 reference IDs such as `briefing:<id>` or `utterance:<session>:<id>` are allowed. Reviewed outcomes are `ConfirmedContradiction`, `NoMaterialIssue`, `InsufficientEvidence`, and `ConflictingEvidence`. Reviewer IDs are local pseudonyms (lowercase ASCII letters, digits and hyphens). UTC timestamps use `Z` or `+00:00`. A reference's presence and scope is not proof its interpretation is correct; human review is required.

`criteria.json` has these exact fields: `version` = `busstop-v7a-thresholds-v1`, `modelName`, `modelDigest` (full lowercase 64-hex SHA-256), `verifierPromptVersion` = current V4 constant, `verificationSchemaVersion` = current V4 constant, `scorerVersion` = `busstop-v7b-reviewed-scoring-v1` (reserved for a later, distinct reviewed-data scorer), `minimumIdenticalInputRuns` (at least 2), `minimumNaturalSessions` (at least 1), `minimumCrashScenarios` (at least 1), `lanes`, `hardGates`, `humanQualityReviewProcedure`, and `thresholdRationale`.

In addition the criteria contain these owner-chosen metrics (each rate decimal 0..1, P95 ms positive integer):

- `minimumClaimSpanPrecision`, `minimumClaimSpanRecall`, `minimumRhetoricalModeAccuracy`
- `minimumInsufficientEvidenceRecall`, `minimumConflictingEvidenceRecall`
- `maximumUnscorableRate`, `maximumQueueDropRate`, `maximumTechnicalFailureRate`
- `maximumVerifierP95LatencyMilliseconds`, `maximumParticipantLatencyRegressionFraction`

Each `lanes` entry has EXACTLY:

`lane`, `minimumIndependentCases`, `minimumCompletedCases`, `minimumConfirmedPositiveCases`, `minimumSelectionCoverage`, `maximumConfirmedFalsePositiveRate`, `minimumConfirmedPrecision`, `minimumEndToEndConfirmedRecall`, `minimumEvidenceSupportRate`, `notApplicableReason`.

Six lanes MUST be present, **exactly one** of each:

`category:SourceFact`, `category:CharacterIdentity`, `category:PersonalHistory`, `category:DialogueAttribution`, `category:Other`, `rhetoric:ProtectedNonAsserted`.

Four fact/identity/history/dialogue lanes require at least one positive and non-null thresholds for both precision and recall; owner selects **larger** minimum samples for a meaningful real evaluation. Protected rhetoric requires zero contradiction-positive examples and an explicit N/A rationale, preserving freedom for humor, quotes, questions and opinions.

All `hardGates` properties are **required to be true**, with these exact names: `noUnauthorizedEvidence`, `noUnchargedModelAttempts`, `noSpeechOrParticipantContextMutation`, `noCorruptRecoveryPromotion`, `noPrivateHoldoutLeakage`, `noNonlocalVerifierInference`, `noEnforcement`. No aggregate quality average can compensate for a failed hard gate.

`approval.json` has exactly: `version` = `busstop-v7a-freeze-approval-v1`, `ownerId`, `approvedAtUtc`, `developmentSha256`, `holdoutSha256`, `criteriaSha256`, `declaration`. The prescribed declaration is precisely:

> I reviewed the labels and evidence, kept the holdout unseen during model tuning, and approved these criteria before observing holdout model results.

The exact wording and hash checks **do not authenticate the owner**. A real owner review is still a human process; an LLM can trivially fabricate these strings. Do not represent a successful validation as cryptographic signature, review authenticity, semantic acceptance or release authorization.

## Windows build + tests: V7a model-free gate

```powershell
cd C:\Users\dvdht\source\repos\BusStop
git status --short
git fetch origin
git switch feature/moderator-v7a-review-freeze-contracts
git pull --ff-only origin feature/moderator-v7a-review-freeze-contracts
git rev-parse HEAD

dotnet build .\BusStop.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "V7a build FAILED" }
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "V7a regression tests FAILED" }
dotnet build .\tests\BusStop.V7.FreezeCheck\BusStop.V7.FreezeCheck.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "V7a freeze CLI build FAILED" }
dotnet build .\tests\BusStop.V6.OllamaLab\BusStop.V6.OllamaLab.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "V6b lab build FAILED" }

Write-Host "V7a LOCAL GREEN" -ForegroundColor Green
```

`VerifierV7aReviewFreezeTests` is **entirely model-free** and creates **synthetic in-memory** review/holdout examples: no real owner labels or holdout are committed. Tests cover all four outcomes, separate categories, missing positive/unknown/conflicting class, duplicate IDs/content, changed raw bytes, post-approval threshold tampering, unreviewed labels, invented evidence refs, owner attestation mismatch, pins, future approvals, technical/human thresholds, and safe receipt non-acceptance.

## Optional local-only PRIVATE review check (after actual human review)

```powershell
$private = Join-Path $env:USERPROFILE 'BusStop-V7-Private'
dotnet run --project .\tests\BusStop.V7.FreezeCheck\BusStop.V7.FreezeCheck.csproj -c Release --no-build -- --prepare --development (Join-Path $private 'development.json') --holdout (Join-Path $private 'holdout.json') --criteria (Join-Path $private 'criteria.json')

# Only after owner review, approval file creation, and explicit threshold freeze:
dotnet run --project .\tests\BusStop.V7.FreezeCheck\BusStop.V7.FreezeCheck.csproj -c Release --no-build -- --validate --development (Join-Path $private 'development.json') --holdout (Join-Path $private 'holdout.json') --criteria (Join-Path $private 'criteria.json') --approval (Join-Path $private 'approval.json')
```

The CLI performs ZERO Ollama calls and no DB writes. Prepare outputs `HumanApprovalReceived=false`; validate outputs `SemanticAcceptanceGranted=false` and `EnforcementAuthorized=false`. The tool prints safe hashes/counts only and refuses BOM or invalid UTF-8.

## Deferred gates and limitations

- V7a does not yet have any real owner-reviewed private dev corpus or unseen holdout. **That work must be performed and confirmed by a human**, not invented by ChatGPT. No actual thresholds have been chosen by the owner.
- Manual review must inspect semantic **near duplicates**, subtle authority tier/conflicting evidence, and paraphrased stories: SHA-256 duplicate checks cannot prove independence or factual correctness.
- V7b must implement its **separate** reviewed scorer and later use the exact frozen file bytes + pins; V6a synthetic provisional score cannot be reused as final truth.
- V7c must document missed claim selection, participant timidity, protected rhetoric, naturalness, context clipping, queue drops, inference overlap, and crash-fault coverage; all integrity gates remain mandatory.
- Passing V7a model-free tests means **review-freeze protocol code** works; it does not mean the review or release-readiness gate has passed.

**Merge discipline:** draft-only PR, independent Windows GREEN, explicit owner approval before merge. No enforcement and no V7b implementation in this PR.

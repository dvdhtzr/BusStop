# BusStop V7d-1 through V7d-3: conversational progress evaluation

Status: implementation proposed on a new BusStop-only branch; owner-local verification and approval are pending. No live changes or semantic acceptance.

Parent main: 64cdfa12dde8e588e57d150a7dc0b92b1203bf10 (merged V7c PR #26).

## Goal and boundaries

Measure whether three distinct people can sustain natural, developing dialogue or conclude gracefully, without lexical echo, meaning-free repetition, unsupported shared histories, fabricated source facts or excessive timidity. Disagreement, deliberate quotation, emphasis, personal anecdotes and silence are allowed; novelty is not mandatory.

These stages are **offline and observational only**. ConversationEngine, FloorCoordinator, ParticipantContextBuilder, moderation, verification inference, and SQLite schema are unchanged. No Ollama calls, no default behavior change, no new simulation runs, no cleanup and no non-BusStop files.

## V7d-1: frozen development baseline and rubric

Known V7c 35-turn transcript canonical SHA256:
ee7d59bf51b05c900a03293dea41ef7e1082c5b2b9d1fa04def0de8671e3c2d5

This is development evidence, NOT an unseen holdout. It is tied to the authoritative SQLite transcript and private V6c coverage, the factory story and Arthur/Maya/Daniel profiles in src/BusStop.Console/Laboratory.cs, the original V7c private human worksheet and the existing V7c audit. Retain the original files together locally; the transcript, source excerpts, reviewer identities/notes, real dialogues, and original model digests are NOT committed.

V7d review labels per turn: UnproductiveLoop, HealthyDisagreementOrCallback, MeaningfulNewMove, InventedSharedHistory, optional Notes. ReviewerAlias, ReviewedAtUtc (UTC), and an exact attestation are required. Sequence and TextSha256 are sealed bindings to the source turn. The PRIVATE template also contains tamper-checked analyzer outputs for each turn (ExactCopyCandidate with source sequence, StrictLexicalOverlapCandidate, BroadLexicalEchoCandidate, LoopWindowCandidate), so the owner can inspect EXACTLY which claims prompted review beside the existing V7c transcript. The candidate flags are not human labels or findings; review labels start null and must be completed manually.

Known investigation examples, NOT auto-approved labels:
- turns 20-21: exactly repeated text across characters; deterministic duplicate candidate
- turns 22-35: many iterations of cheap hands, training, lines and bills; human review determines which turns genuinely stagnate
- turn 12: an unsupported apparent shared memory about Maya's father and Arthur; history-boundary review, not a lexical finding
- turns 1-10: plausible genuine disagreement controls, which must not be automatically marked as stagnation

No development labels, transcript text, or reviewer notes are checked into GitHub.

## V7d-2: deterministic, bounded, read-only diagnostic

V7dProgressAnalyzer accepts ONLY a previously validated, bounded transcript:
- normalize Unicode letter/number tokens and compare normalized full-text equality with earlier turns
- strict lexical-overlap candidate uses word-set Jaccard >=0.72 over the preceding six turns, requiring at least eight input tokens and five distinct words
- exploratory broad-echo review candidate uses the same bounded comparison at Jaccard >=0.25, only where neither full-copy nor strict overlap already fired; this is deliberately recall-oriented and false-positive-prone, NOT an evidence-backed finding
- candidate six-turn loop-pressure window requires at least three exact, strict overlap or exploratory broad-echo signals
- these are REVIEW CANDIDATES, not proof of semantic stagnation
- turn-level signals contain private sequences, hashed text, prior references and similarity; safe aggregate contains only counts and the transcript fingerprint, never actual speech or speaker identity
- maxima: 2,000 turns, 20,000 characters per utterance, bounded regex execution

Uncertainty: intentional quotes/callbacks can trigger false positives; paraphrased loops can evade token overlap; incidental shared words are not agreement or falsehood. No semantic classifier is claimed at this stage.

The standalone tests/BusStop.V7.ProgressLab CLI first reuses the production V7c audit in STRICT SQLite read-only mode. The V7c session must have three participants, exact unmodified transcript hashes, consistent queues and the validated immutable V5a job ledger. Modes:
- analyze: prints safe aggregates to PowerShell; creates no files or folders
- template: only with explicit --output file in an EXISTING directory; writes one private worksheet with create-new semantics, does not overwrite
- calibrate: accepts explicit --review file; prints safe development calibration and does not write anything

No holdout loading, web access, Ollama or conversation writes. The original transcript is read for analysis but never written as a new V7d artifact. Use the existing private V7c worksheet beside the V7d template when annotating.

## V7d-3: development calibration and gates

Only fully labeled and manually attested development worksheets are accepted. Human UnproductiveLoop labels are compared with combined exact-copy / lexical-overlap / broad-echo / loop-pressure candidate signals. Report TP/FP/FN/TN and optional precision, recall, and healthy-disagreement/callback false-alarm rate. Undefined denominators are NULL. Contradictory or tampered turn labels fail closed.

Preliminary minimum interpretable development sample: 20 reviewed positive turns, 20 negative turns, and 10 healthy-disagreement/callback controls. The 35-turn baseline ALONE cannot meet both 20/20 minimums; an insufficient-case result is expected, not an implementation failure. Additional independently sourced development cases require later authorization.

Proposed but NOT frozen final targets: precision >=80%, recall >=70%, healthy-negative false-alarm rate <=10%. These are recommendations, not retrospectively accepted benchmarks. Freeze definitions, corpus split, hashes, thresholds, model version if an optional semantic evaluator is introduced, and uncertainty policy with the owner BEFORE any unseen holdout. Self-attestation does not independently authenticate human review.

Every calibration report retains these flags as FALSE: IndependentReviewAuthenticated, HoldoutEvaluated, ThresholdsFrozen, SemanticAccuracyAccepted, BehavioralInterventionAuthorized. No mechanical or developmental pass opens V7 release authority.

## Technical owner-local gate

From BusStop repo on feature/v7d-conversation-progress-evaluation:

    dotnet build .\BusStop.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw "Solution build FAILED" }
    dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "BusStop tests FAILED" }
    dotnet build .\tests\BusStop.V7.ProgressLab\BusStop.V7.ProgressLab.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "V7d lab build FAILED" }

Reuse the ORIGINAL previously identified run with $run.Db, $run.Session and $run.Coverage. If missing, do not create a replacement experiment merely to green the gate. Read-only aggregate diagnostic:

    dotnet run --project .\tests\BusStop.V7.ProgressLab\BusStop.V7.ProgressLab.csproj -c Release --no-build -- --db $run.Db --session $run.Session --coverage $run.Coverage --mode analyze

Known exact-copy cue: local private review should reveal a candidate at turn 21, referring to turn 20. This real-baseline check is required but is NOT hardcoded in public unit tests. The first owner-local V7d run returned ExactCopyCandidates=1 but strict LexicalOverlapCandidates=0 and TurnsInsideCandidateLoopWindows=0, despite obvious late conversational stagnation. This motivated a distinct low-threshold broad-echo candidate family, not a semantic verdict. On the revised build, expected broad echoes and some loop-pressure candidates should be observed and manually inspected for false positives; no particular number is hardcoded as success.

OPTIONAL private worksheet, only if owner deliberately wants one file in the ALREADY EXISTING natural-run directory:

    $privateReview = Join-Path (Split-Path $run.Db -Parent) "v7d-development-review.private.json"
    dotnet run --project .\tests\BusStop.V7.ProgressLab\BusStop.V7.ProgressLab.csproj -c Release --no-build -- --db $run.Db --session $run.Session --coverage $run.Coverage --mode template --output $privateReview

This cannot overwrite. Fill labels and attestation without altering TextSha256 or Sequence. The private V7c original worksheet is the human reading companion. Then aggregate calibration with no output file:

    dotnet run --project .\tests\BusStop.V7.ProgressLab\BusStop.V7.ProgressLab.csproj -c Release --no-build -- --db $run.Db --session $run.Session --coverage $run.Coverage --mode calibrate --review $privateReview

Do not upload/commit private human notes, transcript, private JSON or local SQLite. No new artifact root is created. No cleanup is performed.

## Acceptance and next approvals

Gate A: owner-local build and model-free regression GREEN, existing transcript digest unchanged, no data leak in safe report, known duplicate detected, fail-closed invalid/tampered reviews, no behavioral regression.

Gate B: human-confirmed development labels, false positive/negative review, sufficient denominators or explicit INCONCLUSIVE, never semantic release acceptance. Synthetic tests validate mechanics, not judgment quality.

Gate C (changed participant contexts, prompts, live nudges, scheduling, extra model calls, post-commit revisions, automated topic changes) is specifically NOT approved. Any optional semantic Ollama evaluation, additional natural runs or context A/B trials require a separately approved stage.

Draft PR only. No merge until owner-local GREEN and explicit owner merge approval. Describe any approved merge as evaluation infrastructure, not V7 semantic success.

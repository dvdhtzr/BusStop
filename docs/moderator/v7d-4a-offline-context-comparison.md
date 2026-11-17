# BusStop V7d-4a — Offline, model-free conversational context comparison

Status: approved V7d-4a implementation; owner-local GREEN and PR review pending.
Base: merged BusStop PR #27, main e57154fb055c3d9e8222c4d829b96ca6066da674.
Scope: BusStop ONLY. V7d-4b/4c/4d remain proposed, NOT approved for implementation.

## Hypothesis and experimental strategies

We will test whether selective context reduces repeated argument feedback without losing essential dialogue context. This first implementation compares pre-turn visibility ONLY; it does not generate alternative dialogue.

A = LegacyRecent20: original 20 most recent committed prior utterances.
B = Recent8: eight most recent committed prior utterances.
C = Selective6Plus2: six latest committed prior utterances, plus up to two exact earlier committed utterances. If the current observed utterance replies to an older committed utterance not in the latest six, reserve the first anchor for that target. Elective anchors come from fourteen turns preceding the latest six: prefer underrepresented speakers, speaker diversity, topical lexical resemblance below near-duplicate levels, then recency. No generated summaries, fabricated canon, other characters' private identities or uncommitted speech. A reply target may be older than the original twenty-turn context, but remains bounded by two anchor slots and is recorded in a private trace.

These selectors are isolated in the Context project and NOT wired to live ConversationEngine, FloorCoordinator, participant prompts, model clients or moderators. A synthetic parity test uses the existing, unchanged ParticipantContextBuilder to confirm that A renders the same prompt as the original 20-turn builder, when all other context settings match. Historical prompts cannot always be reconstructed byte-for-byte from committed SQLite alone: the source settings may include the optional C2 guidance and remembered context.

## Data integrity and privacy

- Source: only previously committed speech from the existing V7c SQLite, guarded by the original V7c read-only transcript + immutable-ledger audit and the known 35-turn DEVELOPMENT fingerprint ee7d59bf51b05c900a03293dea41ef7e1082c5b2b9d1fa04def0de8671e3c2d5.
- The offline selector rejects disordered or cross-session history and any unknown/future reply reference.
- A selection digest binds sequence, original utterance ID and SHA-256 of exact speech. The private trace digest binds the source fingerprint, turn speaker and reply reference, selected sequences and hashes, anchor reasons, and selection counts. These are reproducibility checks, not independent authentication.
- Private per-turn trace: speaker identifiers, selected sequence indices, reply target, anchor reasons and selected hashes; never place in a public/safe report. The private trace contains NO original dialogue text.
- Safe aggregate stdout only: compared committed-turn contexts, selected utterance counts and character counts, anchor totals, known reply omissions, three-speaker coverage. It contains NO speech, speaker identifiers or per-turn indices.
- Limit: the comparison evaluates contexts before COMMITTED turns only; it does not reconstruct each offered, passed or failed opportunity. This is explicitly not complete floor-opportunity parity. Source context may also include additional historical recollection settings; A parity is established in synthetic tests for equal builder inputs.
- Character counts are not estimated full prompt tokens, model latency or dialogue quality. No Ollama calls, new conversation runs, human semantic acceptance, holdout opening, or behavioral authorization.

## V7d-4a technical gate

1. Release solution build GREEN, complete BusStop xUnit GREEN, standalone context comparison lab Release build GREEN.
2. Synthetic regression tests verify A prompt parity, B recent-eight subset, C recent-six plus up to two anchors, directed old-reply preservation, deterministic hashes, prompt attribution, rejection of cross-session/future lines, safe aggregate separation, and tampering refusal.
3. Existing V7c read-only audit passes before the first comparison; original DB, coverage and transcript hash unchanged.
4. No automatically created output folders. Optional --private-report writes exactly one PRIVATE file via CreateNew in an already existing private folder outside the BusStop repository, never overwriting.
5. Owner reviews any unexpected selection choices and explicitly approves PR merge. Passing mechanical tests does NOT prove better conversational quality.

V7d-4b: controlled Ollama A/B/C runs. V7d-4c: blinded human quality comparison. V7d-4d: promotion decision. All three require a separate authorization after V7d-4a is accepted.

## Owner-local verification

From C:\Users\dvdht\source\repos\BusStop on the feature branch:

    dotnet build .\BusStop.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw "Solution FAILED" }

    dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests FAILED" }

    dotnet build .\tests\BusStop.V7.ContextComparisonLab\BusStop.V7.ContextComparisonLab.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "V7d-4a lab FAILED" }

Reuse the original V7c session ONLY:

    $folder = Join-Path $env:TEMP "BusStop-V7c-Natural-20261117-100137"
    $db = Join-Path $folder "session.sqlite"
    $cov = Get-ChildItem (Join-Path $folder "v6c-private") -Filter "v6c-coverage-*.private.json" | Select-Object -First 1
    if (!(Test-Path -LiteralPath $db) -or $null -eq $cov) { throw "Original V7c evidence missing" }
    $e = Get-Content -LiteralPath $cov.FullName -Raw | ConvertFrom-Json
    if ($e.timing.TranscriptShaAfterVerifier -ne "ee7d59bf51b05c900a03293dea41ef7e1082c5b2b9d1fa04def0de8671e3c2d5") { throw "Wrong V7c run" }

    dotnet run --project .\tests\BusStop.V7.ContextComparisonLab\BusStop.V7.ContextComparisonLab.csproj -c Release --no-build -- --db $db --session ([string]$e.session) --coverage $cov.FullName
    if ($LASTEXITCODE -ne 0) { throw "V7d-4a context comparison FAILED" }

For OPTIONAL private per-turn inspection, repeat the last dotnet command adding:

    --private-report (Join-Path $folder "v7d4a-context-selection.private.json")

A second invocation with the same file refuses overwrite. Do not commit/upload local private traces. Do not run additional Ollama sessions to complete this stage.

## Interpretation gate

Review whether C actually retains a useful previous voice, whether mandated reply-reference anchors dominate selection, and whether it inadvertently retrieves stale context. Never declare naturalness, repetition reduction or semantic quality from visibility totals alone.

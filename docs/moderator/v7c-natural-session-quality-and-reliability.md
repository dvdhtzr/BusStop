# BusStop V7c — Natural Conversation and Reliability Observation

**Status:** Draft V7c observation stage; not locally compiled; not final acceptance. Based on merged V7b PR #25 at f5afad7850ead98ae7334e6249711dfa64c39b26.

## Purpose and boundaries

Test the original goal: three distinct fictional people having plausible, free-flowing conversations without unsupported autobiographical histories, misplaced dialogue attributions, artificial timidity, or excessive repetition. V7c can collect operational and self-attested human observations, but it cannot certify semantic acceptance, change a participant prompt, or enable enforcement.

V6c remains opt-in and deferred until the conversation and any original M1 drain have finished. The only live-console addition records coarse phase durations and authoritative committed-transcript hashes directly before and after the deferred verifier drain. There are NO additional per-turn model calls, moderator decisions, queue scheduling changes, or participant-context mutations.

## Read-only natural-session auditor

The manual tests/BusStop.V7.NaturalAudit executable reads an existing SQLite file using SqliteOpenMode.ReadOnly; it does not initialize schema, perform retrieval, dispatch models, or invoke V5c recovery. It checks that the session has exactly three participant IDs, that the committed sequence and earlier reply references are valid, and that V6c's private coverage counts are internally coherent. It enumerates real V6c V5a jobs and checks each checkpoint with the production hash-chain validator, refusing corrupt or foreign jobs and reporting nonterminal/incomplete jobs separately.

The console's original private v6c-coverage report now contains timing and pre/post verifier transcript hashes. The auditor compares both against a fresh canonical hash of the committed SQLite transcript. A mismatch or missing instrumentation is never called a pass. In-memory queue loss before durable registration remains an UNKNOWN interval after a process crash.

The audit counts only HOST cue candidates for autobiography and dialogue attribution. Such cues are neither proof of fabrications nor independent labels. Whole-utterance repetitions and per-speaker turn totals are prompts for manual inspection, NOT semantic scores.

Outputs are separated: PRIVATE per-turn audit and blank human review worksheet, versus aggregate-only numeric diagnostics. Run outside the repository even though v7c-private/ and v7c-safe/ are gitignored. The executable never writes to the SQLite database.

## Human quality review

The worksheet contains the full exact transcript and NULL labels for invented personal history, dialogue misattribution, unsupported certainty, unnatural repetition, timid/formulaic speech, and healthy character development on EVERY turn. Six session-wide ratings (1–5) cover naturalness, personality distinctness, humor, constructive disagreement, history coherence, and moderator nonintrusion. A human must complete all labels, reviewer alias, UTC reviewed time and truthful attestation. Optional --review checks the exact transcript hash and every turn before producing aggregate-only human counts. The attestation is NOT cryptographically authenticated, and cannot award semantic acceptance.

## Windows model-free GREEN gate

~~~powershell
cd C:\Users\dvdht\source\repos\BusStop
git status --short
git fetch origin
git switch feature/moderator-v7c-natural-session-reliability-lab
git pull --ff-only origin feature/moderator-v7c-natural-session-reliability-lab
git rev-parse HEAD

dotnet build .\BusStop.sln -c Release
if ($LASTEXITCODE -ne 0) { throw 'V7c solution build FAILED' }
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'V7c regression tests FAILED' }
dotnet build .\tests\BusStop.V7.NaturalAudit\BusStop.V7.NaturalAudit.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'V7c natural audit CLI build FAILED' }
dotnet build .\tests\BusStop.V7.ReviewedLab\BusStop.V7.ReviewedLab.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'V7b lab build FAILED' }
dotnet build .\tests\BusStop.V7.FreezeCheck\BusStop.V7.FreezeCheck.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'V7a freeze CLI build FAILED' }
Write-Host 'V7c MODEL-FREE LOCAL GREEN' -ForegroundColor Green
~~~

The 16 VerifierV7cNaturalSessionAuditTests run real disposable SQLite with three personas and a scripted local model (no Ollama). They test ledger/transcript consistency, read-only SQLite, queue overflow, transcript hash mismatches, an immutable V5a trigger that correctly rejects direct modification, and a separate offline-corruption scenario in which the update trigger is removed **only in a disposable test database** so the hash-chain validator must reject modified bytes. Also covered: nonterminal registration requiring explicit V5c recovery, private-versus-safe exports, and blank/complete human review. No production trigger or schema is weakened.

## Owner-local NATURAL Ollama experiment (additional gate, not default tests)

Run a long genuine session with three participants using the existing console, with shadow verification enabled and enough deferred queue capacity. Use an external private folder:

~~~powershell
$out = Join-Path $env:TEMP ('BusStop-V7c-Natural-' + (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Force -Path $out | Out-Null
$db = Join-Path $out 'session.sqlite'

dotnet run --project .\src\BusStop.Console\BusStop.Console.csproj -c Release -- `
  --model ollama --ollama-model qwen3.5:9b --no-think `
  --max-opportunities 60 --max-model-calls 120 `
  --verifier-shadow --verifier-model qwen3.5:9b `
  --verifier-budget 32 --verifier-sample-every 2 `
  --verifier-queue-capacity 32 --out $out --db $db
if ($LASTEXITCODE -ne 0) { throw 'Natural conversation FAILED' }

$cov = Get-ChildItem (Join-Path $out 'v6c-private') -Filter 'v6c-coverage-*.private.json' |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $cov) { throw 'V6c coverage missing' }
$sessionId = [string](Get-Content -Raw $cov.FullName | ConvertFrom-Json).session

dotnet run --project .\tests\BusStop.V7.NaturalAudit\BusStop.V7.NaturalAudit.csproj -c Release --no-build -- `
  --db $db --session $sessionId --coverage $cov.FullName `
  --private-dir (Join-Path $out 'v7c-private') `
  --safe-dir (Join-Path $out 'v7c-safe')
if ($LASTEXITCODE -ne 0) { throw 'V7c mechanical audit FAILED' }
Write-Host "Private V7c results: $out"
~~~

The first run samples every second committed utterance and holds up to 32 selected items. The actual selected count depends on successful SPEAK commits. The V7c audit's exit 0 means observed mechanical consistency only; it does not assert all speech was verified or any human semantic criterion passed.

## Re-examine an existing natural run without creating artifacts

Before another long experiment, use the original SQLite session and private V6c coverage file to classify the existing ledger. The `--diagnose-only true` mode is strictly read-only: it prints one aggregate JSON object directly to PowerShell and **creates no directories, worksheet, database, or report files**. No Ollama call is dispatched. Do not redirect the output into the repository.

~~~powershell
$runs = Get-ChildItem $env:TEMP -Directory -Filter 'BusStop-V7c-Natural-*' |
    Sort-Object LastWriteTime -Descending
$selectedRun = $runs | Select-Object -First 1
if ($null -eq $selectedRun) { throw 'No existing V7c natural run found' }
$db = Join-Path $selectedRun.FullName 'session.sqlite'
$cov = Get-ChildItem (Join-Path $selectedRun.FullName 'v6c-private') -Filter 'v6c-coverage-*.private.json' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $cov) { throw 'Private V6c coverage is missing' }
$sessionId = [string](Get-Content -Raw $cov.FullName | ConvertFrom-Json).session

dotnet run --project .\tests\BusStop.V7.NaturalAudit\BusStop.V7.NaturalAudit.csproj -c Release -- `
    --db $db --session $sessionId --coverage $cov.FullName --diagnose-only true
if ($LASTEXITCODE -ne 0) { throw 'Existing-run disposition audit FAILED' }
~~~

The aggregate separates terminal `Complete` / `Partial` / `Incomplete`, nonterminal registrations, no extracted claims, protected or Other claims, missing eligible packet evidence, dispatch-eligible but uncharged jobs requiring investigation, and actual charged V4 dispatch. It also counts completed epistemic outcomes. The counts are derived exclusively from unchanged V5a checkpoint chains, and must reconcile to the original audit. It prints no utterance text, speaker handles, IDs, full private paths, reviewer notes or claim excerpts. **Do not infer truthful speech from zero dispatch or absent evidence.**

If a natural-run folder was removed or contains multiple sessions, do NOT start a new run just to satisfy the audit. Locate the matching retained SQLite and coverage, or report them missing. No automatic recovery and no hidden creation of temporary outputs.

## Deferred cleanup commitment

After the current V7c observational review and PR gate, inventory experiment directories in `$env:TEMP`, local repo outputs, and older test artifacts. Preserve a manifest, baseline evidence and any frozen owner-review files, then explicitly authorize deletion of proven disposable folders. The V7c diagnostic command does not delete anything. Subsequent runs should use a single owner-approved output root with predictable retention rather than growing timestamped locations indefinitely.

## Additional natural evaluation conditions

Repeat with a NEW private output directory and --verifier-sample-every 1 --verifier-queue-capacity 2 to deliberately measure queue drops. A drop is recorded as unverified, not as a NoMaterialIssue.

Repeat with --shadow-moderator --moderator-model qwen3.5:9b to inspect historical M1 coexisting with V6c. Preserve pinned model digests and other controls. Different stochastic conversation durations do NOT establish causal participant latency regression; a separate preregistered repeated baseline/treatment protocol with per-participant latency percentiles is still required.

Read the full private transcript and the generated v7c-*.worksheet.private.json. Fill each null label with true/false, all six 1–5 scores, reviewer alias and UTC review timestamp. Run the same natural audit command with the optional --review <completed-private-worksheet.json> argument; it outputs only reviewed aggregates to the safe folder, and never independently authenticates the owner.

## Recovery and final V7 scope

The existing VerifierV5cRecoveryTests already exercise successor epochs, stale owners, interrupted charged attempts, incompatible model digests, stale evidence and corrupt stage hashes. V7c's new disposable test detects a job stranded after registration but does not repair it; explicit V5c recovery is separate. Do not fault-inject into a real private conversation database.

Outstanding for complete V7 acceptance: genuine independently owner-reviewed dev/holdout and frozen thresholds; multiple local Ollama natural runs; manual quality ratings; controlled matched resource/participant-latency study; complete crash/restart fault evidence; per-category semantic acceptance; human final approval. Any failure must produce a deficiency report and remain shadow. No V7 result automatically authorizes enforcement.

**GREEN-before-merge:** GitHub authoring cannot compile the owner's Windows solution or run the local Ollama. This PR stays draft and unmerged until owner-local GREEN and explicit merge approval.
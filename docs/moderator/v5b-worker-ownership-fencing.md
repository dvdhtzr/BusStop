# BusStop V5b — Worker Ownership and SQLite Fencing

**Status:** Draft, not local GREEN, not merged.
**Parent:** merged V5a PR #18 at 985fb8ffe392addc7e25e6877506ab3875274844.
**Scope:** V5b ownership and fencing only; not V5c recovery, V6 runtime integration, or V7 enforcement.

## Ownership protocol

1. An isolated verifier uses an existing, initialized local file-backed SQLite database containing at least one registered V5a job and an explicitly pinned V4 shadow session policy.
2. Acquire the OS-exclusive adjacent lock file through V5bProcessGate.Acquire(databasePath). The path ending in .v5b.lock stays on disk even after shutdown; the open file handle, not existence, provides exclusivity. **Never unlink the live lock file to clear it.** Process termination releases its handle.
3. AcquireV5bSession(sessionId, gate) adds an append-only, strictly increasing epoch associated with one owner GUID and gate GUID. A new exclusive gate can advance an abandoned active epoch after a crash; no clock-based expiry or lease stealing.
4. AssertV5bCanDispatch(owner) is an explicit preflight before model work. It does not dispatch. Every persisted stage write/reservation/finalization also verifies the CURRENT active epoch and still-held OS gate inside its SQLite transaction.
5. On any session that has entered V5b, all V5a stage APIs require the optional V5bOwnership argument and reject unowned or stale writes. Sessions that have never enrolled retain the old V5a/V4 semantics. Even replay of an identical sealed packet checks ownership.
6. V4 budget reservations are charged through the existing moderator_inference_reservations table, never a second budget. The new verifier_v5b_attempt_epochs table stamps each request with the exact SQLite owner epoch in the SAME transaction as the reservation.
7. Ordinary FinalizeModeratorAttempt, LinkV5aChargedAttempt and RecordV5aClaimResult reject a V4 attempt reserved by an older epoch, **even when invoked by the successor owner**. An abandoned Reserved request stays charged and unresolved. V5c will introduce explicit bounded recovery classification; V5b must not fabricate Completed or Interrupted.
8. ReleaseV5bSession(owner) appends an immutable release record. Dispose of the gate after release. Double release by the same current owner is idempotent; stale owners cannot release a successor.

## New append-only private SQLite relations

- verifier_v5b_schema: exact pinned schema version, unknown and partial schemas fail closed.
- verifier_v5b_epochs: immutable (session, epoch) acquisition, gate, owner identity.
- verifier_v5b_releases: immutable (session, epoch) release.
- verifier_v5b_attempt_epochs: immutable reserved request -> epoch provenance.

Each relation refuses UPDATE and DELETE through SQLite triggers. The migration is additive, and a missing V5b row does not opt existing sessions in.

## Preserved boundaries

- No changes to ConversationEngine, participant context, floor policy, dialogue commits, shared memory, M1 judgment contracts, M1b corpus or participant inference.
- No background process, auto-resume, retry, model selection or remote research is introduced.
- Historical M1 reservations/finalization still use their existing API and shared session budget semantics.
- A process-held lock must remain live throughout verification. The system assumes a trusted local filesystem and honest collaborating processes using the same canonical database path. It does not claim to resist hostile DB administrators, symlink/path-alias manipulation or network filesystems with unreliable file-share behavior.
- SQLite epoch fencing prevents stale authorized API writers. Semantic correctness of model-generated contradictions is assessed separately at later gates.

## Windows owner GREEN gate

~~~powershell
cd C:\Users\dvdht\source\repos\BusStop
git status --short
git fetch origin
git switch feature/moderator-v5b-worker-ownership-fencing
git pull --ff-only origin feature/moderator-v5b-worker-ownership-fencing
git rev-parse HEAD

dotnet build .\BusStop.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "V5b build FAILED" }
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "V5b regression tests FAILED" }
dotnet build .\tests\BusStop.V4.OllamaProbe\BusStop.V4.OllamaProbe.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "V4 probe build FAILED" }
Write-Host "V5b LOCAL GREEN" -ForegroundColor Green
~~~

Model-free V5b tests cover OS exclusivity and release, epoch takeover after simulated crash, protected V5a stages, idempotent operation guard, V4 budget charging, stale request epoch prohibition, M1 independence, wrong-database gate, SQL immutability triggers, and schema compatibility.

**Draft only.** GitHub authoring cannot prove local GREEN. No V5b merge or V5c implementation until the owner validates this branch and explicitly approves merge.

# BusStop V5a — Private Staged Verification Ledger

**Status:** Draft, unmerged. Requires owner-local .NET GREEN and explicit merge approval.  
**Parent:** V4 PR #17 merged into \`main\` at \`3363d912c57bcf5ba5005228a5c0168dab115eac\`.  
**Architecture:** Approved Evidence-Grounded Verifier V1–V7 Specification, §11 and V5 scope of §14.  
**Stage boundary:** V5a only. **V5b OS-process ownership + SQLite fencing and V5c bounded restart/recovery are deferred.**

## Core design

The new V5a ledger is private and model-free. It does **not** register itself with \`ConversationEngine\`, \`ShadowModerationWorker\`, the speaking floor, \`ParticipantContextBuilder\`, \`RecollectionService\`, or the M1 audit path. No participant can inspect or be prompted with ledger entries. No network or model calls occur in V5a.

**Additive, separate tables** created by \`ConversationStore.InitializeSchema\`:

| Table | Purpose |
|---|---|
| \`verifier_v5a_schema\` | Immutable format pin; unknown versions refuse downgrade/migration |
| \`verifier_v5a_jobs\` | Immutable committed job identity; unique (utterance, explicit evaluation key), policy JSON/hash and original speech hash/boundary |
| \`verifier_v5a_events\` | Stage sequence, idempotency key, stage payload SHA-256, previous-event SHA-256, event-chain SHA-256 and recorded time |
| \`verifier_v5a_claims\` | Exact host-validated UTF-16 claim snapshots and per-row payload hashes |
| \`verifier_v5a_packets\` | Original canonical evidence packet bytes, packet SHA-256, retrieval/prompt/completeness versions |
| \`verifier_v5a_attempt_links\` | Versioned claim-group reference to an **already reserved** shared V4 moderator attempt |
| \`verifier_v5a_results\` | One immutable per-claim operational/epistemic result, schema, packet and charged verification attempt linkage |
| \`verifier_v5a_summaries\` | Original deterministically reconciled counts, coverage description, SHA and terminal timestamp |

All V5a relations have SQLite **BEFORE UPDATE/DELETE rejection triggers**; writes are only additions. Sidecars and corresponding chain events commit inside one SQLite IMMEDIATE transaction. A failed transaction leaves neither a half-written stage nor an orphan stage event. There are no permissions to rewrite a completed claim or to reopen a terminal summary.

### Entry and stage contract

1. **RegisterV5aJob** accepts only a truly committed, unchanged utterance, correct original recorded speaking-opportunity boundary, explicitly pinned V4 shadow verifier policy and versioned evaluation identity. Repeated identical job registration is idempotent. A different job ID for the same utterance/evaluation is rejected.
2. **SealV5aClaims** accepts at most 24 sequentially indexed, exact-substring, host-validated claim candidates. A zero-claim snapshot is an explicit real stage, not an implicit absence.
3. **SealV5aPackets** accepts at most one packet per claim, validates each packet and its actual as-of speaker evidence scope **only on the first write**, and freezes its original canonical body, hash, authority and completeness. It may seal zero packets to preserve explicit omissions. A replay with the identical packet IDs/bytes is checked against the old snapshot **without new evidence retrieval**; changing packet contents fails closed.
4. **LinkV5aChargedAttempt** requires a real V4 \`moderator_inference_reservations\` entry plus matching \`v4_verifier_attempt_phases\` row, job, session, committed utterance, speaking opportunity and phase. Extraction links have an empty claim group; verification links identify one to four **packaged** claim IDs. V5a never reserves, refunds or retries a model call.
5. **RecordV5aClaimResult** accepts one immutable result per claim; a model-derived completed judgment requires a linked, already-finalized \`Completed\` charged verification request containing that claim. Permissible zero-call outcomes are nonasserted rhetorical \`NoMaterialIssue\`, or \`InsufficientEvidence\` with no eligible evidence or unknown retrieval scope. Operational errors and skipped-budget outcomes remain explicit, not clean factual findings. Completed judgments must pass the existing V1 packet and citation validator.
6. **SealV5aSummary** requires one explicit disposition for **every identified claim**. The counts are recomputed from frozen results; a missing/unverified claim cannot disappear into an accepted final summary. One terminal summary per job, and no later stage events.
7. **ReadV5aCheckpoint** validates immutable identity, session pins, original speech, stored serialization/hashes, the complete hash-linked event chain, stage ordering, row-to-event agreement, archived packet integrity, request provenance and deterministic terminal counts. It returns only previously stored snapshots and **never retrieves replacement evidence or runs an LLM**.

A summary states disposition of the *identified* claims and declared evidence scope, not proof that all possible claims or counterevidence were discovered. Event SHA-256 protects integrity against accidental alteration, not a hostile SQLite administrator who can replace database files and recompute hashes; V5b adds exclusive writer fencing, and V7 evaluates semantic soundness.

## Compatibility and non-goals

- Existing M1 reservation tables and shadow audits, V2 profile anchors/history, V3 claim and packet serializer contracts, and V4 model/verification API remain intact. Ledger writes are new opt-in calls only.
- Legacy databases migrate additively, but a partial ledger without its version marker or an unknown newer version **fails closed before migration**. No older row is coerced into a new verification result.
- A crash after a committed SPEAK but before registration may leave the utterance unreviewed; V5a does **not** manufacture a recovery guarantee.
- **V5a does not schedule or dispatch work**, create a worker/host service, perform OS locking, acquire a fencing token, classify unknown owners, or resume abandoned jobs. It records complete and incomplete stages, and V5b/V5c must establish ownership and compatibility before any later continuation.
- V5a does not add Wikipedia fetches or participant source lookup. Future source snapshots retain per-recipient exposure, revision and as-of provenance boundaries.
- Nothing in V5a authorizes enforcement or affects what any fictional character says.

## Owner-local GREEN gate

Use Windows PowerShell from a clean worktree:

\`\`\`powershell
cd C:\Users\dvdht\source\repos\BusStop
git status --short
git fetch origin
git switch feature/moderator-v5a-staged-ledger
git pull --ff-only origin feature/moderator-v5a-staged-ledger
git rev-parse HEAD

dotnet build .\BusStop.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "V5a Release build FAILED" }

dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "V5a regression tests FAILED" }

dotnet build .\tests\BusStop.V4.OllamaProbe\BusStop.V4.OllamaProbe.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "V4 isolated probe build FAILED" }

Write-Host "V5a LOCAL GREEN" -ForegroundColor Green
\`\`\`

All M0/M1/V1–V4 tests must pass, plus new \`VerifierV5aLedgerTests\` on immutable stage writes, exact idempotency, cross-process SQLite reopen, source scope, unknown request rejection, original job uniqueness, uncharged verdict rejection, incomplete claims, wrong stage order, unchanged terminal summaries, direct SQL update/delete triggers, corrupt event chains, and unknown-schema downgrade refusal.

The authoring environment cannot run the user's Windows build/tests or Ollama. **Do not claim GREEN** from a GitHub source-only review. V5a does not require another local Ollama invocation; it is explicitly model-free. No merge or V5b implementation until owner reports GREEN and approves the staged PR.

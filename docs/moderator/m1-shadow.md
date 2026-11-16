# M1 — Shadow moderation, no enforcement

M1 is opt-in, independent from the participant model. It uses a pinned
session policy and a second Ollama adapter. The host continues to commit
valid participant speech without waiting for moderator inference.

The periodic sampling interval is pinned in the same immutable SQLite session
policy as the moderator model/budget. Older M0 policy rows receive an additive,
backward-compatible default and cannot silently enter active M1 sampling.

A bounded single-reader, nonblocking queue receives a copy of a *committed*
SPEAK. The worker uses a separate SQLite connection and selects periodic
samples plus detected personal-history, third-party-attribution, numerical
and exact-copy cues. A candidate that is not selected is not evaluated.
Overflow is explicitly counted and reported; no silent claim of full coverage.

The worker reserves a moderator inference attempt durably **before** asking
Ollama. The reservation remains charged for timeouts, failed responses and
crashes. Valid judgments must satisfy strict contract, candidate identity
and authorized evidence-reference checks. The result is stored in an
append-only **private** table, with the committed utterance it observed.
Only the candidate's own biography, prior committed speech and pinned
briefing/exposed passages may serve as references. Merely missing a
biography entry does not prove the event logically impossible.

A response that *would* request revision is audit-only in M1. **No**
revision, topic nudge, decision override, altered participant prompt,
change to floor scheduling, or public transcript mutation is allowed.
Shadow does not automatically become experimental enforcement.
Gate A and a controlled corpus are required before any enforcement.

This is a **best-effort observer**, not a guaranteed complete capture:
a process crash before enqueue, a full bounded queue, or a crash during
inference can leave an opportunity without a shadow review. Any reserved
inference remains charged, and the worker reports dropped/failed reviews.
This incompleteness does not affect authoritative conversation recovery.

The CLI's long-run limits are opt-in. Be mindful that local models
have no per-token API bill, but GPU/CPU, VRAM, latency and context growth
remain real. The shadow worker can contend with participant inference
for Ollama resources even though it never awaits moderator judgment before
committing speech.

## Local verification (required)

    dotnet build .\BusStop.sln -c Release
    dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore

Then perform a local Ollama shadow acceptance and inspect the transcript,
diagnostics, SQLite private shadow reviews and dropped-review count.
A GREEN model-free suite alone does not establish judgment quality.
M1 is not a license to start M2, M3 or M4 automatically.

## M1a/M1b hardening — owner-approved, draft PR #13

The original FAA run demonstrated durable shadow observation but not
reliable judgment: 11 of 33 model responses were structurally valid;
18 were incomplete JSON; 3 failed finding/scope validation; 1 used an
incorrect candidate ID. Those are **historical** baseline measurements.

M1a now:
- Uses an Ollama JSON-schema format only for shadow moderator requests;
  participant model request behavior is unchanged.
- Pins the revised rule/prompt versions in newly created session policies.
  An old shadow policy does **not** silently resume under these new rules.
- Uses 8192 context tokens and up to 768 generated tokens per shadow request;
  `done_reason`, prompt-token count, output-token count and duration are
  captured when Ollama returns them.
- Gives a malformed-output attempt at most one retry, with a NEW durable
  moderator reservation. Evidence-scope failures are never retried.
- Checks exact text duplicates in host code, against all earlier committed
  utterances. Same-speaker versus cross-speaker is reported separately;
  these require zero moderator model calls.
- Preserves each charged model attempt's raw private response and completion
  metadata in an additive `attempts_json` SQLite column and private report.
- Treats metaphors, hypothetical examples, natural fictional anecdotes and
  disagreement as permitted unless a specific supported violation is proven.

M1b:
- Holds a privacy-preserving 33-case provisional manifest in
  `docs/moderator/corpus/faa-shadow-v1.labels.json`.
- The FULL original `shadow-*.json` stays local and is never committed.
- `scripts/evaluate-faa-shadow.ps1` verifies the original session and
  all 33 candidate-text fingerprints, then reports historical structural
  validity and provisional false-positive controls.
- The owner's review of provisional labels is required before interpreting
  corpus metrics as reliable semantic quality evidence.

### Verification sequence (local Windows; do NOT merge before GREEN)

From a CLEAN working tree:

```powershell
git fetch origin
git switch feature/moderator-m1-shadow
git pull --ff-only origin feature/moderator-m1-shadow
dotnet build .\BusStop.sln -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'M1 tests failed' }
```

Then evaluate the original audit **without** running Ollama:

```powershell
.\scripts\evaluate-faa-shadow.ps1
```

For a first real-model smoke run (before the long run):

```powershell
.\scripts\watch-faa-shadow.ps1 -Opportunities 18 -ParticipantCalls 36 -ModeratorCalls 36 -EndAfterPasses 2
```

The live script needs the original FAA sample in Downloads, or use
`-Document 'C:\actual\path\to\FAA_Ground_Stop_Nov_13_2026_BusStop_Sample.txt'`.
Once the shorter real-model run demonstrates acceptable results, the script's
longer default run uses 90 opportunities, 180 participant-call ceiling,
96 moderator-call ceiling and nine passes for natural ending.

Inspect the NEW `shadow-<session>.json`:
- `summary.modelValidReviews` vs `summary.modelInvalidReviews`
  (do not mix deterministic copies into the LLM's validity denominator);
- `summary.deterministicExactCopies`, `dropped`, `workerErrors`;
- each `reviews[].Attempts[]`: `DoneReason`, `PromptEvalCount`,
  `EvalCount`, `Failure`, and `RequestId`;
- whether valid findings protect permitted metaphors and disagreements.

A single successful model run does **not** meet the proposed >=95% validity
gate across repeated evaluations, nor establish a low false-positive rate.
The old audit lacked complete original moderator prompts and Ollama completion
metadata, so identical-input same-seed replay is not proven.

No enforcement, topic nudging, persona changes, new RAG behavior, or M2
laboratory promotion is part of this feature branch.

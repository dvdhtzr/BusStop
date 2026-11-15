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

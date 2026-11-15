# H1 — Integrity hardening

This milestone hardens the existing console conversational laboratory without changing
character prompts, turn selection, natural-end thresholds, or the archived baselines.

## Included

- Character profiles (ID + version) and briefings (ID) are append-only via the
  application's persistence API. Identical repeated saves are allowed; conflicting
  saves are rejected.
- On resume, the engine counts persisted model attempts against the session's
  inference budget and checks the budget before **each** call, including retries.
- Replay checks persisted opportunity ordinal, participant, and reason against
  deterministic coordinator expectations, failing closed on mismatch.
- Continuing sessions can only recall completed prior sessions that ended no later
  than the continuing encounter's creation time.
- Each laboratory run writes a session-ID-specific transcript and diagnostics file
  without overwriting prior run evidence, while retaining the existing convenience
  files and combined batch report.

## Important remaining limitations

- **Character versions:** the session schema stores character IDs, not specific
  character versions. Before enabling editable characters, add a compatible,
  tested session-version pinning migration.
- **Recovery settings:** the session does not persist the full coordinator
  options or model configuration. Replay rejects observed mismatches, but some
  configuration changes may not manifest until subsequent decisions. Persist and
  validate canonical configuration fingerprints in a separate migration.
- **Crash inside inference:** attempts are persisted after the model returns, so
  an interrupted in-flight call is not counted. This milestone protects committed
  speech and persisted attempt budgets, not exactly-once external model calls.
- **Concurrency:** a single process owns the console engine. Multi-process
  ownership and fencing are not implemented.
- **Memory quality:** this milestone does not alter retrieval salience or
  conversational guidance; those remain research questions.

## Local GREEN gate

Run from a clean checkout of the PR branch:

```powershell
dotnet build .\BusStop.sln -c Release
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
```

The real Ollama probe is separate and non-gating. Do not merge without local
verification, a diff review, and the owner's approval.

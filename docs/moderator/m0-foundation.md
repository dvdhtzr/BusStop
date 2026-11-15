# Invisible Moderator M0 — foundation only

**Status:** implementation draft, awaiting owner-run GREEN verification and explicit merge approval.

## Scope

M0 introduces a separate .NET 8 moderation contract project, typed judgments and
host scope checks, a no-op disabled implementation, immutable per-session policy
snapshots, and a durable ledger for *future* moderator model-call reservations.
There is **no moderator LLM call**, shadow evaluator, character revision, topic
nudge, moderation UI, or activation of enforcement in M0.

A new session pins its disabled policy in the same SQLite transaction as its
session and participant records. Older databases are upgraded with an additive,
nullable session policy marker. Sessions created before the marker existed keep
their original behavior: no marker means legacy disabled. A non-null marker
with no policy row, unknown version, or invalid policy fails closed.

Future non-disabled policies can be stored to exercise compatibility and
budget contracts in isolated tests. The engine **refuses to run them** until
their respective later milestones have been implemented and approved.

## Privacy and authority

An M0 candidate is a private typed proposal, not a committed utterance.
The disabled moderator returns an Accept disposition only after basic host
candidate identity/scope validation. No candidate is persisted or made available
to the context builder or recollection service. The only speech commit remains
ConversationStore.CommitUtterance. The coordinator and participant model are
unchanged.

Evidence references in future judgments must be within the host-supplied
authorization set and the problematic span must occur in the candidate text.
These checks establish reference scope, **not semantic truth**. An absent
biography entry is not proof that an event was impossible.

## Future inference ledger

The ledger is separate from participant model_attempts. A moderator inference
reservation counts against the pinned budget at reservation time, before
future dispatch. Retrying a model call consumes a fresh request ID. A process
restart does not restore spent capacity, including Reserved or Interrupted
requests. Completing a reservation does not refund it. The ledger cannot
be used when the session's moderator is disabled.

## Explicit exclusions

M0 does not repair existing participant-call accounting; this is a separate
existing concern to review before M3 enforcement. It does not replace the
existing speaking-opportunity replay protocol, and it does not guarantee atomic
moderator approval-to-speech commits (that work belongs to M3).

R1c remains on unmerged PR #11. The M0 branch starts from the earlier main,
and does not include retrieval changes. Future source-aware review must use
actual per-opportunity visibility, never source passages merely stored globally.

## Local verification required before review/merge

Run from the repository root on the feature branch:

    dotnet build .\BusStop.sln -c Release
    dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore

Run existing stub-based smoke checks as well. Compare unmoderated transcript,
speaking order, call counts, recovery and private memory behavior against main.
A real Ollama run is **not needed to prove M0 contracts** and would not prove
moderator quality. Do not merge or begin M1 without explicit authorization.

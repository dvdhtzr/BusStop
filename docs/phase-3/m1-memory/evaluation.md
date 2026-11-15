# Phase 3 M1 — continuing sessions with evidence-linked recollection: evaluation

**Date:** 2026-11-14 · **Model:** qwen3.5:9b (digest 56671c2a) · **Branch:** phase-3/memory.

M1 adds Layer-2 memory: a continuing conversation among the same three characters recalls what was
actually said before, evidence-linked, deterministically retrieved, bounded, and participant-isolated.

## Verdict

**M1 succeeds, confirmed on a cross-topic test.** Memory surfaces and shapes the continuing conversation
without turning it into a replay.

## How it was evaluated

Three real-model reads, each a prior conversation followed by a continuing one between the same cast:

1. **Same-topic, raw recollection (initial).** Memory clearly surfaced, but the continuing conversation
   *replayed* prior lines (a speaker repeated their own "wait and see" line verbatim).
2. **Same-topic, after two refinements.** Replay was removed: recollections exclude the participant's own
   prior lines, and the framing asks the speaker to build on memory rather than restate it. The continuing
   conversation carried the themes forward with fresh specifics. Continuity was real but *subtle*, because
   the identical briefing produces overlapping themes anyway.
3. **Cross-topic (decisive).** Prior conversation on the factory reopening; continuing conversation on an
   unrelated Main Street bridge closure. The bridge conversation repeatedly, and naturally, reached back
   to the factory — which the bridge topic cannot produce on its own:
   - Maya: "I know the mill closing didn't fix everything for you"; "too many fathers try to start fresh
     after the old mills shut down."
   - Arthur: "My forty years of building this town ended when the last whistle blew"; "if that new span
     doesn't leave folks stranded like the mill did."
   It was woven in, not dumped — a genuine bridge conversation informed by shared history, with a
   relational warmth (Arthur tired of being told to "move on"; Maya acknowledging his loss).

## What works

- Retrieval is deterministic, bounded, inspectable, evidence-linked, and participant-isolated (a
  participant draws only on prior conversations it was part of, and never on its own words).
- The anti-replay refinements (own-line exclusion + framing) turned replay into continuity-with-development.
- The cross-topic test isolates memory from topic overlap and demonstrates it unambiguously.

## Known limitations (not memory)

- Within-conversation thematic circling persists, and sessions still reach the opportunity budget — the
  same base-model behaviour recorded in the Phase 2 baseline, accepted there and unchanged here.

## Next

M2 — subjective impressions: a character's remembered *sense* of the others, distinct from fact, which
does not depend on topic overlap at all and should make continuity sharper and more robust.

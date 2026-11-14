# Baseline evaluation — 2026-11-14, qwen3.5:9b (56671c2a)

Read against the eight questions in `../../baseline-protocol.md`. Three fresh-encounter runs, pinned
config (see `diagnostics.json`). This is the reference baseline; it is archived as-run and not re-tuned.

## Outcome at a glance

| Run | Termination | Opportunities | Utterances |
| --- | --- | --- | --- |
| 1 | NaturalEnd | 12 | 10 |
| 2 | BudgetReached | 18 | 16 |
| 3 | NaturalEnd | 15 | 11 |

**Verdict:** the baseline meets the Phase 1 bar. Across three independent runs the three characters are
distinct, grounded, and take the conversation to genuinely different places, with real development and
two natural endings of three. One recurring defect stands out — verbatim self-repetition, which also
caused the single budget overshoot.

## By question

1. **Responsiveness — strong.** Speakers engage with what was actually said and with who addressed
   them; directed replies are answered or redirected, not ignored (e.g. Run 2 Arthur → Daniel on the
   injured worker, turns 6–8).
2. **Coherence — strong.** Each run follows a clear line, including when it changes subject (Run 3's
   drift to the bridge).
3. **Individuality — strong.** The three are distinguishable without the labels: Arthur concrete and
   wary ("forty years of watching plants close"), Maya hopeful from her father's retraining, Daniel the
   shop teacher reasoning from students and the shop floor.
4. **Natural variation — present, and a highlight.** Run 2 develops a specific new thread (who pays the
   medicine bill after an injury). Run 3 *drifts* — Daniel proposes fixing the bridge instead and Arthur
   agrees, a real tangent and a moment of common ground. Passing is used (Run 3 opens with Arthur
   effectively sitting out).
5. **Grounding — strong.** All three runs stay anchored to the story — EV lines, lower pay, automation,
   who the reopening is really for.
6. **Repetition — the weakness.** Run 1 ends with Daniel repeating his own line almost verbatim (turns 8
   and 10). Run 2's final turn is a verbatim repeat of Daniel's earlier line (turns 14 and 18) and is
   why it hit the budget. Maya occasionally echoes a line back rather than advancing it (Run 2, turn 4).
7. **Development — good.** Run 2 builds the injury/medicine-cost argument concretely across several
   turns. Run 3 shows Arthur softening and the group finding common ground on the bridge before Maya
   pulls it back.
8. **Ending quality — good, 2 of 3 natural.** Lengths 12 / 18 / 15 are a healthy range. Run 2 overshot
   to the budget specifically because the models circled on a verbatim repeat instead of passing.

## Technical integrity

All three runs pass: histories are replayable, sequences strictly contiguous, terminations correctly
classified (two `NaturalEnd`, one `BudgetReached`), inference within budget, and no cross-participant
leakage. No model failures occurred in this batch (all outcomes `ValidSpeak` / `ValidPass`), so the
failure-classification paths were not exercised here. `diagnostics.json` records the full fingerprint.

## Classification of the main weakness

**Repetition** (per the negative-results taxonomy), most plausibly **model capability** interacting with
**context construction**: a 9B model sometimes regenerates a near-identical prior line rather than adding
something new or passing, and the recent-dialogue window re-shows it its own last line. Candidate future
experiments — to be run *against this baseline*, one at a time — include an anti-echo / de-duplication
check at commit time, a larger model, or a shorter recent window. None is applied here.

## Notes

- The run's `batch.md` header displayed `floor-1` due to a stale hardcoded label; the authoritative
  fingerprint (`diagnostics.json`) recorded `floor-2`, which is what ran. The archived `batch.md` header
  is corrected to `floor-2` and the code now derives the label from one source.

## Limitations

Three runs are exploratory and cannot establish conversational quality statistically. This read is the
primary evaluation; the counts support it but do not replace it.

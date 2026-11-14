# Experiment: suppress self-echo — negative result

**Date:** 2026-11-14 · **Model:** qwen3.5:9b (digest 56671c2a) · **Against:** the 2026-11-14 baseline.

## Hypothesis

The baseline's one recurring defect was verbatim self-repetition. Intervention: suppress a valid speak
that repeats the speaker's *own* recent line (word-set Jaccard ≥ 0.8), re-prompt within the attempt
budget, and treat a persistent echo as silence. One variable changed from the baseline
(`suppressSelfEcho: true`); everything else pinned.

## Result — 6 runs

| Run | Termination | Opportunities | Utterances |
| --- | --- | --- | --- |
| 1 | BudgetReached | 18 | 18 |
| 2 | NaturalEnd | 3 | 1 |
| 3 | NaturalEnd | 9 | 7 |
| 4 | BudgetReached | 18 | 16 |
| 5 | BudgetReached | 18 | 17 |
| 6 | BudgetReached | 18 | 12 |

**Verdict: no measurable improvement. Not adopted.**

## Why it did not help

1. **It fires on almost nothing.** Run 6's diagnostics recorded zero `EchoedSelf`, yet Maya still
   near-repeated herself: *"…I've seen folks start fresh at lower wages before; maybe we shouldn't write
   off the whole deal just yet"* appears twice with an identical tail and a different opening clause. The
   differing intros drag the word-set similarity below 0.8, so "same tail, different head" repeats slip
   through.
2. **The echoes that occur are cross-speaker, which a self-only check cannot see.** Run 3 has a genuine
   verbatim repeat — but it is Daniel repeating *Arthur's* whole line, not his own.
3. **The dominant issue this batch was not echo at all.** Four of six ran to the budget (18). These are
   *engaged, oppositional* debates that rarely produce the two consecutive passes the ending needs, and
   their tails circle *thematically* (not verbatim) — which self-echo suppression cannot address.

## Decision

- **Anti-echo is not adopted.** It adds complexity (a new outcome class, a detector, an option) for no
  measurable gain, and targets a defect that proved minor and partly undetectable. The code is preserved
  on the unmerged `phase-2/anti-echo` branch for reference.
- **The empty-start fix is kept.** The experiment surfaced a separate, real bug — a conversation where
  everyone passed at the opening reported `NaturalEnd` with zero utterances. That fix (a natural end now
  requires the conversation to have started; an unengaged room ends as `NoEngagement`) is a correctness
  improvement and is merged independently of anti-echo.

## Note for future work

If echo is revisited, the detector would need to (a) check all recent speakers, not just self, and (b)
use a sequence/substring measure rather than word-set overlap to catch identical tails. But the larger
lever is the budget overshoot of engaged conversations, not echo — and the baseline is already
convincing, so further tuning should clear a real need first.

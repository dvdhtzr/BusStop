# Phase 1 Baseline — Pre-Registration

This records the pinned configuration and the evaluation protocol for the Phase 1 conversational
baseline, **fixed before the baseline runs**. It is not adjusted mid-series to improve the apparent
result. A change to any pinned value creates a *new* baseline; it does not reinterpret this one.

## Pinned configuration

Every run records this fingerprint in its `diagnostics.json`; the model digest is captured at run time.

| Field | Value |
| --- | --- |
| Model | `qwen3.5:9b` (Ollama) |
| Model digest | `56671c2ab9385f9cfcb404638e32cd62d88e3501d44822208363c010179a3c90` |
| Temperature | 0.8 |
| Reasoning (think) | off (`--no-think`) |
| Max opportunities | 18 |
| Max model calls | 36 |
| Max attempts per opportunity | 2 |
| Natural ending | after 2 consecutive passes |
| Ping-pong override window | 4 |
| Recent-dialogue window | 20 utterances |
| Protocol version | `floor-2` (opening → directed-reply priority → ping-pong override → fair order → end on 2 passes) |
| Context version | `ctx-3-ending` (competence: brevity, no restating, concede/build/change-subject/pass, rest when done) |
| Characters | Arthur v1, Maya v2, Daniel v2 |
| Briefing | `aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa` — "Shuttered auto-parts plant to reopen under new owner" |
| Mode | fresh encounter |
| Runs | 3 independent runs (the canonical baseline; more may be run as supplementary signal) |

## The command

```bash
dotnet run --project src/BusStop.Console -- --model ollama --ollama-model qwen3.5:9b --no-think --runs 3 --out ./baseline
```

This produces `baseline/batch.md` (all transcripts + summary table) and `baseline/diagnostics.json`
(the last run's fingerprint). Archive these, see **Archiving** below.

## Evaluation questions

Each run is read by a human and answered against concrete turns (e.g. "Arthur restated the rent point
at turns 4, 9, 14"). Automated counts support reading; they never replace it.

1. **Responsiveness** — Does each speaker engage with what was actually said? Are direct questions acknowledged meaningfully?
2. **Coherence** — Does the conversation develop logically, even when it changes subject?
3. **Individuality** — Can the three be told apart without the speaker labels?
4. **Natural variation** — Do humor, disagreement, uncertainty, curiosity, and silence arise where they fit?
5. **Grounding** — Are briefing claims, personal opinions, and fictional anecdotes distinguishable?
6. **Repetition** — Are arguments restated unnecessarily? Are there loops?
7. **Development** — Do participants acknowledge earlier statements and evolve, rather than re-entrench?
8. **Ending quality** — Did it end naturally, or because it ran out of budget?

## Technical integrity checklist (per run)

- Complete, replayable history.
- No cross-participant private-context leakage.
- No duplicate committed speech; strictly contiguous sequence.
- Correct attribution and directed-reply handling.
- Inference within the budgets above.
- Model failures classified, never delivered as speech and never counted as a pass.
- Termination correctly classified (`NaturalEnd` vs `BudgetReached`).

## What counts — and what does not

Do **not** automatically count as success: more utterances, longer conversations, more disagreement,
more agreement, more questions, more topic changes, or fewer silences. Nor automatically as failure.
The objective is **credible, coherent conversation** — judged by reading.

## Negative results

If the baseline disappoints, preserve it and classify the likely cause: character differentiation,
model capability, context construction, turn scheduling, conversational guidance, repetition, source
material, technical failure, or insufficient evidence. Then change **one** mechanism at a time and
compare against this archived baseline.

## Archiving

Save each baseline's `batch.md` and `diagnostics.json` under `docs/baseline/<date>-<model-digest>/`,
with a short note of the human evaluation against the questions above. That archive is the reference
every future change is measured against.

## Limitations

Three runs cannot prove improved conversational quality; this laboratory is exploratory. Human reading
is primary. Different stochastic outcomes across runs are expected and do not, by themselves, invalidate
the comparison.

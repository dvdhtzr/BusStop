# R1c — First lexical-RAG FAA conversation evaluation

**Date:** 2026-11-15  
**Status:** Real-model retrieval acceptance **PASS**; conversational-quality improvement over R1b **NOT DEMONSTRATED**. No change to prompts, memory, retrieval settings or engine behavior authorized by this evaluation.

## Materials and limits

Directly inspected owner-supplied `BusStop-R1c-20261115-095647.zip` containing `transcript.md` and `diagnostics.json`. Both identify a single local-LLM fresh encounter about the previously provided 10,255-character FAA ground-stop sample.

The source snapshot is `3ef497ce48880be951ae8a6e4700e6104ed1155cb28153761d839e1cf89eac23`, which matches the original sample whose text SHA-256 is `dd93673743c6b33aef1d869f823c17b9e21c8c714401235912170a2449a302fa`.

The attached ZIP does **not** contain the private SQLite source database or the actual model request/response archive. The transcript and diagnostics are consistent with the intended turn-specific visibility, but a post-crash SQLite replay is tested by the source suite, not independently demonstrated by these two files.

R1b comparison is descriptive only, using the earlier owner-provided FAA document acceptance (17 utterances / one pass / one `BudgetReached` run); different stochastic sessions are not causally paired.

## Technical acceptance

| Metric | R1b fixed excerpts | R1c lexical RAG |
| --- | ---: | ---: |
| Visible source passage scope | First four | All nine eligible |
| Unique passage IDs actually offered | 4 | 9 |
| Session model | qwen3.5:9b | qwen3.5:9b |
| Spoken turns | 17 | 16 |
| Voluntary passes | 1 | 2 |
| Speaking opportunities | 18 | 18 |
| Model attempts | 18 | 18 |
| Invalid attempts | 0 | 0 |
| Ending | BudgetReached | BudgetReached |

Model digest: `56671c2ab9385f9cfcb404638e32cd62d88e3501d44822208363c010179a3c90`. Context version: `ctx-3-ending+doc-rag-lexical-v1`. Configuration: temperature 0.8, think off, `floor-2`, 18/36/2 budgets, two-pass natural end. `sequenceContiguous` is true. `sourceEvidence.onDemandRetrieval` is true and `exposedPassageIds` is empty as required by R1c.

There are **18 retrieval events**, one per speaking opportunity; each records three passage IDs, query text, algorithm `lexical-overlap-v1`, finite scores and a speaker/opportunity identity. Two of the 18 opportunities ended in a voluntary pass rather than delivered speech. No model attempt failure is reported.

## What did retrieval actually select?

| Passage | Times selected across 18 opportunities |
| --- | ---: |
| p00001 | 11 |
| p00002 | 7 |
| p00003 | 11 |
| p00004 | 3 |
| p00005 | 1 |
| p00006 | 1 |
| p00007 | 6 |
| p00008 | 13 |
| p00009 | 1 |

All nine passage IDs were indeed offered at least once. That means **collection coverage = 9/9**, not that the participants understood or used all nine. Notably p00005 and p00006 (parts of traveler costs and modernization), and p00009 (largely source-reference material), appear only once each. p00008 is frequent, often because the conversational query favors general technology/infrastructure vocabulary. The source's bibliography tail is not necessarily a meaningful conversational fact, so passage-count coverage is a weak proxy for usable evidence.

The retrieval query consists of the document title plus recent **already committed character speech**. As the discussion gets trapped in old-vs-new shop analogies, the query inherits those terms and keeps retrieving passages about technology and systems. This is a feedback loop in topic selection, not a source-integrity failure.

## Qualitative reading

**Opening:** Arthur starts with **"we didn't need a network to run the shop"**; Maya immediately invokes her father retraining; Daniel invokes students and changing tools. These are credible character cues but prevent the discussion from opening on the specific 2026 FAA communications event.

**Argument loop:** Arthur repeats variations of *old ways, old switch, wires versus eyes, fixing a leak*. Maya repeatedly rebuts that people adapt to new machinery. Daniel repeatedly compares students working with digital tools to old lathes. The premise barely advances despite evidence retrieval from later portions of the FAA document. For example, the subjects of refunds, contractor accountability, and investigation uncertainty are barely discussed.

**Exact speaker echo:** At spoken turn 13, Daniel repeats Maya's entire preceding turn **word for word**, including the first-person **"my dad showed me"**. The independently characterized male shop teacher adopts the nurse's personal history. This is a clear failure of agent individuality and an exact cross-speaker copy, distinct from retrieval correctness.

**False shared personal history:** On the last spoken turn, Maya tells Arthur **"your dad didn't just keep his hand on an old switch when the mill closed; he learned to trust new lines after finding the leak"**. That detail is not in Arthur's profile or his actual earlier words. The model has combined Arthur's separate fictional mention of a father's line leak, Arthur's switch metaphor, and Maya's own father's mill retraining into one unsupported interpersonal biography.

**Factual use:** Maya's final mention of roughly **3,500 aircraft** has support in the document's account of aircraft already airborne. That numeric detail appeared in p00004, which was retrieved for her last opportunity. This is one relevant fact, but an isolated grounded number does not mean the conversation has explored the report substantively. There is no robust source-citation discipline in the spoken JSON protocol.

**Outcome:** 16 utterances, two voluntary passes, and an 18-opportunity budget ending. The conversation has neither a naturally supported ending nor meaningful conversational evolution. One turn fewer than the R1b baseline does not support an improvement claim.

## Verdict and next decisions

**R1c technical PASS:** The opt-in retrieval path can reach passages beyond the first four, records each speaker's candidate evidence in a bounded local, attributable way, and retains the previous transcript integrity controls.

**R1c conversational quality NOT IMPROVED in this one pilot:** Broadening context availability did not prevent loops, copied identity, or invented shared memories. Do not present 9/9 coverage as a quality success. No causal comparison is possible from one stochastic run per mode.

**Do not chase prompt tuning or extra memory mechanisms based on this single case.** Preserve R1b static and R1c dynamic as separate evaluation baselines, leave M1 unchanged and M2 deferred. The next product goal is still random current-news sourcing and user-provided document RAG, with three selected characters discussing genuinely different topics—not endlessly revisiting the same synthetic FAA story.

Before merging PR #11, require owner-confirmed Release build and automated tests GREEN, plus explicit merge approval. This evaluation does not merge the PR and does not authorize R2 news fetch yet.

Future, separately scoped improvement candidates to evaluate:
- source-aware discussion opening/agenda and explicit grounding of factual assertions;
- avoid retrieval-query feedback loops dominated by fictional character analogies;
- distinguish passage coverage from actual use in claims;
- test unrelated source topics and varied personalities to separate fixture overfitting from core conversation behavior.

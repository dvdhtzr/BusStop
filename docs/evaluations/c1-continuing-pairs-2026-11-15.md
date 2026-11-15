# C1 — Three fresh/continuing pairs: evidence review

**Date:** 2026-11-15. **Research status:** qualitative read of six submitted
real-Ollama sessions; no conversational engine or prompt changes.

## Evidence and provenance

Reviewed the owner-supplied `BusStop-C1-20261115-072638.zip`, which contains
three independent laboratory directories (`pair-01` … `pair-03`),
two individually named transcripts and two individually named diagnostics
per directory, a batch report, and the original session SQLite databases.

The SQLite `sessions`, `speaking_opportunities`, `model_attempts`,
`participant_decisions`, and `utterances` records were cross-checked
against diagnostics and transcript utterance counts. The **underlying
archive itself is not yet committed to this public repository**; this
report documents the evidence until its publication is approved.

All runs matched the prior frozen baseline's local model and digest:
`qwen3.5:9b`, `56671c2ab9385f9cfcb404638e32cd62d88e3501d44822208363c010179a3c90`,
temperature 0.8, thinking off, three fixed characters, `floor-2`,
`ctx-3-ending`, max 18 speaking opportunities, 36 model attempts,
two consecutive voluntary passes to end. Each pair has a
fresh-encounter factory discussion and a continuing-session bridge
discussion among the same characters. Both stories are *fictional
laboratory fixtures*, not live or verified news.

| Pair | Factory session | Bridge continuation |
| --- | --- | --- |
| 01 | BudgetReached; 18 opportunities, 16 utterances, 2 passes | BudgetReached; 18 opportunities, 18 utterances, 0 passes |
| 02 | NaturalEnd; 16 opportunities, 13 utterances, 3 passes | NaturalEnd; 9 opportunities, 7 utterances, 2 passes |
| 03 | BudgetReached; 18 opportunities, 16 utterances, 2 passes | NaturalEnd; 9 opportunities, 7 utterances, 2 passes |

**Totals:** 88 opportunities; 88 persisted model attempts; 77 committed
utterances; 11 valid passes; **three technical natural endings and
three budget endings**. All six diagnostics show contiguous utterance
sequences and only `ValidSpeak` / `ValidPass` outcomes.

This is a descriptive sample, **not** a statistical A/B test.

## Findings by pair

### Pair 01: continuity of concerns, not yet a believable relationship

The factory argument stays on the cost of hope, lower wages,
automation, and whether retraining is enough. Arthur and Maya go
around that theme repeatedly, with only brief concessions. In
factory turn 13, Maya incorrectly speaks as if Arthur previously
introduced his own father; the fixed Arthur profile and preceding
dialogue do not establish that fact. The conversation ends at the
opportunity ceiling.

In the continuing bridge session, Arthur starts with **"Hope's fine
for the young, but it won't feed the east side"**: it echoes the
*stance* of his earlier conversation, but that may arise independently
from his fixed persona and the bridge's commute concerns. Maya brings
in the factory again and claims a factory **"burned down"**, whereas
the frozen factory briefing only says it closed. Daniel introduces
an invented classroom lathe replacement story, which Arthur then
treats as shared experience. It is not corroborated by a prior
session utterance.

Two especially serious quality failures occur in the continuing
bridge discussion:
- Arthur's bridge turns 13 and 18 are near-verbatim self-repetitions.
- Arthur's turn 8 warns that a bridge collapse would mean a funeral,
  but his turn 15 declares **"A funeral is better than a new bridge
  built on empty wallets"**. This is an internally inconsistent,
  implausible value judgment, not just a stylistic echo.

The bridge discussion also runs to the budget, without a social ending.

**Assessment:** weak/mixed episodic specificity, low relational
development, high repetition and coherence risk.

### Pair 02: strongest specific recollection, but a short continuation

The factory discussion explores whether workers can safely operate
new automated equipment. It reaches a technical natural ending after
13 spoken turns, but Daniel's final turn **exactly copies** Arthur's
preceding statement about machines consuming safety margins. This
is a cross-speaker echo, not evidence of a genuinely independent
voice. The participants also confidently introduce a first-winter
quit rate unsupported by the frozen briefing.

The continuing bridge encounter begins with Arthur referring to
Maya's father learning to weld. This is a **specific bridge** to the
prior factory conversation: Maya's factory turn 2 describes her
father learning to weld, and the fixed Maya persona says only that
her father *retrained*. Arthur's deterministic recollection selection
would have included her factory turn 2. Maya also addresses Arthur's
earlier concern about **"shortcuts"**, which appeared repeatedly in
the factory session.

The seven-turn bridge exchange shows recognition and some empathic
acknowledgment, but no demonstrated lasting change in trust. Daniel
speaks once and asserts that the bridge's supports are visibly
leaning and its steel is fatigued—specific structural observations
not established by the bridge briefing.

**Assessment:** encouraging episode-specific carryover; relational
change remains uncertain. `NaturalEnd` records the coordinator's
two passes, not necessarily a satisfying conversational conclusion.

### Pair 03: strongest thematic carryover and conditional cooperation

The factory conversation develops a distinctive argument about
whether automation erases the value of traditional manual skills.
Daniel and Arthur press the difference between a bench and a screen;
Maya argues that new skills can supplement old ones. This is a
coherent, somewhat varied exchange, though it reaches the budget.

The bridge conversation transfers **hands-on workmanship versus
screens** into the construction discussion. The carryover is
concrete: Maya's recollection set would include Daniel's factory
turn 5 about holding a hammer and Arthur's factory turn 8 about
**"trade your hands for buttons on a screen"**. Maya's continuing
turn 2 speaks of keeping people in trades **"instead of just
swapping them for a screen"**. Arthur's bridge turn 4 then
conditionally agrees with her proposal that builders remain
involved, rather than merely resisting change.

This is the **best sign of relationship-like responsiveness** in
the submitted sample: Maya incorporates the others' previously
stated values, and Arthur accepts a qualified proposal in the
new setting. However, the seven-turn exchange is too short to
show whether either participant retains a changed view. Arthur
also invents a twenty-year history crossing this bridge with
his wife and claims its **"last bolt gave out"**, which is not
reported by the briefing and may imply an event that never occurred.

**Assessment:** strong topical continuity, modest evidence of
social adaptation, unproven enduring relationship development.

## Memory trace versus memory claim

The session SQLite data confirm that each second encounter began
only after the first ended, with the correct `Continuing` mode and
a different frozen briefing. Inspection of the repository's
`RecollectionService` confirms that each participant can receive
up to four deterministically sampled prior utterances **spoken by
the others**, not by themselves. The report reconstructed those
selections from the persisted first-session utterances and the
repository algorithm, rather than from actual logged prompt payloads.

- **Pair 01:** generalized hope/pay/factory arguments carry across.
  Weak causal evidence: much of that is also in persona descriptions.
- **Pair 02:** Arthur's reference to Maya's father **learning to
  weld** matches a specific selected prior line and is not supplied
  by the fixed profile or bridge briefing.
- **Pair 03:** the **hands-versus-screens** wording and concern carry
  into Maya's bridge reply and match selected lines from Arthur and
  Daniel's prior argument, despite being absent from the bridge news.

These are genuine indicators of **episode-aware conversational
continuity**. They are **not** a controlled demonstration that the
memory feature caused the differences: a matched fresh bridge-only
control is absent, and thematic or invented continuity can be
produced by the model independently.

## Conversational authenticity rubric

| Question | Evidence-based finding |
| --- | --- |
| Responding to other people | **Mixed/positive.** Replies follow arguments, though agreement formulas and counterstatements often stand in for deeper understanding |
| Character individuality | **Mixed.** Arthur's idiom, Maya's family framing, and Daniel's shop analogies differentiate the cast, but they recur mechanically; one exact cross-speaker copy is a major miss |
| Spontaneous curiosity / disagreement | **Mixed.** Useful questions about training and trades occur, but most turns are reply/counterreply; little exploratory social exchange |
| Episodic recollection | **Positive but limited.** Two pairs show identifiable references to previously spoken material not in the bridge fixture |
| Relationship development | **Suggestive, unproven.** Pair 03 shows conditional social cooperation; no multi-session persistence test or subjective impression evidence |
| Repetition / looping | **Weak.** Exact cross-speaker echo in pair 02 factory; near-verbatim self-echo in pair 01 bridge; repeated stance restatement in pair 01 factory |
| Grounding and coherence | **Concerning.** Confidently invented details (factory burning, bridge supports, quit rates) and one explicit reversal about a fatal bridge collapse |
| Quality of ending | **Weak/mixed.** Half the sessions hit the opportunity ceiling. The other three satisfy the coordinator's pass rule, not necessarily a natural human stopping point |

The word `NaturalEnd` is a technical **termination label**, not a
human evaluation of emotional or conversational closure.

## What we should learn, not prematurely fix

The H1 durability improvements worked in this evidence set:
independent per-session artifacts were produced and persisted
attempt/utterance counts match the durable SQLite records. No
model failures occurred, so this batch does not exercise technical
failure recovery paths.

The key authenticity constraints appear to be **substance and
coherence** more than message transport or memory retrieval:
- Character-specific anecdotes can displace discussion rather than
  reveal unique insight.
- Model improvisation sometimes asserts unverified events as facts
  and creates contradictions with previously expressed values.
- Repeated affirmations and metaphors often replace genuinely new
  thinking.
- Continuing sessions can carry issues forward, but consistent
  long-term social growth is not yet observable.

The negative anti-echo experiment remains rejected: a lexical
similarity filter would not address invented facts, moral
contradictions, or thematic circling.

## Recommendation and next controlled gate

**Do not add M2 subjective memories or tune multiple components
together on the strength of these six transcripts.**

The cleanest next scientific question is whether memory produces
*more authentic* continuity than the same cast simply responding
to the bridge story **without memory**.

Proposed **C1b — matched bridge-only control**, design first:

1. Add only a minimal, explicit laboratory-runner option to start
   a `FreshEncounter` with the existing frozen bridge briefing.
   Do **not** alter prompts, coordinator, profiles, memory selection,
   or the underlying model adapter.
2. Keep the three archived continuing bridge transcripts as the
   descriptive memory-on cohort. Run three new independent
   bridge-only fresh sessions with the same pinned model digest,
   temperature, and budgets; note stochastic variability and
   differing preceding context.
3. Compare blind voice attribution and independent judgments of
   responsive listening, specific prior-only references, invented
   events, reversals, repetition, and ending quality.
4. Do not infer model improvement from question counts or transcript
   length. Interpret this as a small qualitative pilot, not proof
   of a causal effect.

A stronger **future** causal design would need matched random seeds
or otherwise controlled generation conditions, and multiple paired
replicates with memory enabled/disabled. That is beyond this
minimal laboratory-runner check.

**Decision:** preserve the results, keep the current conversation
engine unchanged, and seek explicit architectural approval for the
minimal C1b control before implementation. After that, choose
one substantive authenticity intervention only if the comparison
shows a repeatable, meaningful limitation.

# C1b — Memory-on versus fresh bridge-only: qualitative pilot

**Date:** 2026-11-15  
**Status:** Read-only evaluation of owner-supplied experiment archives. No
production code, conversation prompts, model configuration, or stored
experiment records were changed in this evaluation.

## Provenance

- **Memory-on group:** three bridge continuations from
  `BusStop-C1-20261115-072638.zip`, following three factory encounters.
  Refer to [C1 continuing-pairs read](c1-continuing-pairs-2026-11-15.md).
- **Memory-off group:** three independent fresh bridge encounters from
  `BusStop-C1b-20261115-075041.zip`.
- The two **underlying owner-uploaded ZIP archives are not committed to
  the public repository**. This report is an interpretive summary.

Each supplied archive was inspected directly. The session metadata,
utterance, opportunity, and attempt counts were compared with its
individual diagnostics and SQLite database records. The bridge-only
archive contains **exactly three `FreshEncounter` bridge sessions**
and no factory session. The continuing group uses `Continuing` mode
and follows a distinct ended factory encounter. The unchanged runner
supplies recollections only for `Continuing`; thus the control has
no cross-session recollection input even though all three fresh
control sessions share a SQLite database.

Every bridge session has the same:
- `qwen3.5:9b` model and exact digest
  `56671c2ab9385f9cfcb404638e32cd62d88e3501d44822208363c010179a3c90`
- Temperature `0.8`; thinking disabled
- Arthur v1, Maya v2, Daniel v2
- Frozen bridge briefing ID `bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb`
- Protocol `floor-2`; context `ctx-3-ending`
- Maximum 18 opportunities, 36 calls, 2 attempts per opportunity,
  two consecutive passes for the technical natural ending.

All six bridge-session diagnostics show contiguous committed speech,
and the outcomes are exclusively `ValidSpeak` and `ValidPass`.

## Six bridge conversations at a glance

| Group | Run | Opportunities | Spoken turns | Passes | Termination |
| --- | --- | ---: | ---: | ---: | --- |
| Memory on | 01 | 18 | 18 | 0 | BudgetReached |
| Memory on | 02 | 9 | 7 | 2 | NaturalEnd |
| Memory on | 03 | 9 | 7 | 2 | NaturalEnd |
| **Memory on total** | **3** | **36** | **32** | **4** | **2/3 NaturalEnd** |
| Memory off | 01 | 18 | 14 | 4 | BudgetReached |
| Memory off | 02 | 8 | 6 | 2 | NaturalEnd |
| Memory off | 03 | 18 | 11 | 7 | BudgetReached |
| **Memory off total** | **3** | **44** | **31** | **13** | **1/3 NaturalEnd** |

Nearly identical spoken totals (32 versus 31) tell us
**nothing** about authenticity. Pass count and end label are
mechanistic outcomes, not human quality ratings. The memory-off
third run reached its opportunity budget despite seven passes
because they never formed the required consecutive ending streak.

## Memory-on: episode-specific carryover exists, but varies

**Run 01:** General factory references to wages, hope, and losses
could have been generated from the fixed character biographies.
The bridge encounter has substantial circling and an implausible
contradiction: Arthur worries that a collapsing bridge would cause
a funeral, then says **"A funeral is better than a new bridge built
on empty wallets"**. No pass is taken; the run exhausts the budget.
There is no compelling demonstration here of relationship growth.

**Run 02:** Arthur begins with **"If that mill taught him to weld"**,
which is a specific link to Maya's father **learning to weld** in
the preceding factory conversation. Maya's fixed character profile
says that her father retrained but **does not specify welding**.
This is the clearest evidence of episode-linked recall, though it
does not independently establish any lasting change in trust.

**Run 03:** Maya carries the earlier **hands versus computer screens**
disagreement into the bridge construction discussion and proposes
protecting manual trades. Arthur responds with a conditional
concession: **"maybe we can keep those hands on the steel instead
of behind glass."** This is the strongest *social responsiveness*
example, and the underlying wording is found in the first
session's dialogue. It is not enough to establish an evolving
relationship from just one continued encounter.

## Memory-off: no episodic recall, but familiar-sounding history

**Run 01:** Arthur and Maya debate access for low-income residents
versus the safety of closing the span. They gradually narrow in on
whether repairs can happen without interrupting crossings, but
return to the same conflict instead of resolving it. Maya invokes
her father's mill retraining, despite **no factory encounter**
having preceded this run: that detail is already in her *static
character profile*. It is a concrete false-positive warning
against scoring all factory or mill references as memory.

The bridge's **rotted foundation** and permanent closure of the
only crossing become assumed facts, despite the briefing not
establishing those specifics. Daniel claims a child walking home
can't afford **extra gas**, an incoherent connection that a more
attentive speaker might question.

**Run 02:** This is a short, technically natural ending. Arthur
objects to a two-year detour; Maya reframes inconvenience against
safety. Daniel abruptly introduces both students unable to
practice welding **in the rain** and an **oak tree** allegedly
dependent on a shortcut, creating an implausible causal chain.
Maya treats the tree as an emotionally important concern without
interrogating that relevance. The characters respond to words,
but not reliably to meaning.

**Run 03:** The dialogue returns repeatedly to the effects of
detours on students and downtown businesses. At one point Maya
tells Arthur **"we've seen folks like your father retrain"**.
Arthur's approved profile does not establish a retraining father,
and there is **no earlier conversation in this control group**.
This is an *invented shared-history reference*: a model can
produce language sounding like familiarity even when memory is
off. Maya also argues for keeping east-side schools open,
although the briefing describes transportation disruption,
not planned school closure. Seven voluntary passes never form
the consecutive pair needed to end; the run hits the budget.

## Interpretation

**Supported:**
- The minimal C1b control works as designed. The only systematic
  difference in inputs is the continuing group having prior
  conversation/recollection context (as well as a different
  session history); the same model, briefing, cast, and budgets
  are used.
- Specific earlier content is reflected in two of three
  memory-on continuations, especially Maya's father's **welding**
  and the **hands-versus-screens** argument.
- Memory-off dialogue can independently mention mills and
  retraining because that information belongs to the characters'
  biographies.
- False familiarity, unsupported public-world claims, shallow
  affirmations, and unproductive argument loops occur **without**
  the memory feature as well. They are not uniquely caused by
  recollection.

**Not supported:**
- That memory reliably improves emotional realism,
  relationship depth, or trust.
- That memory-on conversations have better natural endings:
  the reported end state is defined by coordinator pass counts.
- That memory-on reduces invention or repetition.
- That the same output length implies equivalent conversational
  substance or quality.
- That this is a tightly paired or statistically powered
  causal study: the groups were sampled at different times,
  without a shared random seed or matched prior experience.

**Important creative distinction:** The characters are fictional,
so plausible invented personal anecdotes are allowed.
The authenticity failure occurs when they **attribute invented
memories to another person**, quietly change shared public facts,
or contradict their own earlier statements without recognizing
the change. A rigid ban on all invented personal experience
would damage the intended simulation.

## Decision gate and recommendation

**C1b is complete as an experiment.** It demonstrates a *modest,
inspectable episodic-memory benefit* but not convincing lasting
relationship development. Keep M1 recollection intact; do not
implement M2 subjective impressions on this evidence.

The most promising next *single-variable* intervention to test
is **conversational evidence attribution**, not extra memory:
clarify in the existing participant guidance that statements
about what **another character previously said or experienced**
should be based on the visible conversation or recollection;
distinguish those from one's own freely invented personal
anecdotes. Preserve natural informality and improvisation.

A prompt-only variant should be preregistered before implementation,
with one control vs changed condition and human reading of
unsupported interpersonal memory, spontaneous creativity,
responsiveness, and unwanted cautiousness. Do not add a
sentence-level censor, moderator agent, fact database, or
automatic penalties. A prompt constraint could itself make
characters stiff or bland; that possibility must be evaluated.

**No implementation is authorized by this report.** Confirm
the proposed next experiment's scope before changing code.

## Review sources

- Archived fresh/continuing results:
  `BusStop-C1-20261115-072638.zip` (owner-provided)
- New fresh bridge-only controls:
  `BusStop-C1b-20261115-075041.zip` (owner-provided)
- [C1 initial qualitative baseline](c1-authenticity-baseline-2026-11-14.md)
- [C1 continuing-pairs evaluation](c1-continuing-pairs-2026-11-15.md)
- [C1b control protocol](../experiments/c1b-bridge-only-control.md)

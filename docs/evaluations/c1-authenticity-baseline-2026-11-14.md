# C1 — Conversational authenticity: archival baseline audit

**Status:** Read-only human-style assessment of the already archived baseline; no prompts,
engine behavior, model settings, or prior transcripts changed.

**Evidence:** [Three-run batch](../baseline/2026-11-14-56671c2a/batch.md),
[pinned protocol](../baseline-protocol.md),
[original assessment](../baseline/2026-11-14-56671c2a/evaluation.md),
[negative anti-echo result](../experiments/2026-11-14-anti-echo/evaluation.md),
and [M1 memory assessment](../phase-3/m1-memory/evaluation.md).

**Sample:** Three independent fresh-encounter conversations using qwen3.5:9b
(digest prefix `56671c2a`), the **same fictional** factory-reopening briefing,
and the same characters. 37 committed utterances across 45 opportunities.
Two runs terminated `NaturalEnd`; one reached `BudgetReached`.
No new Ollama inference has been performed for this review.

## Method and guardrails

This is a small, qualitative, **non-blind** reading of the archived transcripts.
Each category is classified as *evident*, *mixed*, *limited*, or *not assessable*.
A coherent or long response is not automatically a good one. Nor do we treat
agreement, disagreement, questions, or silence as intrinsically desirable.
We distinguish actual conversational movement from simply repeating or
acknowledging another's words.

The original baseline authors' assessment is a useful reference, **not**
a substitute for inspecting specific turns independently. Counts below
were derived from the transcript text and must not be mistaken for
statistics demonstrating model improvement.

## Observations from all three runs

- 37 spoken turns (Run 1: 10; Run 2: 16; Run 3: 11), with
  **Arthur 16, Maya 10, Daniel 11**.
- 36/37 spoken turns carry explicit **addressee metadata** in the archived
  rendering. This does **not** mean the characters literally spoke names
  36 times. It does suggest a conversation structured predominantly around
  reply/counterreply, not unprompted observations to the group.
- Only **two** spoken utterances contain a question mark (Run 2: 1;
  Run 3: 1), and Run 3's question is a rhetorical clarification.
  This is a crude proxy for curiosity, not a direct measure: a person can
  ask an indirect question without a question mark.
- **Two clear near-verbatim self-echo pairs**: Daniel Run 1 turns **8/10**,
  and Daniel Run 2 turns **14/16**. Run 2 Maya turn **4** closely echoes
  Arthur turn **3**. Both transcripts and the previously archived negative
  anti-echo test favor better understanding of loops, not adoption of a
  threshold-based text filter.
- The briefing describes a plant closed six years ago, approximately
  **900** lost jobs, approximately **400** expected new hires, automation
  training, and **lower** wages/job count. Dialogue sometimes moves from
  justified fictional *personal recollection* to confident claims about
  shared events that neither briefing nor character profiles establish.

## Per-run qualitative rubric

| Dimension | Run 1 | Run 2 | Run 3 |
| --- | --- | --- | --- |
| **Responsive listening** | Mixed | Evident but often echoing | Mixed |
| **Distinctive voices beyond biography** | Mixed | Mixed | Mixed |
| **Unscripted disagreement / common ground** | Evident | Evident, then circular | Evident |
| **Substantive belief or relationship development** | Limited | Mixed: topic develops, positions less so | Mixed: bridge tangent; positions persist |
| **Freedom from repetition** | Limited | Limited | Mixed |
| **Convincing social ending** | Limited (technical natural end) | Limited (budget termination) | Mixed (technical natural end) |

### Run 1 — Conditional concession, then a near-duplicate

Arthur and Maya engage the concrete distinction between *training*
and *a living wage*. Arthur's turn 9 conditionally concedes that the new
factory **might** deserve a closer look if the checks are adequate:
a small but observable opening, not a conversion. Daniel's turn 10
substantially repeats his turn 8, undermining the impression of natural
development immediately before the technical natural ending.

Maya's repeated dad/retraining argument is relevant to her persona,
but she repeats the same angle instead of demonstrating new knowledge
of Arthur. Daniel claims he personally saw the new owner's automated
lines installed; the briefing does **not** establish this observation.

### Run 2 — Strongest live thread, weakest closure

Daniel introduces a question about whether workers remain protected
when sickness or injury strikes (turn 5). The group builds a
recognizable thread about medical costs through turns 6–15.
Arthur's concession to Daniel in turn 6 is responsive. However,
the exchange begins to **converge on repeating shared conclusions**
without discovery, and Daniel's turn 16 near-duplicates his turn 14.
The run hits the opportunity budget instead of finding an ending.

Maya's turn 4 mostly mirrors Arthur's preceding line, with little
new content. Arthur says **"ninety hundred jobs"** instead of the
briefing's approximately 900, a factual/linguistic error.
Daniel claims students built the factory's particular automated
lines last year; this is not established by the frozen news briefing.
Disagreements about health benefits and employer liability become
confident claims without sourced grounding.

### Run 3 — Successful tangent, but little lasting movement

Daniel proposes repairing a bridge instead of relying on the plant
reopening, and Arthur endorses that concrete alternative. This
demonstrates **topic freedom** and local common ground rather than
coordinator-scripted debate. Maya challenges the premise using her
father's experience. Arthur ends by returning to the wage problem
rather than revealing an enduring change of outlook. There is no
clear near-verbatim echo, but the final turns still circle around
pay and retraining. The bridge's supposed ongoing leaks are an
unestablished local detail, not a shared briefing fact.

Arthur's first spoken line is **"I'll sit out this one..."**—a line of
dialogue, not a recorded voluntary pass. Treating it as silence in
a qualitative review would overstate the evidence.

## Cross-run conclusions

**What works:** The three people can sustain coherent disagreement,
respond at least topically, sustain a tangent, and sometimes concede
a conditional point. Their core perspectives remain recognizable.

**What remains unproven:** That the characters independently notice
important changes in one another, make decisions based on relationships,
ask spontaneous meaningful questions, express distinctive attitudes
without recycling backstory, or develop *lasting* positions and trust.

**What may interfere:** Reply-dominated turn structure; scaffolded
acknowledgment language; recurring character biography as a substitute
for remembered individual knowledge; looped conclusions; and
fictional observations stated as if they were briefing-supported facts.

**Distinction from previous evaluations:** The original baseline
assessment reasonably called the dialogue promising. C1 adopts a
higher authenticity threshold: recognizable topical talk is not yet
evidence of credible interpersonal development. This is a refinement
of the evaluation question, not a claim that the original results
were technically invalid.

## Relationship-continuity evidence is currently insufficient

All three archived transcripts are fresh encounters. The M1 memory
assessment documents promising cross-topic recollection, but **the
underlying complete paired transcripts are not archived alongside that
assessment**. Thus we cannot independently score evolving
relationships from the C1 evidence set. Additionally, memories of
factory work or mills might also arise from the static personal
biographies; a reference to the factory alone is not sufficient
to demonstrate actual session-specific recollection.

## C1 next data collection — no intervention

Before editing prompts, personalities, floor scheduling, or memory
mechanisms, run **three fresh/continuing pairs** under the current
engine, each with its own output directory. Current
`--continuing` means factory briefing first, bridge briefing second,
with the same three characters and evidence-linked recollection.

Hold constant:
- `qwen3.5:9b`, `--no-think`, temperature 0.8, existing turn/budget
  settings and character versions.
- The same command and two fictional briefings across all three
  pairs. Archive and compare each pair's *actual* diagnostics and
  separately named transcript artifacts (the H1 output format).
- Verify the model digest against the archived baseline; if it
  differs, report it rather than claiming a controlled comparison.

Read each conversation without applying numeric reward
targets. Record exact utterances to answer:
1. Does a participant correctly refer to a **specific earlier
   utterance**, not just generic factory/mill biographical material?
2. Does recalling it change what they say to another person,
   rather than merely repeat it?
3. Does any participant change or qualify a stance in response
   to another character's new point?
4. Is there spontaneous curiosity, humor, surprise, or silence
   where appropriate—rather than stereotyped agreement?
5. Do loops or unsupported factual specifics impair plausibility?
6. Do the participants end when the topic feels finished, or
   merely because of coordinator thresholds/budgets?

**Important limitation:** This is a descriptive follow-up, **not**
a memory causal test, because the current CLI does not provide a
matched fresh bridge-only control. Do not claim memory caused a
difference without that comparison.

## Decision gate

Do **not** adopt an anti-echo filter, add a subjective memory layer,
alter floor protocol, or change prompting based on these three
archival samples alone. First inspect the three new session pairs.
If a single dominant issue recurs, preregister a one-variable
experiment against the frozen baseline. Otherwise retain the
existing architecture and focus on richer evaluation examples.

This C1 report is **documentation-only** and requests no production
code changes or new architectural commitments.

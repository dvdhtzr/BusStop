# C2 — Evidence-attribution guidance: six-conversation comparison

**Date:** 2026-11-15  
**Status:** Inconclusive for the preregistered primary question; **do not adopt as the default prompt**. Preserve the opt-in variant for comparison. No production code or prompt changes made as part of this evaluation.

## Sources and integrity

Analyzed the owner's locally generated `BusStop-C2-20261115-084708.zip`, containing three `control` and three `treatment` fresh bridge-only conversations. Each arm includes three session-specific transcript/diagnostic pairs and one SQLite database. The underlying private ZIP is **not** published to the repository.

All six sessions use the same local `qwen3.5:9b` digest `56671c2ab9385f9cfcb404638e32cd62d88e3501d44822208363c010179a3c90`, temperature `0.8`, thinking off, characters Arthur v1 / Maya v2 / Daniel v2, frozen bridge briefing ID `bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb`, protocol `floor-2`, and 18 opportunity / 36 call / two-attempt budgets.

The controlled difference is the context version and its single appended prompt passage:
- Control: `ctx-3-ending` (historical prompt).
- Treatment: `ctx-3-ending+c2-attribution-v1` (instruction not to invent another participant's previously shared history).

SQLite session/utterance/opportunity/attempt counts match the individual diagnostics. All six sessions have contiguous committed speech and are `FreshEncounter`, so **neither arm has earlier session recollections**. There was one control `EmptySpeak` model attempt, correctly not committed as speech. Remaining outcomes were `ValidSpeak` or `ValidPass`.

## Technical outcomes

| Arm | Run | Opportunities | Attempts | Utterances | Passes | End |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| Control | 01 | 18 | 19 | 16 | 2 | BudgetReached |
| Control | 02 | 18 | 18 | 13 | 5 | BudgetReached |
| Control | 03 | 18 | 18 | 11 | 7 | BudgetReached |
| **Control total** | **3** | **54** | **55** | **40** | **14** | **0/3 NaturalEnd** |
| Treatment | 01 | 11 | 11 | 9 | 2 | NaturalEnd |
| Treatment | 02 | 11 | 11 | 6 | 5 | NaturalEnd |
| Treatment | 03 | 14 | 14 | 10 | 4 | NaturalEnd |
| **Treatment total** | **3** | **36** | **36** | **25** | **11** | **3/3 NaturalEnd** |

Average spoken line length was similar: approximately **31.7 words control** and **30.4 words treatment** (word splitting). The treatment produced substantially **fewer utterances** and finished before the opportunity budget in all three runs.

**Do not interpret a technical `NaturalEnd` as a human-credible closure**: the coordinator uses consecutive passes. Different generative trajectories, not necessarily the new instruction, may explain the large differences in length and stopping with only three runs per arm.

## Evidence against the actual preregistered target

The primary target was **unsupported claims about another character's *previously shared* experiences or conversations**, not ordinary fictional improvisation.

On careful reading, **there is no clean, unambiguous target violation in either new arm**. Thus this batch cannot establish that the C2 instruction reduced its intended failure rate. The previous C1b experiment contained a clear invented reference to Arthur's father, but that earlier event is not repeated as a control positive here.

- **Control Run 01:** Arthur invents a recollection about **"my father's mill"**. This is his *own fictional backstory*, not automatically a C2-target violation. Maya's father and mill feature in her static profile, but making a new autobiographical claim about one's own father is allowed in the creative setting.
- **Treatment Run 01:** Arthur's question **"Your dad had to retrain?"** follows Maya's immediately preceding description of her father retraining. This reference is correctly grounded and plausibly inquisitive.
- **Treatment Run 03:** Maya volunteers that her father learned to weld. That detail is not in the static profile, but it is **her own fictional anecdote**, not an invented prior claim about Arthur or Daniel. Do not penalize it under C2.

Neither group has enough true target incidents to measure whether the instruction helps. **Avoid promoting an absence of clear incidents to a causal success.**

## Actual conversational quality

### Control Run 01 — topical energy, then exact loops

Maya raises a sensible question about whether a quicker safe bridge replacement is possible. Daniel claims to have **personally signed off on the construction timeline** and quotes a contractor's two-year foundation explanation; those are unsupported claims of public authority, not established by the frozen briefing.

The exchange devolves into two exact duplicate pairs: Maya repeats her earlier **"building something too narrow...my father's old mill wall"** line, and Arthur repeats **"My father's mill was closed down on purpose..."**. The run reaches the opportunity limit. This is a repetition failure, not necessarily an interpersonal-memory failure.

### Control Run 02 — reactions without scrutiny

Maya responds to the traffic and detour burdens; Daniel introduces schoolchildren allegedly **already missing three hours of school** because of the detour, even though the briefing says bridge closure begins in the future. Later Daniel says **"nothing beats rust like rust on the road that drops your kid off"**, a semantically incoherent response that other speakers do not interrogate. The run hits its budget despite five passes.

### Control Run 03 — circular speculation

The characters circle around span width, traffic flow, and construction specifications. Daniel reasons that a wider bridge necessarily means **fewer** cars can cross, without supporting that premise. Maya repeatedly warns that widening the road without moving barriers will create traffic jams. Seven passes fail to form the necessary consecutive ending sequence; the run reaches the budget.

### Treatment Run 01 — relevant follow-up, still improvised

Arthur's surprised **"Your dad had to retrain?"** is responsive to Maya, not an invented shared memory. Daniel relates a fictional shop-class welding accident about his *own students*: such personal creative invention remains permitted. He also asserts visible cracks in the bridge without source grounding. The conversation is relatively coherent, but stops after Daniel's safety argument rather than a genuinely negotiated conclusion.

### Treatment Run 02 — avoids loops, but thin

Arthur, Maya, and Daniel discuss inconvenience, safety, and students crossing. It ends after just six spoken turns. The final point acknowledges the need for safe routes but does not show sustained argument or evolving trust. This might be a reasonable short exchange or a loss of conversational energy; the sample cannot decide.

### Treatment Run 03 — serious world-time contradiction

Daniel describes **school juniors** who got lost on the **new detour** because they had allegedly **driven across the bridge for twenty years**. That is incompatible with their age and with the briefing's future closure timeline. Daniel then says the replacement would open in **eighteen months** rather than the stated two years, without explaining why. These are errors of world grounding and internal reasoning, not of recalling another participant's history.

Maya contributes her father's welding experience as a fictional personal anecdote. The conversation ends after Arthur disputes an analogy between learning welding and navigating a detour. It is not a convincing shared resolution.

## Rubric verdict

| Preregistered criterion | Read |
| --- | --- |
| Invented interpersonal memory | **Inconclusive:** no clear target event in either arm |
| Valid references to another person's actual remarks | **Present** (e.g. Arthur responds to Maya's father-retraining statement) |
| Creativity / individual voice | **Present in both**, often overreliant on standard mill and shop-class references |
| Reduced exact loops | **Observed descriptively**, not a result attributable to the intervention in this small pilot |
| Spontaneity / authenticity cost | **Possible:** treatment runs end sooner, sometimes with thin development |
| World-fact and temporal credibility | **Weak in both:** unsupported construction details; particularly implausible school timeline in treatment 03 |
| Natural endings | **Numerically higher in treatment**, but only the deterministic ending rule is verified |
| Model/integrity behavior | **Technically sound:** one rejected empty speech in control, no delivered model failures |

## Research decision

**Do not make C2 guidance the default.** The intended effect remains unproven; both arms can produce creative dialogue, and both can also generate implausible factual or temporal claims. Preserve the optional flag and the baseline. Do not tune the prompt repeatedly in response to these particular six samples.

**M1 memory stays intact. M2 subjective memory remains deferred.** Resist building more conversational mechanics until there is a repeatable, consequential problem with a clear test.

The more valuable next design milestone is the approved **actual conversation source** experience:

1. Randomly choose a **verifiably current free-news story** (with date, source, retrieval provenance, and a frozen rights-respecting briefing).
2. Or accept a **user-uploaded document**, ingested locally with bounded chunking, retrieval, and citations from source passages.
3. Keep public-document/source evidence separate from character biographies and interpersonal recollections.
4. Introduce a common, source-agnostic grounding contract first; use the existing simulated bridge and factory fixtures for backwards-compatible tests.
5. No source ingestor, retrieval tool, or new RAG architecture is authorized by this evaluation. Discuss the architecture and acceptance criteria before implementing.

Related project direction: [conversation source vision](../conversation-source-vision.md), already on `main` from PR #8.

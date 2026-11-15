# C2 — Evidence attribution in conversational relationships

**Status:** Prospective, opt-in, prompt-only experiment. No results yet.

## Hypothesis

C1/C1b transcripts revealed that characters sometimes confidently invent
**another character's experiences or a previous exchange**. This can create
false intimacy even when no memory is available. One narrowly targeted guidance
sentence may reduce such inventions while preserving spontaneity and believable
improvisation.

The change is **not** a general anti-hallucination or fact-checking feature,
a moderator, a text filter, a new agent, or a memory redesign.

## One experimental variable

- **Control:** existing `ctx-3-ending` prompt, exactly unchanged.
- **Treatment:** `ctx-3-ending+c2-attribution-v1`, where the existing
  competence guidance is followed by this *one appended passage*:

> You may draw on your own personal experiences naturally. When saying what another person previously told you or lived through, rely only on what they have actually shared in this conversation or in your recollections. If you do not know, ask instead of inventing shared history.

The experiment is **off by default**. `--c2-attribution` enables it.
The session's persisted `contextVersion` and diagnostics record the
variant. The existing `--fresh-bridge` option supplies independent
memory-off sessions for both groups.

Unchanged: `qwen3.5:9b`, model digest, temperature 0.8, `--no-think`,
character profiles, the same frozen bridge briefing, JSON response format,
floor coordinator, retry/budget policy, conversation history window, and
existing M1 recollection mechanism.

## Run after the PR is GREEN and merged

From a clean `main` checkout (GitHub branch is
`experiment/c2-evidence-attribution-guidance` while the PR is under review):

```powershell
dotnet build .\BusStop.sln -c Release
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore

$root = Join-Path $HOME ("Documents\BusStop-C2-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$control = Join-Path $root "control"
$treatment = Join-Path $root "treatment"

dotnet run --project .\src\BusStop.Console --configuration Release --no-build -- `
    --model ollama --ollama-model qwen3.5:9b --no-think `
    --fresh-bridge --runs 3 --out $control

if ($LASTEXITCODE -ne 0) { throw "C2 control failed" }

dotnet run --project .\src\BusStop.Console --configuration Release --no-build -- `
    --model ollama --ollama-model qwen3.5:9b --no-think `
    --fresh-bridge --c2-attribution --runs 3 --out $treatment

if ($LASTEXITCODE -ne 0) { throw "C2 treatment failed" }

Compress-Archive -Path (Join-Path $root '*') -DestinationPath "$root.zip"
Write-Host "C2 archive: $root.zip"
```

Use *different* output directories. Check every diagnostics JSON before
comparison: all bridge briefing IDs must match, all modes must be
`FreshEncounter`, and the only config distinction should be
`contextVersion`. Verify model digests, opportunity budgets, outcome
counts, and sequence integrity. The comparison is **three stochastic
samples per condition**—not paired by model seed and not powered for a
statistical claim.

## Human reading rubric, recorded before seeing results

Score **per attributable claim**, quoting the relevant turns:

1. **Invented shared memory:** Does someone claim a different participant
   previously said or experienced something absent from the available
   committed history, recollections, or approved profile? Fresh bridge
   controls have no prior shared encounter.
2. **Evidence-consistent reference:** Does a reference to another person
   correctly reflect what they actually shared? Don't treat references
   to static profile biography as evidence of cross-session memory.
3. **Authenticity cost:** Does the treatment sound guarded,
   overly formal, repetitive, or excessively question-driven?
4. **Responsiveness and voice:** Do speakers challenge one another's
   actual point and maintain recognizable, independent perspectives?
5. **Fictional imagination:** Can speakers still invent plausible
   *personal* experiences without treating their inventions as
   established public news facts or someone else's biography?
6. **Natural stopping:** Does an ending feel earned, not just satisfy
   the coordinator's two-pass threshold?
7. **Unrelated failures:** Record contradictions, wrong article claims,
   cross-speaker echoes, and thematic loops separately. Do not claim
   this targeted prompt was designed to fix them.

Precommit to considering the treatment **not adopted** if it merely
reduces explicit invented-history phrases but damages natural dialogue
or produces mostly evasive, formulaic qualifiers. No lexical automatic
penalty is an acceptable replacement for qualitative reading.

## Interpretation limits and stopping point

Even a promising difference in six conversations cannot prove a
generalizable improvement. If the evidence is mixed, leave the default
unchanged and preserve this as a negative or inconclusive experiment.
Do not tune further prompts mid-batch. A future memory-on replication
could test whether the same guidance improves attribution of actual
recall; **that is not part of this first C2 batch**.

## Relationship to the ultimate product

The fixed bridge story is a controlled fixture, **not the intended user
experience**. BusStop's destination is three user-chosen characters
discussing either a randomly selected story from today's free news or a
user-supplied document ingested through **local retrieval-augmented
generation (RAG)**. C2 isolates conversational behavior now so source
ingestion can change later without losing the baseline. See
[the conversation source vision](../conversation-source-vision.md).

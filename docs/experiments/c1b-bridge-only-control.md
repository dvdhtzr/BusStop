# C1b — Fresh bridge-only control

**Status:** Approved minimal lab-runner addition; no conversational intervention.
**Change:** `--fresh-bridge` runs the **existing frozen bridge briefing** as a
`FreshEncounter` without prior-session recollections.

This is a control cohort for the three existing continuing bridge conversations
from `BusStop-C1-20261115-072638.zip`. The previous archive is retained
separately and is **not** committed by this PR.

## Reproduce locally after the PR branch passes tests

```powershell
dotnet build .\BusStop.sln -c Release
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore

$out = Join-Path $HOME ("Documents\BusStop-C1b-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
dotnet run --project .\src\BusStop.Console --configuration Release --no-build -- `
    --model ollama --ollama-model qwen3.5:9b --no-think `
    --fresh-bridge --runs 3 --out $out

Compress-Archive -Path (Join-Path $out '*') -DestinationPath "$out.zip"
Write-Host "$out.zip"
```

**Keep the outputs distinct from the prior experiments.** Expect exactly
three sessions with `config.mode = "FreshEncounter"` and
`config.briefingId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"`.
Their contexts contain no prior-memory section, since the existing runner
supplies `RecollectionService` only for `Continuing` mode.
The session-ID-specific transcripts and diagnostics are written by H1.

## Unchanged controls

- Default with no new flag: the same factory-only fresh encounter(s).
- `--continuing`: the same factory fresh session then bridge continuing
  session, with evidence-linked recollection.
- Combining `--continuing` and `--fresh-bridge` is an error, not a
  silently reinterpreted experiment.

The model, digest, temperature, thinking, floor scheduling, turn budgets,
private contexts, characters, frozen briefing facts, and memory algorithm
remain unchanged. The new flag changes **only** the chosen story for fresh
sessions.

## Evaluation, not a reward target

Compare the three bridge-only controls with the three prior continuing
bridge sessions for:

1. Episode-specific reference to factory conversation details absent from
   the static profiles and the frozen bridge briefing.
2. Whether a reference affects how a speaker responds to *another person*,
   rather than just bringing the previous topic back.
3. Voice distinctiveness in short anonymized excerpts, without depending
   on familiar backstory words.
4. New claims that confuse fictional personal history with the public
   article, or contradict earlier utterances without reflection.
5. Thematic circling, cross-speaker copies, and whether silence feels
   contextually appropriate.
6. Human-credible closure, separately from the engine's termination label.

A **single pilot with three stochastic samples per group is not a
definitive causal estimate**. The groups were produced at different times,
without paired random seeds, and the prior continuing group followed actual
factory encounters. The comparison can expose qualitatively useful
differences or show that similar behavior arises without recollection.

**Gate:** Preserve raw transcripts and diagnostics. Review before any
changes to prompts, coordinator, models, or the memory architecture.

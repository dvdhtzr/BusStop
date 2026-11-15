# R1b — First document-grounded conversation

**Status:** Draft GitHub implementation; local Release build/tests and owner merge
approval required. R1b is NOT full RAG.

## What this milestone actually does

R1a already imports a bounded local UTF-8 `.txt` or `.md` document
and divides it into citable passages. R1b lets **Arthur, Maya and Daniel
discuss an excerpt of a real user-provided document** with their
existing local Ollama speaker/coordinator, without altering M1 memory
or the previous laboratory runs.

The source is frozen and persisted in the local SQLite conversation
database. Each discussion has a permanent `session_evidence` link
to a specific source snapshot and the exact passage IDs exposed to
the model.

For the first iteration, the engine receives **the first four
passages only**. Each passage is labeled with its stable source
citation ID. This is an explicit and deliberately limited bridge to
the current short-briefing interface, **not semantic retrieval**.
The rest of the document remains in local storage but is **not
visible to the participants** and cannot be retrieved by them
during R1b. Source passages are marked as untrusted quoted material,
not instructions for the agent to obey.

## User command after merging and verifying

From the BusStop repository root, assuming the FAA sample document
was downloaded earlier:

```powershell
dotnet build .\BusStop.sln -c Release
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore

$out = Join-Path $HOME ("Documents\BusStop-R1b-" + (Get-Date -Format "yyyyMMdd-HHmmss"))

dotnet run --project .\src\BusStop.Console --configuration Release --no-build -- `
    --model ollama --ollama-model qwen3.5:9b --no-think `
    --discuss-document "$HOME\Downloads\FAA_Ground_Stop_Nov_13_2026_BusStop_Sample.txt" `
    --out $out

if ($LASTEXITCODE -ne 0) { throw "Document discussion failed" }

Write-Host "Transcript: $out\transcript.md"
Write-Host "Diagnostics: $out\diagnostics.json"
```

The console and transcript will report the evidence source's
fingerprint and the **four visible passage IDs**. The diagnostics
includes a `sourceEvidence` section with `fullDocumentRetrieval:
false`, a session-specific `contextVersion`, and the same
model/call budget telemetry as existing runs.

Do **not** publish or automatically upload the generated SQLite
database unless you are comfortable sharing the complete document
text stored inside it. The transcript and diagnostics are enough
for a first conversational review.

## Constraints and verification

1. **Source frozen before the first model call.** Snapshot/passages
   are persisted and exact visible passage IDs are pinned to the
   session before any speaker receives a turn.
2. **Immutable re-capture.** Re-saving identical source and chunking
   is idempotent. Conflicting content or passage boundaries for an
   existing snapshot ID must fail closed with no mutation.
3. **Session association never silently changes.** Pinning
   disallows unauthorized passage IDs, duplicate IDs, and repinning
   after the conversation starts.
4. **Explicit partial context.** Models are told how many of the
   source's passages they have seen, and warned not to claim to
   have read the rest. The transcript identifies the same passages.
5. **Same conversation engine.** The three-character identities,
   speech protocol, M1 and optional C2 guidance, and frozen baseline
   laboratory scenarios remain available. R1b document mode is a
   separate opt-in entry point; it cannot be mixed with a
   continuing session or C2 prompt experiment.
6. **Untrusted documents.** The quoted text does not confer
   permission to alter agents or invoke tools. Prompt instructions
   are only a defense in depth; they do not establish that models
   will always disregard malicious embedded content.
7. **Private by default.** Local full text stays in the local
   SQLite database and is never committed as part of the PR.
   The model sees at most the four selected passages.

This is a meaningful qualitative acceptance test: do the characters
discuss **the supplied material** in their own voices, and do they
avoid inventing content beyond the passages? Even an apparently
successful discussion does **not** establish reliable grounding
or retrieval completeness.

## Next milestone: actual RAG

R1c should let participants retrieve passages on demand, using
their current question and conversational context, with bounded
result counts, source citations, authorization boundaries and
retrieval diagnostics. Start with a transparent deterministic
lexical retriever, evaluate it against real questions from uploaded
documents, and add local embedding/semantic retrieval when measured
coverage makes the need clear. PDF/DOCX import and news discovery
remain separate implementation gates.

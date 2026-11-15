# R1c — Per-turn local lexical RAG for document conversation

**Status:** Draft implementation; requires local GREEN and owner approval to merge.
**Scope:** Local-first, bounded, transparent *lexical* retrieval. This is
working retrieval-augmented generation but not embedding-based semantic search.

## Why

R1b successfully started Arthur/Maya/Daniel talking about the owner's
FAA shutdown document, but exposed only the first four of its nine
source passages in a fixed briefing. The resulting dialogue was
repetitive and made some unsupported factual leaps. R1c gives speakers
a changing, inspectable selection of source evidence as their dialogue
moves, while leaving the coordinator and M1 recollection untouched.

## Experimental variant

- `--discuss-document`: unchanged **R1b** static four-passage baseline.
- `--rag-document`: new **R1c** local retrieval. No fixed source passages
  in its initial briefing.
- Both retain the same local Ollama model, three participants,
  temperature, speaking-floor budgets, and ability to decline the
  floor. The R1c model receives *at most three* labeled source
  passages per speaking opportunity.

A query is formed deterministically from the **source title and last
three committed utterances** (each capped at 240 characters). No
additional LLM planning call, private participant biography, uncommitted
candidate speech, or network request is involved. The transparent
retriever scores word overlap with inverse-document-frequency
weighting, selects the top two matches plus one previously unseen
passage when available, and imposes a strict maximum of three per
turn. This exploratory slot is a deliberately simple way to provide
coverage even if the characters repeat a narrow topic.

**Limits:** Lexical matching has no semantic synonym understanding,
can still retrieve irrelevant evidence, and has no guarantee that
all nine passages will be seen in any particular conversation. It is
not an automatic truth verifier. The quoted text remains untrusted
content, never executable operating instructions.

## Durable visibility contract

R1c uses the same frozen source snapshot as R1a/R1b, identified by
its content SHA-256 and snapshot identity. A session is pinned to its
source before any turn, but has zero **static** exposed passages.
Instead each speaking opportunity records **before inference**:

- the offering opportunity ID and intended speaker;
- the pinned source/snapshot ID;
- retrieval algorithm version and bounded query text;
- the exact source passage IDs and relevance scores.

These records survive reopening the SQLite database. A record
cannot be rewritten with new passages, a different source, a
different speaker, or a different query. It cannot be created after
inference has begun. Source pinning cannot change once the session
starts. The engine rejects a dynamic-retrieval session resumed
without the corresponding evidence provider.

The transcript lists retrieved passage IDs next to each utterance;
the diagnostics retain **all** retrieval events, including passes
and failed attempts, in opportunity order. The model only receives
passage text selected for that particular turn. This is
observability of *what was available*, not proof that the speaker
based every claim on that evidence.

The new per-turn table is created with `CREATE TABLE IF NOT EXISTS`
and does not modify existing R1b snapshot or conversation tables.
Existing local databases should remain readable.

## Local tests and real acceptance

From a clean working tree on the feature branch:

```powershell
dotnet build .\BusStop.sln -c Release
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
```

Once those are GREEN, run an isolated FAA-document retrieval
conversation against local Ollama:

```powershell
$out = Join-Path $HOME ("Documents\BusStop-R1c-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
dotnet run --project .\src\BusStop.Console --configuration Release --no-build -- `
    --model ollama --ollama-model qwen3.5:9b --no-think `
    --rag-document "$HOME\Downloads\FAA_Ground_Stop_Nov_13_2026_BusStop_Sample.txt" `
    --out $out
if ($LASTEXITCODE -ne 0) { throw "R1c conversation failed" }
Write-Host "$out\transcript.md"
Write-Host "$out\diagnostics.json"
```

The SQLite file stores **the complete private document**. For
qualitative review, share only `transcript.md` and
`diagnostics.json` unless explicitly comfortable sharing the
full original source.

## Acceptance and qualitative comparison

Confirm:
1. Source fingerprint matches the original FAA document; context
   version is `ctx-3-ending+doc-rag-lexical-v1`.
2. Each model attempt has a matching, previously persisted
   retrieval event. Every event's source matches the session pin,
   and its passage IDs belong to the frozen document.
3. Every turn sees at most three source passages. Diagnostics show
   whether any `p00005`–`p00009` were reached; later paragraphs
   should be accessible as the topic develops, but coverage is
   not guaranteed in a short run.
4. Uncommitted attempts never become spoken utterances. The
   coordinator's ending and retry budgets are unchanged.
5. Characters engage distinctive source information (FAA
   maintenance, passenger impacts, uncertainty) more often than
   the R1b baseline without becoming mere quote readers.
6. Identify unsupported claims, false shared memories, repeated
   analogies, and naturalness separately from retrieval metrics.

**Do not adopt success from a technical coverage count alone.**
Review whether the characters actually use the relevant passage.
Preserve a negative result honestly; lexical RAG is a baseline for
eventually evaluating local vector search.

## What remains for later review

- Embedding-based or hybrid lexical + semantic ranking with locally
  installed embedding models, if the measured retrieval baseline
  shows a need; no cloud inference.
- User-configurable document/personality selection and UI.
- More local document types such as PDF/DOCX, with extraction and
  citation fidelity tests.
- **Today's news**: freely available rights-eligible feeds,
  validated publication timestamps, randomized selection,
  and provenance; this remains a separate implementation slice.

This PR is **not** permission to change M1, implement M2, merge
the evaluation-report PR, or expand the news ingestion surface.

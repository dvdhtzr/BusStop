# Real content — R1a: Common evidence snapshots

**Status:** First implementation slice. This is local ingestion and source
identity, **not** document-grounded conversation, semantic retrieval, news
fetching, a production RAG system, or a replacement for the existing Briefing.

## Why first

The current engine stores a short immutable `Briefing` that all three
characters read. It does not distinguish a news publisher's article from a
user's longer source document or keep citable passages. Expanding that
engine directly would mix evidence access with the speaking-floor and
interpersonal-memory behavior already evaluated.

R1a adds a **source-neutral, non-executable evidence snapshot** alongside
the existing system. No inference path changes; no agent sees new
document text yet.

## Contract

`EvidenceSourceDescriptor` records:
- `NewsArticle` or `UserDocument`;
- human-readable title, source name, and explicit rights declaration;
- canonical HTTPS URL and publication timestamp **required for news**;
- null public URL for a user document (private file paths never appear
  in the recorded source identity).

`EvidenceSnapshot` contains:
- deterministic `SnapshotId` (SHA-256 over stable metadata and text digest);
- SHA-256 over the exact captured UTF-8 text;
- capture timestamp, full captured text, and numbered passages.

`EvidencePassage` contains:
- stable citation key `<snapshotId>:p00001` etc.;
- original UTF-16 offset and length;
- verbatim text. Passage windows have an overlap, preserve original
  ordering and character positions, and do not silently omit content.

The factory can capture **already-obtained permitted news text** using
the same contract as local documents. It deliberately does **not**
claim to verify a news site's copyright/license terms or fetch an
article. A rights declaration records the caller's assessment, not
an automatic permission grant.

## First working local input

`LocalTextDocumentImporter` supports **UTF-8 .txt and .md** up to
1 MiB. It rejects nonexistent, linked, invalid-encoding, blank,
NUL-containing, unsupported, and oversized files. It reads local
bytes only; no remote services, cloud uploads, or Ollama calls.

Try it from your Windows repository after GREEN and merge:

```powershell
dotnet run --project .\src\BusStop.Console --configuration Release -- `
    --inspect-document "C:\path\to\your-notes.md"
```

The command prints title, source/text fingerprints, character and
passage counts, and the first citation ID. **It does not print or upload
document contents, start a conversation, or create a durable snapshot
database.** The contract and the parser have model-free regression tests.

## Architectural invariants

1. **Factual evidence is not conversational memory.** Shared authorized
   source passages must not be stored as a participant's remembered
   personal statement.
2. **Frozen source version.** Re-ingesting changed text generates new
   identifiers instead of silently editing the source under existing
   citations. Importing identical content and metadata gives stable
   citation IDs (regardless of capture time).
3. **Document text is untrusted content.** Any instructions inside a
   source are evidence *to discuss*, never host rules, permission grants,
   or model operating instructions.
4. **Private by default.** No file contents or absolute local file
   path are sent to GitHub as part of document inspection. Tests use
   synthetic local fixtures.
5. **No quiet truncation.** Oversized or unreadable documents fail
   rather than being summarized or truncated without disclosure.
6. **Do not change conversation behavior.** R1a does not modify
   `BusStop.Engine`, `BusStop.Context`, the coordinator, memory
   retrieval, original prompts, or the two fictional baseline briefings.

## Follow-on slices (separate review gates)

- **R1b — Durable evidence and document conversation:** persist the
  frozen snapshot/passages, pin the evidence version to a session,
  and let a user-selected short document become a real conversation
  topic without losing source provenance. Do not pretend the entire
  document is available if the model gets only an excerpt.
- **R1c — Actual local RAG:** bounded passage retrieval by query or
  current conversational context, evidence-backed citations and
  isolation, then semantic/vector retrieval with a local embedding
  model if measured retrieval quality warrants it. Support larger
  documents, more formats (PDF/DOCX), and retrieval diagnostics.
- **R2 — Today's news:** discover rights-eligible free news sources,
  verify publication time with an explicit timezone, randomly choose
  a current article, freeze permitted evidence, and use the same
  interface. Human/user override should be possible.
- **R3 — User experience:** connect both sources to the chosen
  characters and the bus-stop interface, preserve inspectability
  and live conversation controls.

Only R1a is in the current PR. Each later slice needs clear design,
tests and user-approved GREEN before merge.

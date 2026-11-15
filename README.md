# BusStop

Three fictional people sit together at a bus stop, receive common source material
from a news story or document, and talk. What follows should emerge from their individual
personalities and their responses to one another — not from a script.

This is a local-first, multi-agent conversational experiment. The objective is a
transcript that reads like three distinct individuals having an unscripted
conversation, evaluated separately for conversational quality and for technical
integrity.

**Intended product:** Let the user start a conversation from either a randomly
selected story from **today's news** or an uploaded document understood through
**local RAG retrieval**. The current plant and bridge stories are *fictional,
frozen test fixtures*, not real news. R1a captures local source evidence; R1b
introduces bounded document discussions. Actual on-demand RAG and live
news selection remain future work. See
[conversation source vision](docs/conversation-source-vision.md).

## Status

**Phase 1 — Minimal Conversation Engine.** The smallest engine that can run a real
conversation and prove its integrity: three fixed characters, one frozen briefing,
one local model behind an interface, isolated per-participant contexts, a
deterministic coordinator, voluntary speak-or-pass, and a durable, replayable
transcript. Console runner only.

Out of scope for Phase 1: memory retrieval, news browser, any frontend, per-character
models.

## Design

The architecture and the Phase 1 implementation plan live in the project's design
documents (Phase 0 architecture and Phase 1 plan). Key principles:

- The coordinator is deterministic code; it allocates speaking opportunities, never content.
- Silence is a first-class, recorded outcome, never a model failure reinterpreted as a choice.
- Commit before delivery: no utterance is heard until it is validated and durably committed.
- State flows through committed utterances, so one shared model cannot leak private state.

## Toolchain

- C# / .NET 8 (LTS)
- SQLite for persistence
- A local model (Ollama) behind `IModelClient`, exercised only in the acceptance project

## Real content — first source milestone

R1a introduces a common evidence snapshot for future **today's-news**
and **user-document RAG** inputs. It can already inspect a private UTF-8
`.txt` or `.md` file locally, producing a stable content fingerprint
and citation-ready passage identifiers:

```powershell
dotnet run --project .\src\BusStop.Console -- --inspect-document "C:\path\to\notes.md"
```

This mode makes **no Ollama call** and does **not** yet start a
document-grounded conversation or perform RAG retrieval. See
[the R1a design and test boundary](docs/real-content/r1a-evidence-snapshots.md).
The existing conversation engine, prompts, and fictional lab scenarios
remain unchanged.

### R1b: Discuss a local document with Ollama

```powershell
dotnet run --project .\src\BusStop.Console --configuration Release -- `
    --model ollama --ollama-model qwen3.5:9b --no-think `
    --discuss-document "C:\path\to\notes.md" --out "C:\path\to\result"
```

This gives Arthur, Maya and Daniel **only the first four citable
passages** of that file. It stores the full snapshot in local SQLite
and pins the exposed passages to the conversation; the transcript and
diagnostics identify what was visible. **It does not retrieve further
passages or perform RAG.** See
[the R1b acceptance boundary](docs/real-content/r1b-document-discussion.md).

## Building

```bash
dotnet build
dotnet test
```

The model-free test suite (`BusStop.Tests`) is the technical gate and requires no
model. The real-model probe (`BusStop.Acceptance`) is separate and non-gating.

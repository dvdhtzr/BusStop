# V2 — Explicit character anchors, versioned history and scoped evidence

**Status:** GitHub draft PR / local GREEN pending.
**Baseline:** `main` after PR #14 (merge `d10273606d67bcc72d0f27d026e8ef096f4ee4b3`).
**Design:** `BusStop_Evidence_Grounded_Verifier_V1-V7_Specification.md` §§6–7, 14 (V2).

## Implemented, isolated and model-free

- `CharacterAnchor`: typed facts authored explicitly for a **specific character/profile version**. Persona prose is never converted into binding facts. An unsealed or absent set is **empty**, not a prohibition on new memories. An explicitly sealed empty set is supported.
- `SealVerifierAnchorSet`: host-owned authoring operation requiring an owner-review reference; anchors are immutable for that profile version and cannot be newly added after a version has been pinned. New character versions can have new sets without rewriting existing versions.
- `PinVerifierCharacterVersion`: opt-in sidecar profile pin before the **first** opportunity in a session. Nothing is inferred for legacy/unpinned sessions. This does **not** alter the profile the participant actually sees.
- `CharacterHistoryEvent`: a separate, append-only autobiography index anchored to an **actual committed utterance by that subject**. Provisional entries can be added for new self-claims; reviewed self-reaffirmations and explicit self-corrections require an earlier event and an owner-review reference. The host confirms speaker, exact committed span, predecessor identity, key, chronology and no branching. Old versions are never deleted.
- Retrieval creates V1 `VerificationEvidenceScope` and an explicit `EvidenceCompleteness` for each claim category. Source facts use the session briefing and **only passages actually pinned for exposure**. Own identity/history uses explicitly pinned self anchors and time-available reviewed/provisional self-history. Dialogue attribution uses preceding committed turns and earlier **ended same-cast sessions**.
- Retrieval validates the actual committed target utterance, identity, speaker, original boundary, original text hash, source/passage integrity and bounded selection; it cannot promote a search miss into an exhaustive negative claim.
- A room-shareable anchor is *eligible to be disclosed*, not proof it was sent to another agent. Today the participant context does **not** deliver other-character anchor content, so V2 returns **no other-character anchor values at all**, even when marked shareable. A later explicit exposure event is required before that may change.

### Limitations deliberately retained

- An owner-review reference is a required host-supplied authorization marker, **not cryptographic proof of human review**. Only the trusted owner API is permitted to call the reviewed-event authoring operation; an eventual owner UI/auth workflow will need to enforce this boundary explicitly. No model has a tool to author anchors or approve promotion.
- Reaffirmation semantics are **not** certified by storing two committed utterances. Owner review must compare their meaning; the host validates provenance and chronology. Ambiguous equivalence stays provisional until reviewed. No automatic semantic promotion occurs in V2.
- `VerificationEvidenceScope` still proves scope, not factual truth or contradiction. No Ollama calls, verification jobs, V3 packet assembly, V4 semantic judgments, V5 ledger recovery or live V6 integration exist in this PR.
- This is an **independent private sidecar**. No changes are made to `ConversationEngine`, `ParticipantContextBuilder`, `RecollectionService`, existing M1 moderators, session policies, source pins, or speech commits.

## Future compatibility: participant-initiated research (not implemented in V2)

The owner would like agents eventually to fetch factual sources as needed to support their dialogue, beginning potentially with **Wikipedia**. V2 is designed not to foreclose that, but this is a **separate, later architecture milestone**, not a hidden extension of shadow verification.

A future research flow should have a distinct participant-originated, host-governed tool request; explicit allowed-source policy, rate/call/token budgets, and protections against source-borne instructions; an immutable source capture with attribution (e.g., Wikipedia page ID/revision, canonical URL, retrieval time, license/rights metadata, content hash); and an append-only **per-recipient exposure event** proving which agent actually received which excerpt at what session sequence/time. The agent may cite only its received sources, and V3+ verification may use those exact authorized snapshots at the original utterance boundary. Later fetches **must never retroactively validate or contradict** an earlier speech event merely by having been retrieved.

Future dynamic exposures should be additive, separately versioned records, **not a mutation of immutable initial `session_evidence` pins**. New `EvidenceSourceKind` values can be versioned (Wikipedia is not a news article). Private verifier findings still must never flow into participant prompts; the participant's own permitted research results are a separate input surface that would require explicit architecture approval. Wikipedia content and citations are evidence with provenance, not automatically established truth.

## V2 owner-local acceptance

```powershell
git status --short
git fetch origin
git switch feature/moderator-v2-anchors-history-retrieval
git pull --ff-only origin feature/moderator-v2-anchors-history-retrieval
git rev-parse HEAD
dotnet build .\BusStop.sln -c Release
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
```

Tests specifically cover additive schema initialization, empty legacy anchors, immutable owner seals and pins, shareability versus actual speaker exposure, available source passages only, source/turn visibility, self-owned committed history, reviewed transitions, non-retroactive corrections, identical-cast dialogue, and partial retrieval coverage.

The GitHub authoring environment cannot establish a Windows .NET GREEN result. PR remains draft and **unmerged** until owner-local GREEN and explicit merge approval. V3 is not started.

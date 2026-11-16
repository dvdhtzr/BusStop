# V1 — Evidence-verifier contract boundary (model-free)

**Status:** implementation PR for owner-local verification; not a live feature.
**Parent:** PR #13 merged into `main` at `d952b8f3e5ea3f186434799b5d250240c0f389ed`.
**Design baseline:** `BusStop_Evidence_Grounded_Verifier_V1-V7_Specification.md`, sections 5 and 14 (V1).

## What V1 adds

- A distinct `BusStop.Moderation.Verification` contract family, `evidence-verifier-v1`.
- `VerificationSessionPolicy.Disabled` as the default for the **future** verifier, plus a shadow-only policy shape. This type is intentionally **not persisted or wired into CreateSession** in V1. It cannot enable enforcement.
- Host-bound job identity and SHA-256 of the original committed utterance.
- Indexed UTF-16 claim spans with exact-substring validation and explicit rhetorical/category/provenance classification.
- Typed evidence items with source version/hash, authority, speaker visibility, as-of boundary and correction linkage.
- Host-issued, exact-item evidence scopes; completeness is about the declared search only.
- Canonical JSON evidence packet with SHA-256; `Freeze` detaches item lists from caller-owned mutable arrays.
- Independent epistemic outcomes (`ConfirmedContradiction`, `NoMaterialIssue`, `InsufficientEvidence`, `ConflictingEvidence`) and operational statuses (including transport, format and budget failures).
- Strict validation for identity, versions, packet integrity, authorized citation membership and bounded responses; count consistency checks for future host summaries.

## Trust and compatibility

- **No change** to `ConversationEngine`, `ConversationStore`, SQLite schema, M1 worker, participant prompts, floor scheduling, utterance commit, participant memory or existing M1 audit.
- `shadow-m1-v1` and `moderation-m0-v1` keep their current meanings. Historical M1 outcomes are not reclassified.
- The host must construct `VerificationEvidenceScope` from records the speaker was allowed to see **at the original boundary**. V1 validates a host-issued scope; V2 must implement the retrieval and authorization rules that produce it.
- An authorized citation proves only that the cited record belongs to the permitted packet. It **does not** prove the claim contradicts it. Semantic correctness needs the later V4 evaluator and V6/V7 owner-reviewed laboratory.
- A missing autobiographical statement is **not** a contradiction. This contract does not infer binding anchors from free-form persona text.
- `NoMaterialIssue` is not a proof of truth. An incomplete search cannot prove a universal negative. Failed/budget-exhausted requests never become positive or accepted outcomes.
- All inputs from source material and models remain untrusted data.

## Versioning and limits

- Contract: `evidence-verifier-v1`; serializer: `stj-evidence-packet-v1`.
- Packet content is serialized in declared record-property order using System.Text.Json; lists preserve host-selected order, and no map is serialized. SHA-256 is calculated over the UTF-8 bytes of that exact JSON, in lowercase hexadecimal. The packet envelope ID is intentionally outside the content hash.
- The V1 contract rejects packets above 32 evidence records, 8,192 UTF-16 characters per excerpt, or 131,072 serialized UTF-8 bytes. These are **contract safety caps**, not final V3 retrieval/token-budget settings; changing serialization or contract semantics requires versioning.
- Only `Disabled` and `Shadow` are valid verifier modes. The policy admits at most three per-utterance inference attempts **including retries**. Actual pre-dispatch reservations and session ceilings will be implemented in V4/V5, not simulated in V1.
- API records are immutable-by-convention; `Freeze` copies the evidence collection. Recovery and ledger persistence are deferred to V5.

## Local GREEN gate

From a clean checkout of `feature/moderator-v1-evidence-contracts`:

```powershell
git fetch origin
git switch feature/moderator-v1-evidence-contracts
git pull --ff-only origin feature/moderator-v1-evidence-contracts
git status --short
dotnet build .\BusStop.sln -c Release
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
```

Tests include: invalid/forged spans; mismatched utterance/packet/claim IDs; packet-hash tampering; unauthorized or altered evidence; future-boundary evidence; wrong model verdict citations; incomplete search metadata; status/outcome separation; summary integrity; and shadow-only policy validation. Existing M1 tests continue to run unchanged.

**No local Windows build, test, Ollama experiment, merge, or semantic GREEN is claimed by the authoring environment.** V1 stays unmerged pending owner's reported GREEN, code review and explicit merge approval. V2 requires its own design/gate.

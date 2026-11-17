# BusStop V6b — Isolated Local Ollama Semantic Evaluation Laboratory

**Status:** Draft, unmerged, requires owner Windows GREEN. **Parent:** merged V6a PR #21 at `21243e6e990cadc5fb9b06cd3273a92baf5c0aa6`.
**Approved direction:** V6 semantic-calibration laboratory in the Evidence-Grounded Verifier V1–V7 specification, separate from V6c shadow runtime integration and V7 release-readiness.

## What V6b is (and is not)

V6b exercises the local Ollama model on the **20 synthetic, provisional V6a cases** using the exact `V4GroundedVerifier.BuildPrompt` formatter, the actual `V4JudgmentParser.JsonSchema`, and the authoritative V4 parser / V1 packet/judgment validator. The only runtime change is making the existing prompt formatter `public static` so the isolated lab can reuse it without duplicating or silently drifting its instructions. The prompt contents and production V4 behavior are unchanged.

Each synthetic case becomes one synthetic committed utterance, one host-validated exact-span claim, and one frozen V1 packet containing only the case's explicitly authorized synthetic evidence. The synthetic session/opportunity/job/claim/packet IDs are deterministically derived from the original corpus SHA and case ID. Repeated runs therefore use **byte-identical V4 prompts and packet hashes**, not randomly changing inputs. Source kind/authority, visibility, source IDs, packet SHA-256, as-of boundary, rhetorical lane, and claim identity are all supplied. **The model sees neither provisional expectedOutcome nor expected citations nor owner rationale.** Cases with no authorized evidence remain Unknown completeness, not false certainty.

**Critical limitation:** Synthetic scopes are laboratory-generated, not obtained through actual V2 persisted source retrieval. V6b bypasses production claim selection/extraction, the V4 automatic no-call rhetorical/unknown-scope shortcuts, and the V5 shared session inference ledger because it is a separate manual component probe. Its synthetic scores do not establish live end-to-end detection quality or budget correctness. V6c will address real shadow selection and drop telemetry separately.

## Execution constraints

- The standalone `tests/BusStop.V6.OllamaLab` executable must be launched by a user, never by the conversation engine, its startup, xUnit's default test suite, or a background scheduler.
- Ollama is pinned to `127.0.0.1:11434` with **no remote endpoint option**, explicit model name and required full digest. No cloud fallback or network retrieval.
- Temperature 0.1, think false, 2,048 generated tokens, 8,192 context tokens, four-minute per-call timeout.
- At most **three runs** per explicit invocation, **20 cases per run**, and **one charged LOCAL model invocation per selected case**. There is no model retry, request replay, or refund after a timeout/invalid verdict. The local probe does NOT charge production V4 reservations, and must never be interpreted as production inference budget accounting.
- Ollama transport errors, timeouts, exceptions, invalid JSON, truncation, wrong IDs, packet/hash mismatches, unauthorized references, and inappropriate categorical authority are recorded as operational failures. Safe aggregate diagnostics distinguish host evidence-scope rejection, malformed structure, and truncation from actual epistemic outcomes. An invalid response **never** becomes `NoMaterialIssue` or a successful model verdict.
- No automatic enforcement, participant memory write, news fetch, external biography search, corpus relabeling, holdout exposure or threshold-based promotion.

## Outputs: deliberately private vs shareable

**Private** file per run (`data/v6b-private/<run-id>.private.json`): case IDs, full fictional claim/evidence, production-formatted prompt, raw model response when present, V4 host-validated claim judgment, concise error category, model telemetry and charged local call count. **Treat this file as private; do not commit or post it publicly.** Even fictional sources may become sensitive in future evaluations, so the private boundary must remain enforced.

**Safe aggregate** file per run (`data/v6b-safe/<run-id>.safe.json`): input corpus hash/version, provisional status, model/digest, aggregate outcome confusion, coverage, status categories, false positives, detection/precision, citation authorization vs provisional citation relevance, and category/rhetorical/scenario-group aggregates. It omits raw prompts, source passages, claim text, per-case IDs, full model JSON, and private paths.

On multiple runs, safe drift output records only comparable completed-case count, outcome-flip count and operational/selection-change count—not case IDs or model internals. The CLI rechecks the local model digest before each run and aborts on changed tags. All reports have `AcceptanceAllowed=false`. The V6a labels are provisional development diagnostics, **not human-reviewed reference truth**. A model that mirrors all 20 provisional labels still does not pass an acceptance gate.

Local `.gitignore` covers both report directories. An explicit output path can still be outside those defaults; if so, take care not to commit it. The CLI refuses identical/nested safe and private directories and uses `FileMode.CreateNew` to avoid overwriting existing audits.

## Windows build/test GREEN gate (model-free)

```powershell
cd C:\Users\dvdht\source\repos\BusStop
git status --short
git fetch origin
git switch feature/moderator-v6b-local-ollama-lab
git pull --ff-only origin feature/moderator-v6b-local-ollama-lab
git rev-parse HEAD

dotnet build .\BusStop.sln -c Release
if ($LASTEXITCODE -ne 0) { throw 'Solution build FAILED' }
dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Regression tests FAILED' }
dotnet build .\tests\BusStop.V6.OllamaLab\BusStop.V6.OllamaLab.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'V6b lab build FAILED' }
dotnet build .\tests\BusStop.V4.OllamaProbe\BusStop.V4.OllamaProbe.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'V4 probe build FAILED' }
Write-Host 'V6b LOCAL BUILD/TEST GREEN' -ForegroundColor Green
```

`VerifierV6bLocalLabTests` are model-free scripted-model tests: exact V4 prompt/schema validation, valid strict host-verified synthetic contradiction, malformed/truncated/transport/exception failures, no retry, capped call count, original unselected denominators, true same-model-digest drift, no raw text in safe exports and pinned digest requirement.

## Optional actual local Ollama smoke

This is a **separate, explicit semantic probe**, not part of the default xUnit GREEN gate. With the local `qwen3.5:9b` model available:

```powershell
dotnet run --project .\tests\BusStop.V6.OllamaLab\BusStop.V6.OllamaLab.csproj -c Release --no-build -- `
  --model qwen3.5:9b `
  --corpus .\docs\moderator\corpus\v6a-synthetic-provisional-v1.json `
  --private-dir .\data\v6b-private `
  --safe-dir .\data\v6b-safe `
  --runs 2 --cases 4
if ($LASTEXITCODE -ne 0) { throw 'V6b Ollama smoke FAILED' }
```

That smoke makes up to eight **actual local model calls**; a full `--runs 2 --cases 20` experiment makes up to forty, no retries. Check the private audit for semantic problems and compare the safe per-category/rhetorical aggregates. A zero exit code means the manual tool ran and wrote its files—not that the model reached semantic acceptance thresholds.

## Outstanding next gates

V6c will separately implement opt-in, bounded post-commit shadow scheduling and explicit no-selection/dropped/queue-coverage visibility. V7 requires independently frozen owner-reviewed development labels, an unseen owner-reviewed holdout, predefined thresholds, multiple independent natural sessions and explicit approval. V6b makes no claims of that readiness.

**Do not merge PR on authoring-only source inspection.** The GitHub authoring environment cannot run the owner's Windows .NET/Ollama; local build, tests and audit review are separate gates. Fix failures on this branch, then request explicit owner merge approval.
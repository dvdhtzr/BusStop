# M1b controlled moderator challenge laboratory

**Status:** Experimental, isolated, shadow-only. Not an authorization to merge PR #13 or enable enforcement.

## What is tested

- 36 completely synthetic fictional-airport cases: 18 permitted statements (including four near-repetitions), 14 expected violations, and four exact copies (two cross-speaker, two self-copies).
- Labels are provisional, requiring owner review. The fictional airport incident is not a real historical event and is not the private FAA audit.
- The runner uses production M1a BuildModeratorPrompt, JsonSchemaFor, ShadowResponseParser, ModerationContractValidator and TryBuildExactCopy. There is no separate test prompt.
- No conversation engine, SQLite, participant inference, recollections, enforcement, nudges, or modifications to committed speech.
- Calls only local Ollama, moderator temperature 0.1, think false, context 8192, output 768. At most one retry for recoverable malformed responses; total hard ceiling 72 calls.
- Exact copies are determined in host code with zero model calls. Raw responses and telemetry are saved only to a private directory outside the repository.

## Local verification

From a clean feature/moderator-m1-shadow branch, pull the latest commit then run:

    .\scripts\evaluate-m1b-challenges.ps1

This runs Release build, all tests, and corpus validation without any Ollama calls.

For a short balanced live run of two allowed, two positive, two near-duplicate and two exact-copy cases:

    .\scripts\evaluate-m1b-challenges.ps1 -Live -Limit 8

After inspecting the smoke results, evaluate all 36 cases:

    .\scripts\evaluate-m1b-challenges.ps1 -Live -Limit 36

Private reports are written under $HOME\Documents\BusStop-M1b-* with individual model responses, failure explanations, evidence scopes and completion metadata. Check them before sharing.

## Metric definitions

- TP: expected finding (including deterministic exact copy), predicted finding.
- FP: expected allowed, predicted finding.
- TN: expected allowed, predicted no finding.
- FN: expected finding, predicted no finding.
- Unscorable: malformed, unauthorized, transport failure, budget exhausted, or error. Never counted as a TN or FN.
- Precision = TP/(TP+FP) when defined.
- RecallLowerBound = TP / all expected positive cases in the selected subset, including unscorable positives.
- FalsePositiveRate = FP / all expected allowed cases in the selected subset, with unscorable allowed cases reported separately.
- Deterministic exact-copy TP are reported separately and do not count toward model reasoning ability.

An always-Accept model fails all 14 nonexact positive cases. Host validity proves shape and evidence-scope authorization, NOT truth of the model's explanation.

## Promotion boundaries

Owner reviews the 36 provisional labels, local Release verification is GREEN, a complete 36-case local Ollama run has zero unscorable outputs, and non-null findings must be useful and reference only authorized evidence. Repeated evaluations are required before discussing promotion. No automated merges or enforcement. Stagnation nudges belong to another gate; four near repetitions are allowed in the M1b corpus.

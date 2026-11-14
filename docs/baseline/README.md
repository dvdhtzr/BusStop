# Baseline archive

Each archived baseline lives in a dated subdirectory named `<date>-<model-digest-prefix>/`, containing:

- `batch.md` — all transcripts plus the summary table, as produced by the runner.
- `diagnostics.json` — the run fingerprint (config, outcomes, integrity flags).
- `evaluation.md` — the human read against the questions in `../baseline-protocol.md`.

A baseline is immutable once archived. A change to any pinned value in the protocol produces a new
baseline in a new subdirectory; it never overwrites or reinterprets an existing one.

# FAA shadow corpus v1

This 33-record manifest was derived from the owner's private FAA audit. It stores only ordinal, candidate SHA-256 and *provisional assistant* labels. **No labels are owner-confirmed ground truth.** The original private JSON and SQLite database must not be committed to this public repository.

`Allow`: protected metaphor, opinion, question, or plausible self-authored anecdote. `ExactCopyOther` and `ExactCopySelf`: mechanically verifiable duplicates. `NeedsHumanReview`: disputed history/factual attribution, excluded from quality scoring pending review.

The evaluator verifies source session, label count, ordering and candidate-text SHA-256. An existing historical audit cannot prove new runtime behavior; rerun the live shadow laboratory after local GREEN to measure changes. The audit does not include every original model prompt or Ollama generation-completion field, so identical-prompt replay is not possible from this JSON alone.

# BusStop — conversation source vision

**Status:** Approved product goal. R1a adds source-neutral evidence
snapshots and local text-document inspection; **live news selection,
document-grounded conversations, and RAG retrieval remain unimplemented**.
This note keeps the research and source-ingestion work aligned with
the intended experience.

## The intended encounter

The user chooses three fictional characters, approves their personas,
and seats them on a shared bus stop bench. Their discussion should
emerge from the characters' perspectives and what they learn from
one another, not from a script.

The topic can originate in either of two user-facing ways:

1. **Today's news:** BusStop can automatically pick **a randomly
   selected story from news published today** through an eligible,
   freely accessible source, with a user-directed selection/override
   where appropriate. Capture the publication date, source identity,
   canonical URL, and a frozen, rights-respecting factual briefing.
   "Today" must use an explicit news-source date and time zone, rather
   than assuming the article is current because the feed was fetched
   today. Do not invent a live story, its text, or its publication date.
2. **User-supplied document:** The user provides a document, which is
   parsed **locally**, segmented into bounded, attributed chunks, and
   indexed/retrieved through **local RAG techniques**. As the exchange
   shifts, participants can retrieve relevant passages with stable
   citations/provenance, instead of treating a short initial summary
   as the entire contents of a long document. The document is evidence
   for discussion, not an instruction source that can alter agent
   permissions, goals, identities, or rules.

These paths should produce a common **conversation evidence contract**:
source identifiers, title/description, origin/date where known, immutable
snapshot/version, an initial common-ground briefing, and inspectable
attribution for facts asserted from the source. Retrieval can enrich
a participant's view over time but must not silently change the
original document or news record.

**Key separation:** Factual source retrieval and autobiographical
conversation recollection are not the same kind of memory.
All three participants may discuss the same authorized source;
each character's private profile, subjective impressions, and
session history retain their distinct visibility boundaries.

The system should preserve the right to disagree, speculate, joke,
ask questions, and tell fictional personal stories. Grounding
requirements apply to *claims presented as established source facts*;
they do not prohibit plausible fictional imagination.

## Sequence

- **Now:** Qualify authentic conversation using fixed, explicitly
  fictional factory and bridge briefing fixtures.
- **C2:** Test one opt-in instruction against invented interpersonal
  history. No news/RAG infrastructure is introduced.
- **R1a (first real-content slice):** Source-neutral provenance,
  stable passage citations, and local UTF-8 document inspection.
- **R1b / R1c:** Durably pin evidence to conversation sessions and
  implement local document-grounded retrieval, with further format
  support and measured retrieval quality.
- **R2:** Design and implement real-time, rights-eligible news selection
  with verified publication dates, provenance, local privacy,
  prompt-injection boundaries, and accurate source citations.

The goal is **credible relationships and conversations grounded in
interesting, user-relevant material**, not an endless series of
experiments about two canned news stories.

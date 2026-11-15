namespace BusStop.Domain;

/// <summary>A single thing a participant recalls from a previous conversation. Evidence-linked: it carries
/// the exact prior utterance it came from (<see cref="SourceSession"/>, <see cref="SourceUtterance"/>), so
/// what the participant remembers is always traceable to what was actually said. For milestone 1 a
/// recollection is purely factual — the content is the committed line itself; a participant's subjective
/// interpretation of it is a later milestone.</summary>
public sealed record Recollection(
    CharacterId Owner,
    SessionId SourceSession,
    UtteranceId SourceUtterance,
    long SourceSequence,
    CharacterId Speaker,
    string Content);

namespace BusStop.Domain;

/// <summary>One character's subjective sense of another, formed from a past conversation. Distinct from a
/// <see cref="Recollection"/>: a recollection is a fact (what was said); an impression is an interpretation
/// (what the owner makes of the subject). It is private to the <see cref="Owner"/> — never visible to the
/// subject or anyone else — and evidence-linked to the <see cref="SourceSession"/> it was formed in.</summary>
public sealed record Impression(
    CharacterId Owner,
    CharacterId Subject,
    SessionId SourceSession,
    string Content,
    DateTimeOffset FormedAt);

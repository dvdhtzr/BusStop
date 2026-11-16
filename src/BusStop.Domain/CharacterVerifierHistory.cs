namespace BusStop.Domain;

/// <summary>Who may use an owner-approved anchor as verification evidence.
/// SubjectOnly is NEVER made available to other participants.</summary>
public enum CharacterAnchorExposure
{
    SubjectOnly,
    SharedWithRoom
}

/// <summary>
/// An explicitly owner-authored, version-specific binding fact.
/// No Persona text is parsed into anchors. The owner-approved anchor set for a
/// (CharacterId, Version) is immutable, including an intentionally empty set.
/// </summary>
public sealed record CharacterAnchor(
    CharacterId Character,
    int ProfileVersion,
    string Key,
    string Value,
    CharacterAnchorExposure Exposure);

/// <summary>
/// History is only a provenance-bearing account of what this fictional speaker
/// said about themselves, never external truth. Promotion and correction require
/// independently approved review; model interpretation cannot grant authority.
/// </summary>
public enum CharacterHistoryEventKind
{
    Provisional,
    ReviewedSelfReaffirmation,
    ReviewedSelfCorrection
}

public sealed record CharacterHistoryEvent(
    Guid EventId,
    CharacterId Subject,
    string Key,
    string Statement,
    SessionId SourceSession,
    UtteranceId SourceUtterance,
    long SourceSequence,
    CharacterHistoryEventKind Kind,
    Guid? PreviousEventId,
    string? OwnerReviewReference,
    DateTimeOffset RecordedAt);

/// <summary>Versioned, as-of verifier history view, never participant memory.</summary>
public sealed record CharacterHistoryView(
    CharacterHistoryEvent Event,
    bool IsSuperseded,
    Guid? SupersededBy,
    bool IsEstablished);

namespace BusStop.Domain;

/// <summary>A user-approved fictional identity. The core identity is stable; only a new
/// <see cref="Version"/> represents an edited profile.</summary>
public sealed record CharacterProfile(
    CharacterId Id,
    int Version,
    string Name,
    string Persona,
    IReadOnlyList<string> Traits);

/// <summary>The frozen, source-grounded briefing all three participants receive. Immutable for
/// a given session.</summary>
public sealed record Briefing(
    BriefingId Id,
    string Title,
    string Source,
    string Url,
    string Body);

/// <summary>A conversation session. Model and protocol configuration are fixed once the session
/// starts so that a transcript is reproducible and is never reinterpreted by later code.</summary>
public sealed record Session(
    SessionId Id,
    IReadOnlyList<CharacterId> Participants,
    SessionMode Mode,
    BriefingId Briefing,
    string ProtocolVersion,
    string ModelConfigVersion,
    string ContextVersion,
    SessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt);

/// <summary>An offer of the floor to one participant. <see cref="CommittedBoundary"/> is the
/// committed-utterance sequence visible when the offer was made, so the decision can be tied to
/// exactly what the participant could have heard.</summary>
public sealed record SpeakingOpportunity(
    OpportunityId Id,
    SessionId Session,
    CharacterId IntendedParticipant,
    OpportunityReason Reason,
    long CommittedBoundary,
    OpportunityStatus Status);

/// <summary>A participant's structured, validated decision for one opportunity, linked to the
/// model attempt that produced it. Addressee/RepliesTo are only meaningful when the action is
/// Speak.</summary>
public sealed record ParticipantDecision(
    OpportunityId Opportunity,
    CharacterId Participant,
    DecisionAction Action,
    CharacterId? Addressee,
    UtteranceId? RepliesTo,
    AttemptId Attempt);

/// <summary>A committed utterance: the authoritative record of something a participant said.
/// <see cref="Sequence"/> is strictly increasing and unique within a session.</summary>
public sealed record Utterance(
    UtteranceId Id,
    SessionId Session,
    CharacterId Speaker,
    long Sequence,
    string Text,
    CharacterId? Addressee,
    UtteranceId? RepliesTo,
    DateTimeOffset CommittedAt);

/// <summary>A record of one model call. The raw request/response are retained privately elsewhere;
/// this is the durable, inspectable classification of what happened, including retry lineage.</summary>
public sealed record ModelAttempt(
    AttemptId Id,
    OpportunityId Opportunity,
    CharacterId Participant,
    string ModelName,
    string ModelDigest,
    int ProtocolVersion,
    AttemptOutcome Outcome,
    AttemptId? RetryOf,
    DateTimeOffset At);

namespace BusStop.Domain;

/// <summary>Whether a session starts clean or resumes an existing relationship.</summary>
public enum SessionMode
{
    FreshEncounter,
    Continuing
}

/// <summary>Durable session lifecycle status. Transient coordinator states (directed-reply
/// handling, ending checks) are not persisted here; they are derived while a session is Active.</summary>
public enum SessionStatus
{
    Created,
    Opening,
    Active,
    Paused,
    Ended
}

/// <summary>What a participant chose to do with a speaking opportunity.</summary>
public enum DecisionAction
{
    Speak,
    Pass
}

/// <summary>Why the coordinator offered an opportunity to a participant.</summary>
public enum OpportunityReason
{
    Opening,
    DirectedReply,
    FairOrder
}

/// <summary>Lifecycle of a single speaking opportunity.</summary>
public enum OpportunityStatus
{
    Offered,
    Decided,
    Expired
}

/// <summary>The classified outcome of one model call. These are mutually exclusive and every
/// one is persisted. The distinction between <see cref="ValidPass"/> and every failure class
/// is the integrity line of the system: a failure must never be recorded as a voluntary pass.</summary>
public enum AttemptOutcome
{
    ValidSpeak,
    ValidPass,
    InvalidSchema,
    EmptySpeak,
    Timeout,
    TransportError,
    Cancelled,

    /// <summary>Well-formed speech that merely repeats the speaker's own recent line. It is suppressed
    /// rather than delivered. Not a failure of the model call and not a voluntary silence; its own class.</summary>
    EchoedSelf
}

/// <summary>Coarse category an <see cref="AttemptOutcome"/> falls into.</summary>
public enum OutcomeCategory
{
    Spoke,
    Silent,
    Failed,

    /// <summary>Valid output that was deliberately withheld (e.g. a self-echo), distinct from a technical
    /// failure and from a voluntary silence.</summary>
    Suppressed
}

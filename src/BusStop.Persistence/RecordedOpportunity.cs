using BusStop.Domain;

namespace BusStop.Persistence;

/// <summary>The outcome an opportunity reached, as derived from durable records during replay.</summary>
public enum ReplayOutcome
{
    Spoke,
    Passed,
    Failed
}

/// <summary>A recorded opportunity with its derived outcome, used to rebuild coordinator state on
/// resume. A would-be speak whose commit never landed appears here as <see cref="ReplayOutcome.Failed"/>,
/// never as speech.</summary>
public sealed record RecordedOpportunity(
    OpportunityId Id,
    int Ordinal,
    CharacterId Participant,
    OpportunityReason Reason,
    ReplayOutcome Outcome,
    UtteranceId? Utterance,
    CharacterId? Addressee);

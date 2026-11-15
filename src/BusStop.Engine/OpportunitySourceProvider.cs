using BusStop.Domain;

namespace BusStop.Engine;

/// <summary>Optional source-retrieval boundary. Its implementation must persist
/// exactly what was retrieved for this opportunity BEFORE returning it to the
/// engine for the model prompt. Each participant receives only their own
/// turn's bounded source passage set.</summary>
public interface IOpportunitySourceProvider
{
    TurnSourceEvidence Retrieve(
        SpeakingOpportunity opportunity,
        IReadOnlyList<Utterance> committedHistory);
}

public sealed record TurnSourceEvidence(IReadOnlyList<EvidencePassage> Passages);

using BusStop.Domain;
using BusStop.Engine;
using BusStop.Persistence;
using BusStop.Sources;

namespace BusStop.Console;

/// <summary>R1c console adapter for on-turn local lexical source retrieval.
/// Only committed public speech and source title form its query. All passages
/// returned to the engine are saved under the speaking opportunity first.</summary>
public sealed class LocalOpportunitySourceProvider : IOpportunitySourceProvider
{
    private readonly ConversationStore _store;
    private readonly EvidenceSnapshot _snapshot;

    public LocalOpportunitySourceProvider(ConversationStore store, EvidenceSnapshot snapshot)
    {
        _store = store;
        _snapshot = snapshot;
    }

    public TurnSourceEvidence Retrieve(
        SpeakingOpportunity opportunity,
        IReadOnlyList<Utterance> committedHistory)
    {
        var previous = _store.GetOpportunityRetrievals(opportunity.Session);
        var seen = previous.SelectMany(record => record.PassageIds)
            .ToHashSet(StringComparer.Ordinal);

        // No model planner or extra inference calls. As new turns are
        // committed, retrieval follows the words actually spoken.
        var recent = committedHistory.OrderBy(u => u.Sequence).TakeLast(3)
            .Select(u => u.Text.Length <= 240 ? u.Text : u.Text[..240]);
        var query = string.Join(" ", new[] { _snapshot.Descriptor.Title }.Concat(recent));
        if (query.Length > LocalLexicalRetriever.MaxQueryCharacters)
            query = query[..LocalLexicalRetriever.MaxQueryCharacters];

        var result = LocalLexicalRetriever.Retrieve(_snapshot, query, seen);
        _store.SaveOpportunityRetrieval(
            opportunity, _snapshot.SnapshotId,
            result.Version, result.Query, result.Passages, result.Scores);

        return new TurnSourceEvidence(result.Passages);
    }
}

using BusStop.Domain;
using BusStop.Persistence;

namespace BusStop.Memory;

public sealed record RecollectionOptions(int MaxRecollections = 4);

/// <summary>Builds a participant's evidence-linked recollections for a continuing session. Retrieval is
/// deterministic, bounded, inspectable, and participant-specific: it draws only from prior conversations
/// among the same people (which that participant was therefore part of and heard), and selects an even
/// spread across that history rather than the most recent lines, with a small rotation so repeated
/// continuations surface different memories rather than looping on the same ones.</summary>
public sealed class RecollectionService
{
    private readonly ConversationStore _store;

    public RecollectionService(ConversationStore store) => _store = store;

    public IReadOnlyList<Recollection> Recall(
        CharacterId participant,
        IReadOnlyList<CharacterId> roomParticipants,
        SessionId currentSession,
        RecollectionOptions? options = null)
    {
        var opts = options ?? new RecollectionOptions();
        var priorSessions = _store.FindPriorSessions(roomParticipants, currentSession);
        if (priorSessions.Count == 0) return Array.Empty<Recollection>();

        var heard = new List<Utterance>();
        foreach (var s in priorSessions)
            heard.AddRange(_store.GetCommittedUtterances(s));

        // Recall the situation and what OTHERS said — not a transcript of one's own lines. Surfacing a
        // participant's own words back to them tempts verbatim self-repeat in the continuing conversation.
        heard = heard.Where(u => u.Speaker != participant).ToList();

        // Rotate selection by how many prior conversations there have been, so a second or third
        // continuation does not surface the same spread as the first.
        var selected = SelectSpread(heard, opts.MaxRecollections, priorSessions.Count - 1);

        return selected
            .Select(u => new Recollection(participant, u.Session, u.Id, u.Sequence, u.Speaker, u.Text))
            .ToList();
    }

    /// <summary>Selects at most <paramref name="max"/> items as an even spread across the list, starting at
    /// a rotation offset. Pure and deterministic, so retrieval is testable in isolation.</summary>
    public static IReadOnlyList<T> SelectSpread<T>(IReadOnlyList<T> items, int max, int offset)
    {
        if (max <= 0) return Array.Empty<T>();
        if (items.Count <= max) return items.ToList();

        var stride = items.Count / max;           // >= 1 because items.Count > max
        var start = stride <= 0 ? 0 : offset % stride;
        if (start < 0) start += stride;

        var picked = new List<T>();
        for (var i = start; i < items.Count && picked.Count < max; i += stride)
            picked.Add(items[i]);
        return picked;
    }
}

using BusStop.Domain;

namespace BusStop.Coordinator;

public sealed record CoordinatorOptions(int PingPongWindow = 4, int? EndAfterConsecutivePasses = null);

/// <summary>Why a conversation ended on its own terms. Budget and observer stops are engine-level
/// terminations, kept separate so a natural ending is never confused with hitting a ceiling.</summary>
public enum EndReason
{
    NaturalEnd
}

public abstract record CoordinatorStep;

/// <summary>An offer of the floor. <see cref="ReplyTo"/> is set only for a directed reply.</summary>
public sealed record Offer(CharacterId Participant, OpportunityReason Reason, UtteranceId? ReplyTo) : CoordinatorStep;

public sealed record ConversationEnded(EndReason Reason) : CoordinatorStep;

/// <summary>Deterministic speaking-floor policy. It offers opportunities and reacts to their outcomes;
/// it never decides content. After an opening round it prioritises a directed reply, overrides that
/// priority to break two-party ping-pong, otherwise offers the least-recently-offered participant, and
/// ends only after a full quiet round followed by a silent ending-check round.</summary>
public sealed class FloorCoordinator
{
    private readonly IReadOnlyList<CharacterId> _participants;
    private readonly int _pingPongWindow;
    private readonly int _endThreshold;

    private readonly Queue<CharacterId> _opening;
    private readonly Dictionary<CharacterId, long> _lastOffered;
    private readonly List<CharacterId> _recentSpeakers = new();
    private long _tick;
    private int _quietStreak;
    private (CharacterId Addressee, UtteranceId Utterance)? _pendingReply;

    public FloorCoordinator(IReadOnlyList<CharacterId> participants, CoordinatorOptions? options = null)
    {
        if (participants.Count == 0) throw new ArgumentException("A conversation needs participants.", nameof(participants));
        var opts = options ?? new CoordinatorOptions();
        _participants = participants;
        _pingPongWindow = opts.PingPongWindow;
        _opening = new Queue<CharacterId>(participants);
        _lastOffered = participants.ToDictionary(p => p, _ => 0L);
        // End after one full round of consecutive passes: when everyone declines in turn, the
        // conversation has run its course. (Two full rounds proved unreachable for talkative models.)
        _endThreshold = opts.EndAfterConsecutivePasses ?? participants.Count;
    }

    public CoordinatorStep Next()
    {
        // The opening phase gives every participant one initial, declinable opportunity.
        if (_opening.Count > 0)
            return OfferTo(_opening.Dequeue(), OpportunityReason.Opening, null);

        // A full quiet round followed by a silent ending-check round is a natural end.
        if (_quietStreak >= _endThreshold)
            return new ConversationEnded(EndReason.NaturalEnd);

        if (_pendingReply is { } pending)
        {
            if (FairnessOverrideActive())
            {
                // Drop directed priority to break two-party ping-pong; fair order gives the floor
                // to the least-recently-offered participant, which is the one being shut out.
                _pendingReply = null;
            }
            else
            {
                _pendingReply = null;
                return OfferTo(pending.Addressee, OpportunityReason.DirectedReply, pending.Utterance);
            }
        }

        return OfferTo(FairPick(), OpportunityReason.FairOrder, null);
    }

    public void RecordSpoke(UtteranceId utterance, CharacterId speaker, CharacterId? addressee)
    {
        _quietStreak = 0;
        _recentSpeakers.Add(speaker);
        _pendingReply = addressee is { } a ? (a, utterance) : null;
    }

    public void RecordPassed(CharacterId participant)
    {
        _quietStreak++;
    }

    /// <summary>A technical failure is neither speech nor a voluntary pass, so it does not advance the
    /// conversation toward a natural ending. The engine bounds retries and total calls.</summary>
    public void RecordFailure(CharacterId participant)
    {
    }

    private Offer OfferTo(CharacterId participant, OpportunityReason reason, UtteranceId? replyTo)
    {
        _lastOffered[participant] = ++_tick;
        return new Offer(participant, reason, replyTo);
    }

    private CharacterId FairPick() =>
        _participants.OrderBy(p => _lastOffered[p]).First();

    private bool FairnessOverrideActive()
    {
        if (_participants.Count <= 2) return false;
        if (_recentSpeakers.Count < _pingPongWindow) return false;

        var window = _recentSpeakers.Skip(_recentSpeakers.Count - _pingPongWindow).ToList();
        var distinct = window.Distinct().ToList();
        if (distinct.Count != 2) return false;

        // Override only if a participant is being excluded from the recent exchange.
        return _participants.Any(p => !distinct.Contains(p));
    }
}

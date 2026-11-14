using BusStop.Context;
using BusStop.Coordinator;
using BusStop.Domain;
using BusStop.Models;
using BusStop.Persistence;

namespace BusStop.Engine;

public sealed record EngineOptions(
    int MaxOpportunities = 60,
    int MaxModelCalls = 120,
    int MaxAttemptsPerOpportunity = 2,
    int ProtocolVersion = 1,
    CoordinatorOptions? Coordinator = null);

/// <summary>Why a run stopped. A natural end and a reached budget are distinct, so hitting a ceiling
/// is never reported as the conversation concluding on its own.</summary>
public enum TerminationReason
{
    NaturalEnd,
    BudgetReached
}

public sealed record RunResult(TerminationReason Reason, int Opportunities, int ModelCalls);

/// <summary>Orchestrates a session. It enforces commit-before-delivery (nothing a participant hears is
/// ever anything but a committed utterance read back from the store), bounds inference, records every
/// attempt with its outcome, and rebuilds coordinator state from durable records so a crashed session
/// resumes exactly once.</summary>
public sealed class ConversationEngine
{
    private readonly ConversationStore _store;
    private readonly IModelClient _model;
    private readonly ParticipantContextBuilder _builder;
    private readonly SessionId _session;
    private readonly IReadOnlyList<ParticipantHandle> _handles;
    private readonly IReadOnlyDictionary<CharacterId, CharacterProfile> _profiles;
    private readonly Briefing _briefing;
    private readonly EngineOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _trace;

    private readonly FloorCoordinator _coordinator;
    private int _opportunityOrdinal;
    private int _modelCalls;

    public ConversationEngine(
        ConversationStore store,
        IModelClient model,
        ParticipantContextBuilder builder,
        SessionId session,
        IReadOnlyList<CharacterProfile> participants,
        Briefing briefing,
        EngineOptions? options = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? trace = null)
    {
        _store = store;
        _model = model;
        _builder = builder;
        _session = session;
        _briefing = briefing;
        _options = options ?? new EngineOptions();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _trace = trace;
        _handles = participants.Select(p => new ParticipantHandle(p.Id, p.Name)).ToList();
        _profiles = participants.ToDictionary(p => p.Id);

        _coordinator = RebuildCoordinator(out _opportunityOrdinal);
    }

    /// <summary>Rebuilds coordinator state by replaying the session's recorded opportunities in order.
    /// For a fresh session there are none, so this starts a clean opening. For a resumed one it applies
    /// each recorded outcome, treating an uncommitted would-be speak as a failure that was never said.</summary>
    private FloorCoordinator RebuildCoordinator(out int nextOrdinal)
    {
        var coordinator = new FloorCoordinator(_handles.Select(h => h.Id).ToList(), _options.Coordinator);
        var recorded = _store.GetRecordedOpportunities(_session);

        foreach (var rec in recorded)
        {
            // Advancing the coordinator keeps its internal ordering aligned with what was recorded.
            _ = coordinator.Next();
            switch (rec.Outcome)
            {
                case ReplayOutcome.Spoke:
                    coordinator.RecordSpoke(rec.Utterance!.Value, rec.Participant, rec.Addressee);
                    break;
                case ReplayOutcome.Passed:
                    coordinator.RecordPassed(rec.Participant);
                    break;
                default:
                    coordinator.RecordFailure(rec.Participant);
                    break;
            }
        }

        nextOrdinal = recorded.Count;
        return coordinator;
    }

    public RunResult Run(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_opportunityOrdinal >= _options.MaxOpportunities || _modelCalls >= _options.MaxModelCalls)
            {
                _store.UpdateSessionStatus(_session, SessionStatus.Ended, _clock());
                return new RunResult(TerminationReason.BudgetReached, _opportunityOrdinal, _modelCalls);
            }

            var step = _coordinator.Next();
            if (step is ConversationEnded)
            {
                _store.UpdateSessionStatus(_session, SessionStatus.Ended, _clock());
                return new RunResult(TerminationReason.NaturalEnd, _opportunityOrdinal, _modelCalls);
            }

            var offer = (Offer)step;
            RunOpportunity(offer, cancellationToken);
            _opportunityOrdinal++;
        }
    }

    private void RunOpportunity(Offer offer, CancellationToken cancellationToken)
    {
        var opportunityId = OpportunityId.New();
        var boundary = _store.MaxSequence(_session);
        var opportunity = new SpeakingOpportunity(
            opportunityId, _session, offer.Participant, offer.Reason, boundary, OpportunityStatus.Offered);
        _store.SaveOpportunity(opportunity, _opportunityOrdinal);

        var self = _profiles[offer.Participant];
        _trace?.Invoke($"… {self.Name} has the floor ({offer.Reason}); thinking…");
        var history = _store.GetCommittedUtterances(_session);
        var built = _builder.Build(new ContextInputs(self, _handles, _briefing, history, opportunity));

        var validationContext = new ValidationContext(
            _handles.ToDictionary(h => h.Name, h => h.Id),
            offer.Participant,
            built.VisibleUtteranceIds);

        // Bounded inference. Each attempt is recorded with its own outcome and retry lineage.
        ValidatedDecision decision = new(AttemptOutcome.TransportError, null, null, null, null);
        AttemptId? previous = null;
        AttemptId lastAttempt = default;

        for (var i = 0; i < _options.MaxAttemptsPerOpportunity; i++)
        {
            var response = _model.Decide(new ModelRequest(offer.Participant, built.Prompt), cancellationToken);
            _modelCalls++;
            decision = DecisionValidator.Validate(response, validationContext);

            var attempt = new ModelAttempt(
                AttemptId.New(), opportunityId, offer.Participant,
                _model.ModelName, _model.ModelDigest, _options.ProtocolVersion,
                decision.Outcome, previous, _clock());
            _store.SaveAttempt(attempt);
            previous = attempt.Id;
            lastAttempt = attempt.Id;

            if (!OutcomeClassification.IsFailure(decision.Outcome))
                break;
        }

        switch (decision.Outcome)
        {
            case AttemptOutcome.ValidSpeak:
                // Commit before delivery: the utterance becomes visible to others only once this
                // returns, because every context is read back from committed utterances.
                var repliesTo = decision.RepliesTo ?? offer.ReplyTo;
                var utterance = _store.CommitUtterance(
                    opportunityId, _session, offer.Participant, decision.Text!, decision.Addressee, repliesTo, _clock());
                _store.SaveDecision(new ParticipantDecision(
                    opportunityId, offer.Participant, DecisionAction.Speak, decision.Addressee, repliesTo, lastAttempt));
                _coordinator.RecordSpoke(utterance.Id, offer.Participant, decision.Addressee);
                _trace?.Invoke($"  {self.Name}: {decision.Text}");
                break;

            case AttemptOutcome.ValidPass:
                _store.SaveDecision(new ParticipantDecision(
                    opportunityId, offer.Participant, DecisionAction.Pass, null, null, lastAttempt));
                _coordinator.RecordPassed(offer.Participant);
                _trace?.Invoke($"  {self.Name} stays quiet.");
                break;

            default:
                // A failure after bounded retries: no utterance, no decision, not a pass.
                _coordinator.RecordFailure(offer.Participant);
                _trace?.Invoke($"  {self.Name}: [no valid response — {decision.Outcome}]");
                break;
        }

        _store.UpdateOpportunityStatus(opportunityId, OpportunityStatus.Decided);
    }
}

using BusStop.Moderation;
using BusStop.Moderation.Verification;
using BusStop.Context;
using BusStop.Coordinator;
using BusStop.Domain;
using BusStop.Memory;
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
    BudgetReached,

    /// <summary>No one ever spoke: the conversation never started, as opposed to concluding.</summary>
    NoEngagement
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
    private readonly RecollectionService? _recollections;
    private readonly SessionMode _mode;
    private readonly ModerationSessionPolicy _moderationPolicy;
    private readonly IShadowModerationSink? _shadowSink;
    private readonly IShadowModerationSink? _verifierSink;

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
        Action<string>? trace = null,
        RecollectionService? recollections = null,
        IShadowModerationSink? shadowSink = null,
        IShadowModerationSink? verifierShadowSink = null)
    {
        _store = store;
        _model = model;
        _builder = builder;
        _session = session;
        _briefing = briefing;
        _options = options ?? new EngineOptions();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _trace = trace;
        _recollections = recollections;
        _mode = store.GetSession(session)?.Mode ?? SessionMode.FreshEncounter;
        _moderationPolicy = store.GetModerationPolicy(session);
        // M1 observes committed speech asynchronously. Enforcement remains forbidden.
        if (_moderationPolicy.Mode is not (ModerationOperatingMode.Disabled or ModerationOperatingMode.Shadow))
            throw new InvalidOperationException("Enforcement is not supported before Gate A.");
        if (_moderationPolicy.Mode == ModerationOperatingMode.Shadow && shadowSink is null)
            throw new InvalidOperationException("Shadow sessions need an explicit nonblocking observer.");
        if (_moderationPolicy.Mode == ModerationOperatingMode.Disabled && shadowSink is not null)
            throw new InvalidOperationException("A disabled session cannot observe private candidates.");
        _shadowSink = shadowSink;
        // V6c is independently opted in by a V4 policy PINNED before the first
        // speaking opportunity. It never reuses or replaces M1's sink.
        var verifierPolicy = store.GetV4VerifierPolicy(session);
        if (verifierShadowSink is not null &&
            verifierPolicy?.Mode != VerificationOperatingMode.Shadow)
            throw new InvalidOperationException(
                "V6c shadow observer requires an explicit pinned V4 shadow verifier policy.");
        if (verifierPolicy?.Mode == VerificationOperatingMode.Shadow &&
            verifierShadowSink is null)
            throw new InvalidOperationException(
                "An opted-in V6c session requires its private nonblocking verifier sink.");
        _verifierSink = verifierShadowSink;
        _handles = participants.Select(p => new ParticipantHandle(p.Id, p.Name)).ToList();
        _profiles = participants.ToDictionary(p => p.Id);

        _coordinator = RebuildCoordinator(out _opportunityOrdinal);
        // Total inference budget is a property of the durable session, not this process.
        _modelCalls = _store.GetAttempts(_session).Count;
    }

    /// <summary>Rebuilds coordinator state by replaying the session's recorded opportunities in order.
    /// For a fresh session there are none, so this starts a clean opening. For a resumed one it applies
    /// each recorded outcome, treating an uncommitted would-be speak as a failure that was never said.</summary>
    private FloorCoordinator RebuildCoordinator(out int nextOrdinal)
    {
        var coordinator = new FloorCoordinator(_handles.Select(h => h.Id).ToList(), _options.Coordinator);
        var recorded = _store.GetRecordedOpportunities(_session);

        var expectedOrdinal = 0;
        foreach (var rec in recorded)
        {
            // Refuse incompatible/corrupt replay rather than silently rewriting history
            // under a different speaker or scheduling policy.
            var next = coordinator.Next();
            if (rec.Ordinal != expectedOrdinal || next is not Offer expected ||
                expected.Participant != rec.Participant || expected.Reason != rec.Reason)
                throw new InvalidOperationException(
                    $"Incompatible recorded opportunity at ordinal {rec.Ordinal}: replay refused.");
            expectedOrdinal++;
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

    private static string Snippet(string? raw) =>
        raw is null ? "(no body)" : (raw.Length > 400 ? raw[..400] + "…" : raw).Replace("\n", " ").Replace("\r", " ");

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
            if (step is ConversationEnded ended)
            {
                _store.UpdateSessionStatus(_session, SessionStatus.Ended, _clock());
                var reason = ended.Reason == EndReason.NoEngagement
                    ? TerminationReason.NoEngagement
                    : TerminationReason.NaturalEnd;
                return new RunResult(reason, _opportunityOrdinal, _modelCalls);
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

        // In a continuing session, the speaker recalls evidence-linked lines from prior conversations
        // among the same people. Fresh encounters carry no recollections.
        IReadOnlyList<Recollection>? recollections = null;
        if (_mode == SessionMode.Continuing && _recollections is not null)
            recollections = _recollections.Recall(offer.Participant, _handles.Select(h => h.Id).ToList(), _session);

        var built = _builder.Build(new ContextInputs(self, _handles, _briefing, history, opportunity, recollections));

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
            // The remaining budget is checked on EVERY attempt, not just each turn.
            if (_modelCalls >= _options.MaxModelCalls)
                break;

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

            _trace?.Invoke($"    attempt {i + 1} failed ({decision.Outcome}); raw: {Snippet(response.RawJson)}");
        }

        ShadowObservation? completedObservation = null;
        switch (decision.Outcome)
        {
            case AttemptOutcome.ValidSpeak:
                // The M0 no-op boundary is contract-validated but never invokes an LLM.
                // Shadow and enforcement paths are deliberately NOT operational in M0.
                var candidate = new ModerationCandidate(
                    Guid.NewGuid(), _session, opportunityId, offer.Participant,
                    boundary, decision.Text!);
                var scope = new ModerationEvidenceScope(
                    _session, opportunityId, offer.Participant, boundary,
                    new HashSet<string>(StringComparer.Ordinal));
                var moderatorResult = DisabledModerator.Instance.Review(candidate, scope);
                if (moderatorResult.Disposition != ModerationDisposition.Accept)
                    throw new InvalidOperationException("Disabled moderator returned an invalid disposition.");
                // Commit before delivery: the utterance becomes visible to others only once this
                // returns, because every context is read back from committed utterances.
                var repliesTo = decision.RepliesTo ?? offer.ReplyTo;
                var utterance = _store.CommitUtterance(
                    opportunityId, _session, offer.Participant, decision.Text!, decision.Addressee, repliesTo, _clock());
                _store.SaveDecision(new ParticipantDecision(
                    opportunityId, offer.Participant, DecisionAction.Speak, decision.Addressee, repliesTo, lastAttempt));
                _coordinator.RecordSpoke(utterance.Id, offer.Participant, decision.Addressee);
                _trace?.Invoke($"  {self.Name}: {decision.Text}");
                if (_shadowSink is not null || _verifierSink is not null)
                    completedObservation = new ShadowObservation(candidate, utterance.Id, history.ToArray());
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

        // A post-commit, nonblocking notification only. No observer may approve,
        // delay or veto this already-authoritative participant outcome.
        if (completedObservation is { } observed && _shadowSink is not null)
        {
            try
            {
                if (!_shadowSink.TryObserve(observed))
                    _trace?.Invoke("  [Moderator] shadow observation skipped (queue full).");
            }
            catch (Exception ex)
            {
                // Observation failure never becomes participant failure or silence.
                try { _trace?.Invoke($"  [Moderator] observer error: {ex.Message}"); }
                catch { /* Trace is observational, too. */ }
            }
        }
        if (completedObservation is { } verifierObserved && _verifierSink is not null)
        {
            try
            {
                if (!_verifierSink.TryObserve(verifierObserved))
                    _trace?.Invoke("  [Verifier] shadow observation dropped (queue full).");
            }
            catch (Exception ex)
            {
                // Secondary observation can never veto SPEAK or mutate any
                // participant prompt, memory, coordinator or draft response.
                try { _trace?.Invoke($"  [Verifier] observer error: {ex.GetType().Name}"); }
                catch { /* Trace is observational, too. */ }
            }
        }
    }
}

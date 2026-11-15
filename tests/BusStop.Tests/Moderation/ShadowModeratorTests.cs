using System.Text.Json;
using System.Text.RegularExpressions;
using BusStop.Context;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Moderation;
using BusStop.Models;
using BusStop.Persistence;
using Microsoft.Data.Sqlite;

namespace BusStop.Tests.Moderation;

public sealed class ShadowModeratorTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"busstop-m1-{Guid.NewGuid():N}.db");
    private readonly CharacterProfile _arthur = new(CharacterId.New(), 1, "Arthur",
        "Retired machinist. No established FAA family connection.", new[] { "ornery" });
    private readonly CharacterProfile _maya = new(CharacterId.New(), 1, "Maya",
        "Nurse. Her father retrained after a mill closure.", new[] { "warm" });
    private readonly CharacterProfile _daniel = new(CharacterId.New(), 1, "Daniel",
        "Teacher.", new[] { "curious" });
    private readonly Briefing _briefing = new(BriefingId.New(), "FAA ground stop",
        "Test document", "local-evidence:test", "Communications failed.");

    private sealed class EchoModerator : IModelClient
    {
        public string ModelName => "local-moderator";
        public string ModelDigest => "local-digest";
        public int Calls { get; private set; }
        public ModelResponse Decide(ModelRequest request, CancellationToken token = default)
        {
            Calls++;
            var match = Regex.Match(request.Prompt,
                @"Candidate ID: (?<id>[a-f0-9-]{36})", RegexOptions.IgnoreCase);
            if (!match.Success)
                throw new InvalidOperationException("Candidate ID was not sent to moderator.");
            return ModelResponse.Responded(JsonSerializer.Serialize(new
            {
                contractVersion = ShadowResponseParser.ContractVersion,
                candidateId = match.Groups["id"].Value,
                disposition = "Accept",
                severity = "None",
                finding = (object?)null
            }));
        }
    }

    private ConversationStore Open()
    {
        var store = ConversationStore.OpenFile(_path);
        store.InitializeSchema();
        return store;
    }

    private (SessionId Id, CharacterProfile[] Cast) Seed(
        ConversationStore store, ModerationSessionPolicy policy)
    {
        var cast = new[] { _arthur, _maya, _daniel };
        foreach (var profile in cast)
            store.SaveCharacter(profile);
        store.SaveBriefing(_briefing);
        var id = SessionId.New();
        store.CreateSession(new Session(
            id, cast.Select(x => x.Id).ToArray(), SessionMode.FreshEncounter,
            _briefing.Id, "floor-2", "stub", "test", SessionStatus.Active,
            DateTimeOffset.UtcNow, null), policy);
        return (id, cast);
    }

    private static ModerationSessionPolicy Policy(int calls = 4) =>
        ModerationSessionPolicy.Disabled with
        {
            Mode = ModerationOperatingMode.Shadow,
            RuleVersion = "shadow-rules-v1",
            PromptVersion = "shadow-m1-prompt-v1",
            ModelName = "local-moderator",
            ModelDigest = "local-digest",
            Temperature = 0.1,
            Think = false,
            MaxModeratorCalls = calls
        };

    [Fact]
    public void Strict_response_parser_rejects_stale_candidates_and_out_of_scope_references()
    {
        var candidate = new ModerationCandidate(
            Guid.NewGuid(), SessionId.New(), OpportunityId.New(),
            _arthur.Id, 0, "My father worked for the FAA.");
        var scope = new ModerationEvidenceScope(
            candidate.Session, candidate.Opportunity, candidate.Speaker, 0,
            new HashSet<string>(StringComparer.Ordinal) { "bio:arthur" });

        string Json(string candidateId, string[] refs) => JsonSerializer.Serialize(new
        {
            contractVersion = ShadowResponseParser.ContractVersion,
            candidateId,
            disposition = "RequestRevision",
            severity = "High",
            finding = new
            {
                kind = "UnestablishedAutobiography",
                claimSpan = "My father worked for the FAA.",
                explanation = "No established personal-history support.",
                evidenceReferences = refs,
                uncertain = false,
                narrowFeedback = "Remove invented autobiography, keep your FAA opinion."
            }
        });

        var valid = ShadowResponseParser.Parse(Json(candidate.CandidateId.ToString(), new[] { "bio:arthur" }),
            candidate, scope);
        Assert.Equal(ModerationDisposition.RequestRevision, valid.Judgment.Disposition);
        Assert.Equal(ModeratorSeverity.High, valid.Severity);

        Assert.Throws<InvalidDataException>(() =>
            ShadowResponseParser.Parse(Json(Guid.NewGuid().ToString(), new[] { "bio:arthur" }),
                candidate, scope));
        Assert.Throws<InvalidOperationException>(() =>
            ShadowResponseParser.Parse(Json(candidate.CandidateId.ToString(), new[] { "unseen" }),
                candidate, scope));
    }

    [Fact]
    public void Deterministic_triggers_detect_identity_cues_and_exact_other_speaker_copies()
    {
        var id = SessionId.New();
        var opp = OpportunityId.New();
        ShadowObservation O(string text, Utterance[]? history = null) =>
            new(new ModerationCandidate(Guid.NewGuid(), id, opp, _arthur.Id, history?.Length ?? 0, text),
                UtteranceId.New(), history ?? Array.Empty<Utterance>());
        Assert.Equal("biography-cue",
            ShadowModerationWorker.DetectTrigger(O("My father flew for the FAA."), 9));
        Assert.Equal("specific-number",
            ShadowModerationWorker.DetectTrigger(O("There were 3,500 affected flights."), 9));
        Assert.Null(ShadowModerationWorker.DetectTrigger(O("That seems wrong."), 9));

        var prior = new Utterance(UtteranceId.New(), id, OpportunityId.New(), _maya.Id,
            1, "I saw the radar fail.", null, null, DateTimeOffset.UtcNow);
        Assert.Equal("exact-copy",
            ShadowModerationWorker.DetectTrigger(O("I saw the radar fail.", new[] { prior }), 9));
    }

    [Fact]
    public async Task Shadow_reviews_are_audited_but_cannot_change_participant_speech()
    {
        using var store = Open();
        var (session, cast) = Seed(store, Policy());
        var participant = new StubModelClient(new[]
        {
            StubResponses.Speak("My father worked for the FAA."),
            StubResponses.Speak("I disagree with you."),
            StubResponses.Pass()
        });
        var moderator = new EchoModerator();
        var policy = store.GetModerationPolicy(session);
        var observer = new ShadowModerationWorker(
            _path, session, policy, moderator, cast, _briefing,
            sampleEvery: 1, queueCapacity: 8);
        var result = new ConversationEngine(
            store, participant, new ParticipantContextBuilder(),
            session, cast, _briefing, new EngineOptions(MaxOpportunities: 3),
            shadowSink: observer).Run();
        await observer.DrainAsync();

        Assert.Equal(TerminationReason.BudgetReached, result.Reason);
        Assert.Equal(3, result.ModelCalls);
        Assert.Equal(new[] { "My father worked for the FAA.", "I disagree with you." },
            store.GetCommittedUtterances(session).Select(x => x.Text));
        Assert.Equal(3, store.GetAttempts(session).Count);
        Assert.Equal(2, store.GetShadowReviews(session).Count);
        Assert.Equal(2, store.GetModeratorReservations(session).Count);
        Assert.Equal(2, moderator.Calls);
        Assert.All(store.GetShadowReviews(session), r =>
        {
            Assert.Equal(ShadowReviewStatus.Reviewed, r.Status);
            Assert.Equal(ModerationDisposition.Accept, r.Judgment!.Disposition);
            Assert.True(store.GetCommittedUtterances(session)
                .Any(u => u.Id == r.CommittedUtterance && u.Text == r.Candidate.Text));
        });

        using var reopened = Open();
        Assert.Equal(2, reopened.GetShadowReviews(session).Count);
        Assert.Throws<InvalidOperationException>(() =>
            reopened.AppendShadowReview(reopened.GetShadowReviews(session)[0] with
            {
                Candidate = reopened.GetShadowReviews(session)[0].Candidate with { Text = "Forged" }
            }));
    }

    [Fact]
    public async Task Moderator_budget_failure_never_prevents_authoritative_commits()
    {
        using var store = Open();
        var (session, cast) = Seed(store, Policy(calls: 1));
        var participant = new StubModelClient(new[]
        {
            StubResponses.Speak("My father worked for the FAA."),
            StubResponses.Speak("Your father worked there too."),
            StubResponses.Pass()
        });
        var model = new EchoModerator();
        var observer = new ShadowModerationWorker(
            _path, session, store.GetModerationPolicy(session), model, cast, _briefing,
            sampleEvery: 1, queueCapacity: 8);
        new ConversationEngine(store, participant, new ParticipantContextBuilder(),
            session, cast, _briefing, new EngineOptions(MaxOpportunities: 3),
            shadowSink: observer).Run();
        await observer.DrainAsync();

        Assert.Equal(2, store.GetCommittedUtterances(session).Count);
        Assert.Single(store.GetModeratorReservations(session));
        Assert.Equal(1, model.Calls);
        Assert.Contains(store.GetShadowReviews(session),
            r => r.Status == ShadowReviewStatus.BudgetExhausted);
    }

    [Fact]
    public void Shadow_policy_without_observer_is_rejected_before_running()
    {
        using var store = Open();
        var (session, cast) = Seed(store, Policy());
        Assert.Throws<InvalidOperationException>(() =>
            new ConversationEngine(store,
                new StubModelClient(Array.Empty<ModelResponse>()),
                new ParticipantContextBuilder(), session, cast, _briefing));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
            File.Delete(_path);
    }
}

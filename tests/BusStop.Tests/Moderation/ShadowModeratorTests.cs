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
        private readonly bool _requestRevision;
        private readonly bool _malformed;

        public EchoModerator(bool requestRevision = false, bool malformed = false)
        {
            _requestRevision = requestRevision;
            _malformed = malformed;
        }

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
            if (_malformed)
                return ModelResponse.Responded("{this is not JSON");

            var id = match.Groups["id"].Value;
            if (_requestRevision)
            {
                var matchRef = Regex.Match(request.Prompt, @"\[(?<ref>bio:[^\]]+)\]");
                if (!matchRef.Success)
                    throw new InvalidOperationException("Moderator must see the speaker biography reference.");
                return ModelResponse.Responded(JsonSerializer.Serialize(new
                {
                    contractVersion = ShadowResponseParser.ContractVersion,
                    candidateId = id,
                    disposition = "RequestRevision",
                    severity = "High",
                    finding = new
                    {
                        kind = "UnestablishedAutobiography",
                        claimSpan = "My father worked for the FAA.",
                        explanation = "This is not in the speaker's established biography.",
                        evidenceReferences = new[] { matchRef.Groups["ref"].Value },
                        uncertain = false,
                        narrowFeedback = "Remove the invented family connection but retain your opinion."
                    }
                }));
            }

            return ModelResponse.Responded(JsonSerializer.Serialize(new
            {
                contractVersion = ShadowResponseParser.ContractVersion,
                candidateId = id,
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
            RuleVersion = ShadowModerationWorker.CurrentRuleVersion,
            PromptVersion = ShadowModerationWorker.CurrentPromptVersion,
            ModelName = "local-moderator",
            ModelDigest = "local-digest",
            Temperature = 0.1,
            Think = false,
            MaxModeratorCalls = calls,
            ShadowSampleEvery = 1
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
            Assert.Contains(store.GetCommittedUtterances(session),
                u => u.Id == r.CommittedUtterance && u.Text == r.Candidate.Text);
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
    public async Task Shadow_revision_request_is_recorded_but_original_speech_remains_committed()
    {
        using var store = Open();
        var (session, cast) = Seed(store, Policy());
        var observer = new ShadowModerationWorker(
            _path, session, store.GetModerationPolicy(session),
            new EchoModerator(requestRevision: true), cast, _briefing,
            sampleEvery: 1, queueCapacity: 4);
        var result = new ConversationEngine(
            store, new StubModelClient(new[]
            {
                StubResponses.Speak("My father worked for the FAA.")
            }), new ParticipantContextBuilder(), session, cast, _briefing,
            new EngineOptions(MaxOpportunities: 1), shadowSink: observer).Run();
        await observer.DrainAsync();

        Assert.Equal(TerminationReason.BudgetReached, result.Reason);
        Assert.Equal("My father worked for the FAA.",
            Assert.Single(store.GetCommittedUtterances(session)).Text);
        var review = Assert.Single(store.GetShadowReviews(session));
        Assert.Equal(ShadowReviewStatus.Reviewed, review.Status);
        Assert.Equal(ModerationDisposition.RequestRevision, review.Judgment!.Disposition);
        Assert.Equal(ModeratorSeverity.High, review.Severity);
        Assert.Single(store.GetAttempts(session)); // no hidden corrective participant call
    }

    [Fact]
    public async Task Malformed_moderator_output_is_private_audit_failure_not_character_failure()
    {
        using var store = Open();
        var (session, cast) = Seed(store, Policy());
        var observer = new ShadowModerationWorker(
            _path, session, store.GetModerationPolicy(session),
            new EchoModerator(malformed: true), cast, _briefing,
            sampleEvery: 1, queueCapacity: 4);
        new ConversationEngine(
            store, new StubModelClient(new[]
            {
                StubResponses.Speak("My father worked for the FAA.")
            }), new ParticipantContextBuilder(), session, cast, _briefing,
            new EngineOptions(MaxOpportunities: 1), shadowSink: observer).Run();
        await observer.DrainAsync();

        Assert.Single(store.GetCommittedUtterances(session));
        Assert.Equal(ShadowReviewStatus.InvalidResponse,
            Assert.Single(store.GetShadowReviews(session)).Status);
        Assert.Equal(2, store.GetModeratorReservations(session).Count);
        Assert.All(store.GetModeratorReservations(session), reservation =>
            Assert.Equal(ModerationAttemptStatus.Completed, reservation.Status));
        Assert.Equal(2, Assert.Single(store.GetShadowReviews(session)).Attempts!.Count);
    }

    [Fact]
    public void Ollama_schema_pins_candidate_identity_and_required_fields()
    {
        var id = Guid.NewGuid();
        using var schema = JsonDocument.Parse(ShadowResponseParser.JsonSchemaFor(id));
        var root = schema.RootElement;
        var properties = root.GetProperty("properties");
        Assert.Equal(id.ToString("D"),
            properties.GetProperty("candidateId").GetProperty("enum")[0].GetString());
        Assert.Contains(root.GetProperty("required").EnumerateArray(),
            entry => entry.GetString() == "finding");
        Assert.Equal("object", root.GetProperty("type").GetString());
    }

    [Fact]
    public void Scoped_ollama_schema_lists_exact_authorized_references_not_bare_ids()
    {
        var candidateId = Guid.NewGuid();
        var briefing = "briefing:" + Guid.NewGuid().ToString("D");
        var bio = "bio:arthur:v1";
        var utterance = "utterance:" + Guid.NewGuid().ToString("D");
        var references = new[] { utterance, bio, briefing, bio };
        using var schema = JsonDocument.Parse(
            ShadowResponseParser.JsonSchemaFor(candidateId, references));
        var entries = schema.RootElement.GetProperty("properties")
            .GetProperty("finding").GetProperty("anyOf")[1]
            .GetProperty("properties").GetProperty("evidenceReferences")
            .GetProperty("items").GetProperty("enum")
            .EnumerateArray()
            .Select(x => x.GetString()).ToArray();

        Assert.Equal(3, entries.Length);
        Assert.Contains(briefing, entries);
        Assert.Contains(bio, entries);
        Assert.Contains(utterance, entries);
        Assert.DoesNotContain(briefing[("briefing:".Length)..], entries);
        Assert.Equal(candidateId.ToString("D"),
            schema.RootElement.GetProperty("properties")
                .GetProperty("candidateId").GetProperty("enum")[0].GetString());
    }

    [Fact]
    public void Scoped_schema_escapes_untrusted_references_and_handles_empty_scope()
    {
        const string untrusted = "utterance:quote\\\"\\ncomma, brace }";
        using var schema = JsonDocument.Parse(
            ShadowResponseParser.JsonSchemaFor(Guid.NewGuid(),
                new[] { untrusted }));
        var enumerated = schema.RootElement.GetProperty("properties")
            .GetProperty("finding").GetProperty("anyOf")[1]
            .GetProperty("properties").GetProperty("evidenceReferences")
            .GetProperty("items").GetProperty("enum")[0].GetString();
        Assert.Equal(untrusted, enumerated);

        using var empty = JsonDocument.Parse(
            ShadowResponseParser.JsonSchemaFor(Guid.NewGuid(),
                Array.Empty<string>()));
        Assert.Empty(empty.RootElement.GetProperty("properties")
            .GetProperty("finding").GetProperty("anyOf")[1]
            .GetProperty("properties").GetProperty("evidenceReferences")
            .GetProperty("items").GetProperty("enum").EnumerateArray());
    }

    [Fact]
    public async Task Exact_cross_speaker_copy_is_identified_without_ollama_even_after_budget()
    {
        using var store = Open();
        var (session, cast) = Seed(store, Policy(calls: 1));
        var model = new EchoModerator();
        var observer = new ShadowModerationWorker(
            _path, session, store.GetModerationPolicy(session), model, cast, _briefing,
            sampleEvery: 1, queueCapacity: 8);
        new ConversationEngine(store, new StubModelClient(new[]
        {
            StubResponses.Speak("This machine needs inspection."),
            StubResponses.Speak("This machine needs inspection.")
        }), new ParticipantContextBuilder(), session, cast, _briefing,
            new EngineOptions(MaxOpportunities: 2), shadowSink: observer).Run();
        await observer.DrainAsync();

        Assert.Equal(2, store.GetCommittedUtterances(session).Count);
        Assert.Equal(1, model.Calls);
        Assert.Single(store.GetModeratorReservations(session));
        var copy = Assert.Single(store.GetShadowReviews(session)
            .Where(r => r.Judgment?.Finding?.Kind == ModerationIssueKind.ExactDuplicate));
        Assert.Null(copy.RequestId);
        Assert.Empty(copy.Attempts!);
        Assert.Equal(ShadowReviewStatus.Reviewed, copy.Status);
        Assert.Equal(ModerationDisposition.Advisory, copy.Judgment!.Disposition);
    }

    private sealed class RecoveringModerator : IModelClient
    {
        public int Calls { get; private set; }
        public string ModelName => "local-moderator";
        public string ModelDigest => "local-digest";

        public ModelResponse Decide(ModelRequest request, CancellationToken token = default)
        {
            Assert.NotNull(request.JsonSchema);
            Assert.Equal(8192, request.ContextTokens);
            Assert.Equal(768, request.MaxGeneratedTokens);
            Calls++;
            if (Calls == 1)
                return ModelResponse.Responded("{\"contractVersion\":") with
                {
                    Generation = new ModelGenerationTelemetry("length", 2400, 32, 1000)
                };
            var match = Regex.Match(request.Prompt,
                @"Candidate ID: (?<id>[a-f0-9-]{36})", RegexOptions.IgnoreCase);
            Assert.True(match.Success);
            return ModelResponse.Responded(JsonSerializer.Serialize(new
            {
                contractVersion = ShadowResponseParser.ContractVersion,
                candidateId = match.Groups["id"].Value,
                disposition = "Accept",
                severity = "None",
                finding = (object?)null
            })) with
            {
                Generation = new ModelGenerationTelemetry("stop", 2300, 45, 1000)
            };
        }
    }

    [Fact]
    public async Task Invalid_json_retry_spends_new_reservation_and_records_both_telemetry_traces()
    {
        using var store = Open();
        var (session, cast) = Seed(store, Policy(calls: 2));
        var model = new RecoveringModerator();
        var observer = new ShadowModerationWorker(
            _path, session, store.GetModerationPolicy(session), model, cast, _briefing,
            sampleEvery: 1, queueCapacity: 4);
        new ConversationEngine(store, new StubModelClient(new[]
        {
            StubResponses.Speak("A careful repair is worth the delay.")
        }), new ParticipantContextBuilder(), session, cast, _briefing,
            new EngineOptions(MaxOpportunities: 1), shadowSink: observer).Run();
        await observer.DrainAsync();

        Assert.Equal(2, model.Calls);
        Assert.Equal(2, store.GetModeratorReservations(session).Count);
        var review = Assert.Single(store.GetShadowReviews(session));
        Assert.Equal(ShadowReviewStatus.Reviewed, review.Status);
        Assert.Equal(2, review.Attempts!.Count);
        Assert.Equal("length", review.Attempts[0].DoneReason);
        Assert.Equal(ShadowReviewStatus.InvalidResponse, review.Attempts[0].Status);
        Assert.Equal("stop", review.Attempts[1].DoneReason);
        Assert.Equal(ShadowReviewStatus.Reviewed, review.Attempts[1].Status);
        Assert.Single(store.GetCommittedUtterances(session));
        using var reopened = Open();
        Assert.Equal(2, Assert.Single(reopened.GetShadowReviews(session)).Attempts!.Count);
    }

    private sealed class OutOfScopeModerator : IModelClient
    {
        public int Calls { get; private set; }
        public string ModelName => "local-moderator";
        public string ModelDigest => "local-digest";

        public ModelResponse Decide(ModelRequest request, CancellationToken token = default)
        {
            Calls++;
            var id = Regex.Match(request.Prompt,
                @"Candidate ID: (?<id>[a-f0-9-]{36})", RegexOptions.IgnoreCase)
                .Groups["id"].Value;
            return ModelResponse.Responded(JsonSerializer.Serialize(new
            {
                contractVersion = ShadowResponseParser.ContractVersion,
                candidateId = id,
                disposition = "RequestRevision",
                severity = "High",
                finding = new
                {
                    kind = "UnestablishedAutobiography",
                    claimSpan = "My father worked for the FAA.",
                    explanation = "Fabricated citation that must fail host authorization.",
                    evidenceReferences = new[] { "utterance:UNAUTHORIZED" },
                    uncertain = false,
                    narrowFeedback = "Reject out of scope reference."
                }
            }));
        }
    }

    [Fact]
    public async Task Host_scope_violation_is_not_retried_or_used_to_modify_speech()
    {
        using var store = Open();
        var (session, cast) = Seed(store, Policy(calls: 4));
        var moderator = new OutOfScopeModerator();
        var observer = new ShadowModerationWorker(
            _path, session, store.GetModerationPolicy(session), moderator,
            cast, _briefing, sampleEvery: 1, queueCapacity: 4);
        new ConversationEngine(store, new StubModelClient(new[]
        {
            StubResponses.Speak("My father worked for the FAA.")
        }), new ParticipantContextBuilder(), session, cast, _briefing,
            new EngineOptions(MaxOpportunities: 1), shadowSink: observer).Run();
        await observer.DrainAsync();

        Assert.Equal(1, moderator.Calls);
        Assert.Single(store.GetModeratorReservations(session));
        var review = Assert.Single(store.GetShadowReviews(session));
        Assert.Equal(ShadowReviewStatus.InvalidResponse, review.Status);
        Assert.Single(review.Attempts!);
        Assert.Single(store.GetCommittedUtterances(session));
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

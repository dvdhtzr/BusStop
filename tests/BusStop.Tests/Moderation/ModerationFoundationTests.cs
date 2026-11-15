using BusStop.Context;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Moderation;
using BusStop.Models;
using BusStop.Persistence;
using Microsoft.Data.Sqlite;

namespace BusStop.Tests.Moderation;

public sealed class ModerationFoundationTests : IDisposable
{
    private readonly string _db = Path.Combine(
        Path.GetTempPath(), "busstop-moderator-m0-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly CharacterProfile _a = new(CharacterId.New(), 1, "Arthur", "Machinist.", new[] { "direct" });
    private readonly CharacterProfile _b = new(CharacterId.New(), 1, "Maya", "Nurse.", new[] { "warm" });
    private readonly CharacterProfile _c = new(CharacterId.New(), 1, "Daniel", "Teacher.", new[] { "curious" });
    private readonly Briefing _story = new(BriefingId.New(), "A story", "Local", "https://example.com", "Body");

    private ConversationStore Open()
    {
        var store = ConversationStore.OpenFile(_db);
        store.InitializeSchema();
        return store;
    }

    private SessionId Seed(ConversationStore store, ModerationSessionPolicy? policy = null)
    {
        var cast = new[] { _a, _b, _c };
        foreach (var character in cast)
            store.SaveCharacter(character);
        store.SaveBriefing(_story);
        var id = SessionId.New();
        store.CreateSession(new Session(
            id, cast.Select(c => c.Id).ToArray(), SessionMode.FreshEncounter,
            _story.Id, "floor-test", "stub/stub-digest", "ctx-test",
            SessionStatus.Active, DateTimeOffset.UtcNow, null), policy);
        return id;
    }

    private ConversationEngine Engine(ConversationStore store, SessionId session, IModelClient model) =>
        new(store, model, new ParticipantContextBuilder(), session,
            new[] { _a, _b, _c }, _story);

    private void ExecuteSql(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static ModerationSessionPolicy ShadowPolicy(int calls = 2) =>
        ModerationSessionPolicy.Disabled with
        {
            Mode = ModerationOperatingMode.Shadow,
            RuleVersion = "rules-m0",
            PromptVersion = "prompt-m0",
            ModelName = "qwen3.5:9b",
            ModelDigest = "test-digest",
            Temperature = 0.8,
            Think = false,
            MaxModeratorCalls = calls,
            MaxRevisionsPerOpportunity = 1
        };

    [Fact]
    public void Disabled_moderation_preserves_participant_speech_and_call_accounting()
    {
        using var store = Open();
        var session = Seed(store);
        var model = new StubModelClient(new[]
        {
            StubResponses.Speak("I disagree."),
            StubResponses.Pass(),
            StubResponses.Pass()
        });
        var result = new ConversationEngine(
            store, model, new ParticipantContextBuilder(), session,
            new[] { _a, _b, _c }, _story, new EngineOptions(MaxOpportunities: 3)).Run();

        Assert.Equal(TerminationReason.BudgetReached, result.Reason);
        Assert.Equal(3, result.ModelCalls);
        Assert.Equal(new[] { "I disagree." },
            store.GetCommittedUtterances(session).Select(u => u.Text));
        Assert.Equal(3, store.GetAttempts(session).Count);
        Assert.Empty(store.GetModeratorReservations(session));
        Assert.Equal(ModerationSessionPolicy.Disabled, store.GetModerationPolicy(session));
    }

    [Fact]
    public void New_policy_snapshot_is_preserved_across_database_reopen()
    {
        SessionId id;
        using (var store = Open())
        {
            id = Seed(store);
            Assert.Equal(ModerationSessionPolicy.Disabled, store.GetModerationPolicy(id));
        }
        using var reopened = Open();
        Assert.Equal(ModerationSessionPolicy.Disabled, reopened.GetModerationPolicy(id));
    }

    [Fact]
    public void Existing_pre_m0_database_schema_is_upgraded_additively()
    {
        // Legacy sessions table has no moderation marker at all.
        ExecuteSql("""
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                mode TEXT NOT NULL,
                briefing_id TEXT NOT NULL,
                protocol_version TEXT NOT NULL,
                model_config_version TEXT NOT NULL,
                context_version TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                ended_at TEXT NULL
            );
            """);

        using var store = Open();
        var id = Seed(store);
        Assert.Equal(ModerationSessionPolicy.Disabled, store.GetModerationPolicy(id));
        Assert.Single(store.GetSession(id)!.Participants.Take(1));
    }

    [Fact]
    public void Pre_moderation_sessions_remain_legacy_disabled_after_migration()
    {
        SessionId id;
        using (var store = Open())
            id = Seed(store);

        // Mimic a pre-M0 session that had no version marker or policy row.
        ExecuteSql("UPDATE sessions SET moderation_policy_marker = NULL; " +
                   "DELETE FROM session_moderation_policies;");

        using var reopened = Open();
        Assert.Equal(ModerationSessionPolicy.Disabled, reopened.GetModerationPolicy(id));
        var scripted = new StubModelClient(new[] { StubResponses.Pass() });
        var result = new ConversationEngine(
            reopened, scripted, new ParticipantContextBuilder(), id,
            new[] { _a, _b, _c }, _story,
            new EngineOptions(MaxModelCalls: 1)).Run();
        Assert.Equal(TerminationReason.BudgetReached, result.Reason);
        Assert.Single(reopened.GetAttempts(id));
    }

    [Fact]
    public void New_session_missing_policy_fails_closed_instead_of_downgrading()
    {
        SessionId id;
        using (var store = Open())
            id = Seed(store);

        ExecuteSql("DELETE FROM session_moderation_policies;");

        using var reopened = Open();
        Assert.Throws<InvalidOperationException>(() => reopened.GetModerationPolicy(id));
        Assert.Throws<InvalidOperationException>(() =>
            Engine(reopened, id, new StubModelClient(Array.Empty<ModelResponse>())));
    }

    [Fact]
    public void Unsupported_future_moderation_mode_cannot_run_in_m0()
    {
        using var store = Open();
        var session = Seed(store, ShadowPolicy());
        Assert.Equal(ModerationOperatingMode.Shadow, store.GetModerationPolicy(session).Mode);
        Assert.Throws<InvalidOperationException>(() =>
            Engine(store, session, new StubModelClient(Array.Empty<ModelResponse>())));
    }

    [Fact]
    public void Invalid_pinned_policy_is_rejected_before_session_creation()
    {
        using var store = Open();
        Assert.Throws<InvalidOperationException>(() =>
            Seed(store, ModerationSessionPolicy.Disabled with { MaxModeratorCalls = 1 }));
        Assert.Throws<InvalidOperationException>(() =>
            Seed(store, ShadowPolicy() with { ContractVersion = "unknown-version" }));
    }

    [Fact]
    public void Reservation_spends_separate_budget_before_dispatch_and_survives_restart()
    {
        SessionId session;
        var request = Guid.NewGuid();
        using (var store = Open())
        {
            session = Seed(store, ShadowPolicy(calls: 1));
            var first = store.ReserveModeratorAttempt(session, request, null,
                ModerationInferencePurpose.ShadowSampling, DateTimeOffset.UtcNow);
            Assert.Equal(ModerationAttemptStatus.Reserved, first.Status);
            Assert.Equal(first, store.ReserveModeratorAttempt(session, request, null,
                ModerationInferencePurpose.ShadowSampling, DateTimeOffset.UtcNow));
            Assert.Empty(store.GetAttempts(session)); // participant ledger remains separate
        }

        using var reopened = Open();
        Assert.Single(reopened.GetModeratorReservations(session));
        Assert.Throws<InvalidOperationException>(() =>
            reopened.ReserveModeratorAttempt(session, Guid.NewGuid(), null,
                ModerationInferencePurpose.MaterialViolationReview, DateTimeOffset.UtcNow));

        reopened.FinalizeModeratorAttempt(session, request, ModerationAttemptStatus.Interrupted);
        reopened.FinalizeModeratorAttempt(session, request, ModerationAttemptStatus.Interrupted);
        Assert.Equal(ModerationAttemptStatus.Interrupted,
            reopened.GetModeratorReservations(session).Single().Status);
        Assert.Throws<InvalidOperationException>(() =>
            reopened.FinalizeModeratorAttempt(session, request, ModerationAttemptStatus.Completed));
        Assert.Throws<InvalidOperationException>(() =>
            reopened.ReserveModeratorAttempt(session, Guid.NewGuid(), null,
                ModerationInferencePurpose.MaterialViolationReview, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Disabled_policy_cannot_reserve_inference()
    {
        using var store = Open();
        var id = Seed(store);
        Assert.Throws<InvalidOperationException>(() =>
            store.ReserveModeratorAttempt(id, Guid.NewGuid(), null,
                ModerationInferencePurpose.ShadowSampling, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Candidate_and_judgment_must_match_host_scope()
    {
        var session = SessionId.New();
        var opportunity = OpportunityId.New();
        var candidate = new ModerationCandidate(
            Guid.NewGuid(), session, opportunity, _a.Id, 3,
            "My father worked at the FAA.");
        var scope = new ModerationEvidenceScope(
            session, opportunity, _a.Id, 3,
            new HashSet<string>(StringComparer.Ordinal) { "character:A:v1" });
        Assert.Equal(ModerationDisposition.Accept,
            DisabledModerator.Instance.Review(candidate, scope).Disposition);

        var review = new ModerationJudgment(
            candidate.CandidateId, ModerationDisposition.RequestRevision,
            new ModerationFinding(
                ModerationIssueKind.UnestablishedAutobiography, "My father worked at the FAA.",
                "This does not appear in the established personal-history evidence.",
                new[] { "character:A:v1" }, false, "Remove the unsupported autobiography."));
        ModerationContractValidator.Validate(candidate, scope, review);
        Assert.Throws<InvalidOperationException>(() =>
            ModerationContractValidator.Validate(candidate,
                scope with { Speaker = _b.Id }, review));
        Assert.Throws<InvalidOperationException>(() =>
            ModerationContractValidator.Validate(candidate, scope,
                review with { CandidateId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() =>
            ModerationContractValidator.Validate(candidate, scope,
                review with { Finding = review.Finding! with
                {
                    EvidenceReferences = new[] { "unseen-passage" }
                } }));
        Assert.Throws<InvalidOperationException>(() =>
            ModerationContractValidator.Validate(candidate, scope,
                review with { Finding = review.Finding! with { Uncertain = true } }));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_db))
            File.Delete(_db);
    }
}

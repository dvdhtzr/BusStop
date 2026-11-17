using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Context;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Engine.Verification;
using BusStop.Models;
using BusStop.Moderation;
using BusStop.Moderation.Verification;
using BusStop.Persistence;
using Xunit;

namespace BusStop.Tests.Moderation;

public sealed class VerifierV6cShadowIntegrationTests
{
    private sealed class TestModel : IModelClient
    {
        public string ModelName => "synthetic-v6c-model";
        public string ModelDigest => "synthetic-v6c-digest";
        public int Calls { get; private set; }
        public Func<ModelRequest, ModelResponse> Respond { get; set; } =
            _ => ModelResponse.TransportError();

        public ModelResponse Decide(ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Respond(request);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; }
        public ConversationStore Store { get; }
        public Session Session { get; }
        public CharacterProfile First { get; }
        public CharacterProfile Second { get; }
        public Briefing Briefing { get; }
        public TestModel Model { get; } = new();
        public V4SessionPolicy Policy { get; }
        public Fixture(bool optIn = true, int sharedBudget = 8)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "busstop-v6c-" + Guid.NewGuid().ToString("N") + ".db");
            Store = ConversationStore.OpenFile(Path);
            Store.InitializeSchema();
            First = new CharacterProfile(CharacterId.New(), 1, "Arthur",
                "A careful local observer", new[] { "curious" });
            Second = new CharacterProfile(CharacterId.New(), 1, "Bea",
                "A careful local observer", new[] { "practical" });
            Store.SaveCharacter(First);
            Store.SaveCharacter(Second);
            Briefing = new Briefing(BriefingId.New(), "Fictional local report",
                "synthetic V6c test", "local-evidence:synthetic-v6c",
                "The official report recorded exactly 3 outages and 3 delays.");
            Store.SaveBriefing(Briefing);
            Session = new Session(SessionId.New(),
                new[] { First.Id, Second.Id },
                SessionMode.FreshEncounter, Briefing.Id,
                "floor-2", "synthetic-v6c", "test-context",
                SessionStatus.Active, DateTimeOffset.UtcNow, null);
            Store.CreateSession(Session);
            Policy = new V4SessionPolicy(
                EvidenceVerifierV4.ContractVersion, VerificationOperatingMode.Shadow,
                V3ClaimDetector.RuleVersion, V3ModelClaimExtractor.PromptVersion,
                EvidenceVerifierV4.VerificationPromptVersion,
                V3EvidencePacketAssembler.RetrievalSelectorVersion,
                EvidenceVerifierV4.VerificationSchemaVersion,
                Model.ModelName, Model.ModelDigest, 0.1, false,
                sharedBudget, 3, 2, 2048, 8192);
            if (optIn) Store.PinV4VerifierPolicy(Session.Id, Policy);
        }

        public (Utterance Speech, ShadowObservation Observation) Speak(
            string text, CharacterProfile? speaker = null)
        {
            var who = speaker ?? First;
            var boundary = Store.MaxSequence(Session.Id);
            var opportunity = new SpeakingOpportunity(
                OpportunityId.New(), Session.Id, who.Id,
                OpportunityReason.FairOrder, boundary, OpportunityStatus.Offered);
            Store.SaveOpportunity(opportunity, (int)boundary);
            var prior = Store.GetCommittedUtterances(Session.Id).ToArray();
            var speech = Store.CommitUtterance(
                opportunity.Id, Session.Id, who.Id, text,
                null, null, DateTimeOffset.UtcNow);
            var candidate = new ModerationCandidate(
                Guid.NewGuid(), Session.Id, opportunity.Id,
                who.Id, boundary, text);
            return (speech,
                new ShadowObservation(candidate, speech.Id, prior));
        }

        public V6cShadowVerifier Worker(int sample = 1, int capacity = 8) =>
            new(Path, Session.Id, Policy, Model,
                sampleEvery: sample, queueCapacity: capacity);

        public Guid Job(Utterance speech)
        {
            var seed = V6cShadowContract.EvaluationKey + "|" +
                speech.Session + "|" + speech.Opportunity + "|" + speech.Id;
            return new Guid(SHA256.HashData(
                Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));
        }

        public void Dispose()
        {
            Store.Dispose();
            try { File.Delete(Path); } catch (IOException) { }
            try { File.Delete(Path + ".v5b.lock"); } catch (IOException) { }
        }
    }

    private static ModelResponse Answer(
        ModelRequest request, ClaimVerificationOutcome outcome,
        bool includeOpposition = true)
    {
        Assert.Equal(V4JudgmentParser.JsonSchema, request.JsonSchema);
        const string pre = "BEGIN FROZEN HOST-AUTHORIZED CLAIM BATCH (DATA):\n";
        const string post = "\nEND FROZEN HOST-AUTHORIZED CLAIM BATCH";
        var start = request.Prompt.IndexOf(pre, StringComparison.Ordinal);
        var stop = request.Prompt.LastIndexOf(post, StringComparison.Ordinal);
        Assert.True(start >= 0 && stop > start);
        using var payload = JsonDocument.Parse(
            request.Prompt.Substring(start + pre.Length,
                stop - start - pre.Length));
        var root = payload.RootElement;
        var claims = root.GetProperty("claims").EnumerateArray().ToArray();
        var judgments = claims.Select(claim =>
        {
            var items = claim.GetProperty("content")
                .GetProperty("Items").EnumerateArray().ToArray();
            var refs = includeOpposition && items.Length > 0
                ? new[] { items[0].GetProperty("ReferenceId").GetString()! }
                : Array.Empty<string>();
            return new
            {
                claimId = claim.GetProperty("claimId").GetString(),
                packetId = claim.GetProperty("packetId").GetString(),
                packetSha256 = claim.GetProperty("packetSha256").GetString(),
                outcome = outcome.ToString(),
                supportingReferences = Array.Empty<string>(),
                opposingReferences = outcome ==
                    ClaimVerificationOutcome.ConfirmedContradiction
                    ? refs : Array.Empty<string>(),
                problematicText = outcome ==
                    ClaimVerificationOutcome.ConfirmedContradiction
                    ? claim.GetProperty("claimText").GetString() : "",
                explanation = "Synthetic host-parser integration fixture."
            };
        }).ToArray();
        return ModelResponse.Responded(JsonSerializer.Serialize(new
        {
            jobId = root.GetProperty("jobId").GetString(),
            judgments
        })) with
        {
            Generation = new ModelGenerationTelemetry(
                "stop", 100, 50, 900)
        };
    }

    [Fact]
    public void Disabled_session_cannot_attach_a_verifier_sink()
    {
        using var f = new Fixture(optIn: false);
        var worker = f.Worker();
        Assert.Throws<InvalidOperationException>(() =>
            new ConversationEngine(f.Store, f.Model,
                new ParticipantContextBuilder(), f.Session.Id,
                new[] { f.First, f.Second }, f.Briefing,
                verifierShadowSink: worker));
        Assert.Equal(0, f.Model.Calls);
    }

    [Fact]
    public void Opted_in_verifier_requires_explicit_sink_and_never_reuses_m1()
    {
        using var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() =>
            new ConversationEngine(f.Store, f.Model,
                new ParticipantContextBuilder(), f.Session.Id,
                new[] { f.First, f.Second }, f.Briefing));
        var engine = new ConversationEngine(f.Store, f.Model,
            new ParticipantContextBuilder(), f.Session.Id,
            new[] { f.First, f.Second }, f.Briefing,
            verifierShadowSink: f.Worker());
        Assert.NotNull(engine);
        Assert.Equal(0, f.Model.Calls);
    }

    [Fact]
    public void Queue_overflow_is_nonblocking_and_reports_unregistered_selection()
    {
        using var f = new Fixture();
        var observer = f.Worker(capacity: 1);
        var first = f.Speak("The official report counted 12 outages.");
        var second = f.Speak("The report counted 11 outages.", f.Second);
        var transcript = f.Store.GetCommittedUtterances(f.Session.Id).ToArray();

        Assert.True(observer.TryObserve(first.Observation));
        Assert.False(observer.TryObserve(second.Observation));
        Assert.Equal(0, f.Model.Calls);
        Assert.Equal(2, observer.Coverage.CommittedObserved);
        Assert.Equal(2, observer.Coverage.Selected);
        Assert.Equal(1, observer.Coverage.Dropped);
        Assert.Equal(1, observer.Coverage.Queued);
        Assert.Equal(0, observer.Coverage.DurableJobsRegistered);

        f.Model.Respond = r => Answer(r, ClaimVerificationOutcome.NoMaterialIssue);
        observer.DrainAsync().GetAwaiter().GetResult();
        Assert.Equal(1, observer.Coverage.DurableJobsRegistered);
        Assert.Equal(1, observer.Coverage.JobsSummarized);
        Assert.Equal(1, observer.Coverage.SelectedWithoutDurableJob);
        Assert.False(observer.Coverage.AllSelectedDurablyRegistered);
        Assert.Equal(1, f.Model.Calls);
        Assert.Equal(transcript, f.Store.GetCommittedUtterances(f.Session.Id));
        Assert.Throws<InvalidOperationException>(() =>
            observer.DrainAsync().GetAwaiter().GetResult());
        Assert.Throws<InvalidDataException>(() =>
            f.Store.ReadV5aCheckpoint(f.Job(second.Speech)));
    }

    [Fact]
    public void Every_second_utterance_is_selected_without_silent_clean_verdicts()
    {
        using var f = new Fixture();
        var worker = f.Worker(sample: 2, capacity: 8);
        var first = f.Speak("Maybe the report is unclear.");
        var second = f.Speak("The official report counted 12 outages.");
        var third = f.Speak("The weather looks pleasant today.");
        Assert.True(worker.TryObserve(first.Observation));
        Assert.True(worker.TryObserve(second.Observation));
        Assert.True(worker.TryObserve(third.Observation));
        Assert.Equal(3, worker.Coverage.CommittedObserved);
        Assert.Equal(2, worker.Coverage.NotSelected);
        Assert.Equal(1, worker.Coverage.Selected);
        f.Model.Respond = r => Answer(r, ClaimVerificationOutcome.NoMaterialIssue);
        worker.DrainAsync().GetAwaiter().GetResult();
        Assert.Equal(1, worker.Coverage.DurableJobsRegistered);
        Assert.Equal(1, f.Model.Calls);
        Assert.Equal(0, worker.Coverage.Dropped);
        Assert.False(f.Store.HasV5bOwnershipEpoch(
            new SessionId(Guid.NewGuid())));
        Assert.Equal(1, f.Store.ReadV5aCheckpoint(
            f.Job(second.Speech)).ClaimResults.Count);
    }

    [Fact]
    public void Actual_v3_v4_v5_stages_are_sealed_without_any_speech_mutation()
    {
        using var f = new Fixture();
        var (speech, observation) = f.Speak(
            "The official report counted 12 outages.");
        var before = f.Store.GetCommittedUtterances(f.Session.Id).ToArray();
        var worker = f.Worker();
        f.Model.Respond = req =>
            Answer(req, ClaimVerificationOutcome.ConfirmedContradiction);
        Assert.True(worker.TryObserve(observation));
        worker.DrainAsync().GetAwaiter().GetResult();

        Assert.Equal(1, f.Model.Calls);
        var ledger = f.Store.ReadV5aCheckpoint(f.Job(speech));
        Assert.True(ledger.IsTerminal);
        Assert.NotEmpty(ledger.Claims);
        Assert.NotEmpty(ledger.Packets);
        Assert.Single(ledger.AttemptLinks);
        Assert.Equal(1, ledger.ClaimResults.Count);
        Assert.Equal(1, ledger.Summary!.ConfirmedContradictions);
        Assert.Equal(1, f.Store.GetV4AttemptCounts(ledger.Job)
            .SessionSharedAttempts);
        Assert.True(f.Store.HasV5bOwnershipEpoch(f.Session.Id));
        Assert.Equal(before, f.Store.GetCommittedUtterances(f.Session.Id));
        Assert.Equal(1, worker.Coverage.JobsSummarized);
    }

    [Fact]
    public void Invalid_model_output_is_incomplete_not_no_material_issue()
    {
        using var f = new Fixture();
        var (speech, observation) = f.Speak(
            "The official report counted 12 outages.");
        var worker = f.Worker();
        f.Model.Respond = _ => ModelResponse.Responded("{}");
        worker.TryObserve(observation);
        worker.DrainAsync().GetAwaiter().GetResult();

        var ledger = f.Store.ReadV5aCheckpoint(f.Job(speech));
        Assert.True(ledger.IsTerminal);
        Assert.Equal(0, ledger.Summary!.NoMaterialIssues);
        Assert.Equal(1, ledger.Summary.FailedClaims);
        Assert.Equal(VerificationCompletionStatus.Incomplete,
            ledger.Summary.CompletionStatus);
        Assert.Equal(VerificationOperationalStatus.InvalidResponse,
            Assert.Single(ledger.ClaimResults).Status);
        Assert.Equal(1, worker.Coverage.IncompleteSummaries);
        Assert.Equal(1, f.Model.Calls);
    }

    [Fact]
    public void Unknown_evidence_does_not_require_an_ollama_call_or_claim_truth()
    {
        using var f = new Fixture();
        var (speech, observation) = f.Speak("I worked as a pilot for thirty years.");
        var worker = f.Worker();
        worker.TryObserve(observation);
        worker.DrainAsync().GetAwaiter().GetResult();
        Assert.Equal(0, f.Model.Calls);
        var checkpoint = f.Store.ReadV5aCheckpoint(f.Job(speech));
        Assert.True(checkpoint.IsTerminal);
        Assert.Equal(0, checkpoint.Summary!.ConfirmedContradictions);
        Assert.Contains(checkpoint.ClaimResults, c =>
            c.Judgment?.Outcome == ClaimVerificationOutcome.InsufficientEvidence);
        Assert.Equal(0, f.Store.GetV4AttemptCounts(checkpoint.Job)
            .SessionSharedAttempts);
    }

    [Fact]
    public void One_shared_v4_session_attempt_ceiling_prevents_verifier_dispatch()
    {
        using var f = new Fixture(sharedBudget: 1);
        var (speech, observation) = f.Speak(
            "The official report counted 12 outages.");
        // Seed an already-charged verification request without V5b enrollment.
        // This uses the existing V4 shared-budget row rather than spoofing
        // an unversioned M1 request.
        var old = new VerificationJobIdentity(
            Guid.NewGuid(), speech.Session, speech.Opportunity,
            speech.Id, speech.Speaker, speech.Sequence - 1,
            EvidenceVerifierV1.ContractVersion,
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(speech.Text))).ToLowerInvariant());
        Assert.True(f.Store.TryReserveV4Attempt(
            old, Guid.NewGuid(), V4AttemptPhase.ClaimExtraction));
        var worker = f.Worker();
        worker.TryObserve(observation);
        worker.DrainAsync().GetAwaiter().GetResult();
        Assert.Equal(0, f.Model.Calls);
        Assert.True(f.Store.ReadV5aCheckpoint(f.Job(speech)).IsTerminal);
        Assert.Equal(1, f.Store.GetV4AttemptCounts(old).SessionSharedAttempts);
    }

    [Fact]
    public void Crash_before_drain_never_promises_durable_registration()
    {
        using var f = new Fixture();
        var (speech, observation) = f.Speak(
            "The official report counted 12 outages.");
        var worker = f.Worker();
        Assert.True(worker.TryObserve(observation));
        // Simulate loss of in-memory queue without invoking DrainAsync.
        Assert.Equal(1, worker.Coverage.SelectedWithoutDurableJob);
        Assert.Equal(0, worker.Coverage.DurableJobsRegistered);
        Assert.False(f.Store.HasV5bOwnershipEpoch(f.Session.Id));
        Assert.Throws<InvalidDataException>(() =>
            f.Store.ReadV5aCheckpoint(f.Job(speech)));
        Assert.Equal(0, f.Model.Calls);
    }
}

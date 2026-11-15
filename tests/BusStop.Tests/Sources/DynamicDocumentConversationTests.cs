using System.Text.Json;
using BusStop.Console;
using BusStop.Context;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Models;
using BusStop.Persistence;
using BusStop.Sources;
using Microsoft.Data.Sqlite;

namespace BusStop.Tests.Sources;

public sealed class DynamicDocumentConversationTests : IDisposable
{
    private readonly string _db = Path.Combine(
        Path.GetTempPath(), $"busstop-r1c-{Guid.NewGuid():N}.db");

    private static EvidenceSnapshot Source() =>
        EvidenceSnapshotFactory.Capture(
            new EvidenceSourceDescriptor(
                EvidenceSourceKind.UserDocument, "FAA disruption",
                "Local test document", null, null, "Owner-provided unit-test fixture"),
            string.Join("\n", Enumerable.Range(1, 12).Select(i =>
                $"Passage area {i} aviation transport and airport operations. " +
                (i == 9 ? "Travelers asked about compensation and airfare refunds. " :
                          "Controllers discussed safety, infrastructure, and communications. ") +
                new string((char)('A' + i), 1050))),
            passageCharacters: 1400, overlapCharacters: 120);

    private ConversationStore Open()
    {
        var store = ConversationStore.OpenFile(_db);
        store.InitializeSchema();
        return store;
    }

    [Fact]
    public void Dynamic_retrieval_is_committed_before_inference_and_audited_per_speaker()
    {
        var source = Source();
        var briefing = EvidenceBriefingBuilder.BuildForRetrieval(source);
        SessionId session;
        var model = new CapturingModel();
        IReadOnlyList<OpportunitySourceRetrieval> retrievals;

        using (var store = Open())
        {
            store.SaveEvidenceSnapshot(source);
            session = Laboratory.Seed(
                store, SessionMode.FreshEncounter, "recording/digest",
                briefing, EvidenceBriefingBuilder.RetrievalContextVersion);

            store.PinSessionEvidenceForRetrieval(session, source.SnapshotId);
            Assert.Empty(store.GetSessionEvidence(session)!.ExposedPassageIds);
            Assert.DoesNotContain("Travelers asked about compensation", briefing.Body);

            var engine = new ConversationEngine(
                store, model, new ParticipantContextBuilder(),
                session, Laboratory.Cast, briefing,
                sourceProvider: new LocalOpportunitySourceProvider(store, source));

            var result = engine.Run();
            Assert.Equal(TerminationReason.NaturalEnd, result.Reason);
            retrievals = store.GetOpportunityRetrievals(session);
            Assert.Equal(model.Prompts.Count, retrievals.Count);
            Assert.True(retrievals.Count >= 6);
            Assert.Contains(retrievals.SelectMany(x => x.PassageIds),
                id => source.Passages.Any(p => p.Ordinal > 4 && p.PassageId == id));
            Assert.All(retrievals, x =>
            {
                Assert.Equal(source.SnapshotId, x.SnapshotId);
                Assert.InRange(x.PassageIds.Count, 1, 3);
                Assert.Equal(x.PassageIds.Count, x.Scores.Count);
                Assert.Equal(LocalLexicalRetriever.Version, x.Algorithm);
                Assert.False(string.IsNullOrWhiteSpace(x.Query));
            });

            // Each model request contains the EXACT passages persisted for
            // that speaking opportunity; the unbounded full document is absent.
            for (var i = 0; i < retrievals.Count; i++)
            {
                var prompt = model.Prompts[i];
                var item = retrievals[i];
                Assert.Contains("# Source evidence retrieved for this speaking turn", prompt);
                foreach (var id in item.PassageIds)
                    Assert.Contains(id, prompt);
                foreach (var passage in source.Passages)
                {
                    if (item.PassageIds.Contains(passage.PassageId))
                        Assert.Contains(passage.Text, prompt);
                    else
                        Assert.DoesNotContain($"[Passage {passage.PassageId}]", prompt);
                }
            }

            var transcript = TranscriptRenderer.RenderTranscript(store, session, Laboratory.Cast);
            Assert.Contains("local lexical RAG", transcript);
            Assert.Contains("_Retrieved before this turn:", transcript);

            var diagnostics = TranscriptRenderer.RenderDiagnostics(
                store, session, result, model.ModelName, model.ModelDigest);
            using var parsed = JsonDocument.Parse(diagnostics);
            var evidence = parsed.RootElement.GetProperty("sourceEvidence");
            Assert.True(evidence.GetProperty("onDemandRetrieval").GetBoolean());
            Assert.False(evidence.GetProperty("fullDocumentRetrieval").GetBoolean());
            Assert.Equal(retrievals.Count, evidence.GetProperty("turnRetrievals").GetArrayLength());

            // The source text itself is never transformed into interpersonal
            // recollections by the retrieval path.
            Assert.Null(store.GetSessionEvidence(SessionId.New()));
        }

        using (var reopened = Open())
        {
            var recovered = reopened.GetOpportunityRetrievals(session);
            Assert.Equal(retrievals.Select(x => x.Opportunity),
                recovered.Select(x => x.Opportunity));
            Assert.Equal(retrievals.SelectMany(x => x.PassageIds),
                recovered.SelectMany(x => x.PassageIds));
            Assert.Equal(EvidenceContextVersions.DynamicLexicalRetrieval,
                reopened.GetSession(session)!.ContextVersion);
        }
    }

    [Fact]
    public void Wrong_source_and_post_inference_retrieval_are_rejected()
    {
        var source = Source();
        var other = EvidenceSnapshotFactory.Capture(
            source.Descriptor with { Title = "Different file" }, source.SnapshotText);
        var briefing = EvidenceBriefingBuilder.BuildForRetrieval(source);
        using var store = Open();
        store.SaveEvidenceSnapshot(source);
        store.SaveEvidenceSnapshot(other);
        var session = Laboratory.Seed(
            store, SessionMode.FreshEncounter, "stub/digest", briefing,
            EvidenceBriefingBuilder.RetrievalContextVersion);
        store.PinSessionEvidenceForRetrieval(session, source.SnapshotId);

        var opp = new SpeakingOpportunity(
            OpportunityId.New(), session, Laboratory.Arthur.Id,
            OpportunityReason.Opening, 0, OpportunityStatus.Offered);
        store.SaveOpportunity(opp, 0);
        var passages = source.Passages.Take(2).ToArray();
        Assert.Throws<InvalidOperationException>(() =>
            store.SaveOpportunityRetrieval(
                opp, other.SnapshotId, "lexical", "airport", passages, new[] { 1d, 0d }));

        store.SaveOpportunityRetrieval(
            opp, source.SnapshotId, "lexical", "airport", passages, new[] { 1d, 0d });
        Assert.Throws<InvalidOperationException>(() =>
            store.SaveOpportunityRetrieval(
                opp, source.SnapshotId, "lexical", "changed query", passages, new[] { 1d, 0d }));

        store.SaveAttempt(new ModelAttempt(
            AttemptId.New(), opp.Id, Laboratory.Arthur.Id,
            "test", "digest", 1, AttemptOutcome.ValidPass, null,
            DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() =>
            store.SaveOpportunityRetrieval(
                opp, source.SnapshotId, "lexical", "airport", passages, new[] { 1d, 0d }));
    }

    [Fact]
    public void Retrieval_mode_cannot_run_without_provider_or_use_static_pin()
    {
        var source = Source();
        var briefing = EvidenceBriefingBuilder.BuildForRetrieval(source);
        using var store = Open();
        store.SaveEvidenceSnapshot(source);
        var session = Laboratory.Seed(
            store, SessionMode.FreshEncounter, "stub/digest", briefing,
            EvidenceBriefingBuilder.RetrievalContextVersion);

        Assert.Throws<InvalidOperationException>(() =>
            store.PinSessionEvidence(session, source.SnapshotId,
                new[] { source.Passages[0].PassageId }));
        store.PinSessionEvidenceForRetrieval(session, source.SnapshotId);
        Assert.Throws<InvalidOperationException>(() =>
            new ConversationEngine(
                store, new CapturingModel(), new ParticipantContextBuilder(),
                session, Laboratory.Cast, briefing));
    }

    private sealed class CapturingModel : IModelClient
    {
        public string ModelName => "recording";
        public string ModelDigest => "digest";
        public List<string> Prompts { get; } = new();

        public ModelResponse Decide(
            ModelRequest request, CancellationToken cancellationToken = default)
        {
            Prompts.Add(request.Prompt);
            return Prompts.Count switch
            {
                1 => StubResponses.Speak("Who will reimburse travelers for flights?"),
                2 => StubResponses.Speak("Are passenger refunds addressed in the document?"),
                3 => StubResponses.Speak("I wonder about controller communications."),
                _ => StubResponses.Pass()
            };
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_db)) File.Delete(_db);
    }
}

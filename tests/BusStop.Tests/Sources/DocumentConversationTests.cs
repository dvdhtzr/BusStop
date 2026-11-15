using BusStop.Console;
using BusStop.Context;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Models;
using BusStop.Persistence;
using BusStop.Sources;
using Microsoft.Data.Sqlite;

namespace BusStop.Tests.Sources;

public sealed class DocumentConversationTests : IDisposable
{
    private readonly string _db = Path.Combine(
        Path.GetTempPath(), "busstop-r1b-" + Guid.NewGuid().ToString("N") + ".db");

    private static EvidenceSnapshot Source(string text) =>
        EvidenceSnapshotFactory.Capture(
            new EvidenceSourceDescriptor(
                EvidenceSourceKind.UserDocument,
                "FAA shutdown discussion fixture",
                "User-provided local document",
                null, null,
                "User submitted for private local discussion"),
            text, capturedAt: DateTimeOffset.Parse("2026-11-15T08:00:00Z"));

    private ConversationStore Open()
    {
        var store = ConversationStore.OpenFile(_db);
        store.InitializeSchema();
        return store;
    }

    [Fact]
    public void Snapshot_and_session_pin_survive_database_reopen_without_losing_passage_text()
    {
        var snapshot = Source(string.Concat(Enumerable.Repeat(
            "Air traffic controllers discussed the two-hour detour. ", 125)));
        var (briefing, ids) = EvidenceBriefingBuilder.Build(snapshot);
        SessionId session;

        using (var store = Open())
        {
            store.SaveEvidenceSnapshot(snapshot);
            store.SaveEvidenceSnapshot(snapshot); // idempotent exact re-capture
            session = Laboratory.Seed(
                store, SessionMode.FreshEncounter, "stub/digest",
                briefing, EvidenceBriefingBuilder.ContextVersion);
            store.PinSessionEvidence(session, snapshot.SnapshotId, ids);
            store.PinSessionEvidence(session, snapshot.SnapshotId, ids); // same pin allowed pre-run
        }

        using (var reopened = Open())
        {
            var saved = reopened.GetEvidenceSnapshot(snapshot.SnapshotId);
            Assert.NotNull(saved);
            Assert.Equal(snapshot.Descriptor, saved!.Descriptor);
            Assert.Equal(snapshot.TextSha256, saved.TextSha256);
            Assert.Equal(snapshot.SnapshotText, saved.SnapshotText);
            Assert.Equal(snapshot.Passages, saved.Passages);

            var pin = reopened.GetSessionEvidence(session);
            Assert.NotNull(pin);
            Assert.Equal(snapshot.SnapshotId, pin!.SnapshotId);
            Assert.Equal(briefing.Id, pin.Briefing);
            Assert.Equal(ids, pin.ExposedPassageIds);
            Assert.Equal(EvidenceBriefingBuilder.ContextVersion,
                reopened.GetSession(session)!.ContextVersion);
        }
    }

    [Fact]
    public void Rechunking_cannot_mutate_existing_frozen_evidence()
    {
        const string text = "The bridge is closed. The road is crowded. " +
                            "The bridge is closed. The road is crowded. " +
                            "The bridge is closed. The road is crowded.";
        var initial = Source(text);
        var differentChunking = EvidenceSnapshotFactory.Capture(
            initial.Descriptor, text, passageCharacters: 40, overlapCharacters: 4);

        Assert.Equal(initial.SnapshotId, differentChunking.SnapshotId);
        Assert.NotEqual(initial.Passages.Count, differentChunking.Passages.Count);

        using var store = Open();
        store.SaveEvidenceSnapshot(initial);
        Assert.Throws<InvalidOperationException>(() =>
            store.SaveEvidenceSnapshot(differentChunking));
        var stored = store.GetEvidenceSnapshot(initial.SnapshotId)!;
        Assert.Equal(initial.Passages, stored.Passages);
    }

    [Fact]
    public void Session_pin_rejects_unrelated_passages_and_cannot_change_after_speech()
    {
        var snapshot = Source(new string('A', 7000));
        var (briefing, ids) = EvidenceBriefingBuilder.Build(snapshot);
        using var store = Open();
        store.SaveEvidenceSnapshot(snapshot);
        var session = Laboratory.Seed(
            store, SessionMode.FreshEncounter, "stub/digest", briefing,
            EvidenceBriefingBuilder.ContextVersion);

        Assert.Throws<InvalidOperationException>(() =>
            store.PinSessionEvidence(session, snapshot.SnapshotId, new[] { "other:p00001" }));
        Assert.Null(store.GetSessionEvidence(session));

        store.PinSessionEvidence(session, snapshot.SnapshotId, ids);
        Assert.Throws<InvalidOperationException>(() =>
            store.PinSessionEvidence(session, snapshot.SnapshotId, new[] { ids[0] }));
        Assert.Equal(ids, store.GetSessionEvidence(session)!.ExposedPassageIds);

        // Inference isn't needed to test the no-rebind-after-first-turn rule.
        var opp = new SpeakingOpportunity(
            OpportunityId.New(), session, Laboratory.Arthur.Id,
            OpportunityReason.Opening, 0, OpportunityStatus.Offered);
        store.SaveOpportunity(opp, ordinal: 0);
        Assert.Throws<InvalidOperationException>(() =>
            store.PinSessionEvidence(session, snapshot.SnapshotId, ids));
    }

    [Fact]
    public void Model_sees_exactly_the_bounded_source_and_transcript_records_what_was_visible()
    {
        var longText = string.Concat(Enumerable.Repeat(
            "A bridge-related public discussion includes several questions. ", 200))
            + "UNSEEN_DOCUMENT_TAIL";
        var source = Source(longText);
        var (briefing, ids) = EvidenceBriefingBuilder.Build(source);
        Assert.True(source.Passages.Count > ids.Count);
        Assert.DoesNotContain("UNSEEN_DOCUMENT_TAIL", briefing.Body);

        using var store = Open();
        store.SaveEvidenceSnapshot(source);
        var session = Laboratory.Seed(
            store, SessionMode.FreshEncounter, "recording/digest", briefing,
            EvidenceBriefingBuilder.ContextVersion);
        store.PinSessionEvidence(session, source.SnapshotId, ids);

        var client = new PromptCapturingModel();
        var engine = new ConversationEngine(
            store, client, new ParticipantContextBuilder(),
            session, Laboratory.Cast, briefing);
        var result = engine.Run();

        Assert.NotEmpty(client.Prompts);
        Assert.All(client.Prompts, prompt =>
        {
            Assert.Contains(ids[0], prompt);
            Assert.Contains("Visible source passages: 4 of", prompt);
            Assert.DoesNotContain("UNSEEN_DOCUMENT_TAIL", prompt);
        });
        Assert.Equal(TerminationReason.NoEngagement, result.Reason);

        var transcript = TranscriptRenderer.RenderTranscript(
            store, session, Laboratory.Cast);
        Assert.Contains(source.SnapshotId, transcript);
        Assert.Contains(ids[0], transcript);
        Assert.Contains("no live RAG retrieval", transcript);

        var diagnostics = TranscriptRenderer.RenderDiagnostics(
            store, session, result, client.ModelName, client.ModelDigest);
        Assert.Contains(source.SnapshotId, diagnostics);
        Assert.Contains("fullDocumentRetrieval", diagnostics);
        Assert.Contains("false", diagnostics);
    }

    private sealed class PromptCapturingModel : IModelClient
    {
        public string ModelName => "recording";
        public string ModelDigest => "digest";
        public List<string> Prompts { get; } = new();

        public ModelResponse Decide(
            ModelRequest request, CancellationToken cancellationToken = default)
        {
            Prompts.Add(request.Prompt);
            return StubResponses.Pass();
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_db)) File.Delete(_db);
    }
}

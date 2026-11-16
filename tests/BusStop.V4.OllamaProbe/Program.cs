using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Domain;
using BusStop.Engine.Verification;
using BusStop.Models;
using BusStop.Moderation.Verification;
using BusStop.Persistence;

// Manual, explicitly invoked V4 laboratory only. NEVER added to the
// conversation application, startup, default test suite or live model worker.
if (args.Length != 4 || args[0] != "--model" || args[2] != "--audit")
{
    Console.Error.WriteLine(
        "Usage: --model <local-ollama-model:tag> --audit <private-local-json-path>");
    return 2;
}

var name = args[1];
var audit = Path.GetFullPath(args[3]);
if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(args[3]))
    throw new ArgumentException("Model and explicit local private audit path are required.");

if (!OllamaAdapter.IsAvailable())
    throw new InvalidOperationException(
        "Local Ollama is unavailable at http://localhost:11434. No network fallback is permitted.");

using var model = new OllamaAdapter(name,
    temperature: 0.1, think: false, timeout: TimeSpan.FromMinutes(4));
if (string.IsNullOrWhiteSpace(model.ModelDigest) ||
    model.ModelDigest == "unknown")
    throw new InvalidOperationException(
        "The local model digest could not be pinned; V4 acceptance refuses an unpinned run.");

var database = Path.Combine(Path.GetTempPath(),
    "busstop-v4-" + Guid.NewGuid().ToString("N") + ".db");

try
{
    using var db = ConversationStore.OpenFile(database);
    db.InitializeSchema();

    var arthur = new CharacterProfile(
        CharacterId.New(), 1, "Arthur", "A fictional retired neighbor.", new[] { "curious" });
    var bea = new CharacterProfile(
        CharacterId.New(), 1, "Bea", "A fictional local resident.", new[] { "practical" });
    db.SaveCharacter(arthur);
    db.SaveCharacter(bea);
    var briefing = new Briefing(
        BriefingId.New(), "Airport operations", "local test fixture",
        "local-evidence:synthetic-v4", "The airport authority recorded 3 outages and 3 delays.");
    db.SaveBriefing(briefing);

    var session = new Session(
        SessionId.New(), new[] { arthur.Id, bea.Id },
        SessionMode.FreshEncounter, briefing.Id,
        "floor-2", "v4-lab", "v4-lab", SessionStatus.Active,
        DateTimeOffset.UtcNow, null);
    db.CreateSession(session);

    var pinned = new V4SessionPolicy(
        EvidenceVerifierV4.ContractVersion, VerificationOperatingMode.Shadow,
        V3ClaimDetector.RuleVersion, V3ModelClaimExtractor.PromptVersion,
        EvidenceVerifierV4.VerificationPromptVersion,
        V3EvidencePacketAssembler.RetrievalSelectorVersion,
        EvidenceVerifierV4.VerificationSchemaVersion,
        model.ModelName, model.ModelDigest, 0.1, false,
        MaxSessionCalls: 8, MaxCallsPerUtterance: 3,
        MaxVerificationCallsPerUtterance: 2,
        MaxGeneratedTokens: 2_048, ContextTokens: 8_192);
    db.PinV4VerifierPolicy(session.Id, pinned);

    Utterance Speak(CharacterProfile speaker, string text)
    {
        var boundary = db.MaxSequence(session.Id);
        var opportunity = new SpeakingOpportunity(
            OpportunityId.New(), session.Id, speaker.Id,
            OpportunityReason.FairOrder, boundary, OpportunityStatus.Offered);
        db.SaveOpportunity(opportunity, (int)boundary);
        return db.CommitUtterance(opportunity.Id, session.Id,
            speaker.Id, text, null, null, DateTimeOffset.UtcNow);
    }

    string Hash(string text) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    (V4ReviewReport Report, V3PacketAssembly Packets) Assess(Utterance utterance)
    {
        var job = new VerificationJobIdentity(
            Guid.NewGuid(), utterance.Session, utterance.Opportunity,
            utterance.Id, utterance.Speaker, utterance.Sequence - 1,
            EvidenceVerifierV1.ContractVersion, Hash(utterance.Text));
        var extracted = V3ClaimDetector.Detect(job, utterance);
        var packets = new V3EvidencePacketAssembler(db).Assemble(
            job, utterance, extracted);
        var result = new V4GroundedVerifier(db).Verify(
            job, utterance, extracted, packets, model);
        return (result, packets);
    }

    var sourceStatement = Speak(arthur, "The official report recorded 12 outages.");
    var source = Assess(sourceStatement);

    var autobiography = Speak(arthur, "I once worked as a commercial pilot.");
    var identity = Assess(autobiography);

    _ = Speak(bea, "I said there were only 3 flight delays.");
    var attribution = Speak(arthur, "Bea said there were 12 flight delays.");
    var dialogue = Assess(attribution);

    var cases = new[]
    {
        new { Name = "source-fact", Speech = sourceStatement.Text, Report = source.Report,
            Packets = source.Packets },
        new { Name = "unanchored-autobiography", Speech = autobiography.Text, Report = identity.Report,
            Packets = identity.Packets },
        new { Name = "dialogue-attribution", Speech = attribution.Text, Report = dialogue.Report,
            Packets = dialogue.Packets }
    };

    Directory.CreateDirectory(Path.GetDirectoryName(audit)
        ?? throw new InvalidDataException("Cannot determine private audit directory."));
    File.WriteAllText(audit, JsonSerializer.Serialize(new
    {
        contractVersion = EvidenceVerifierV4.ContractVersion,
        atUtc = DateTimeOffset.UtcNow,
        localOnly = true,
        stage = "isolated owner-review laboratory",
        modelName = model.ModelName,
        modelDigest = model.ModelDigest,
        policy = pinned,
        observations = cases,
        sharedReservations = db.GetModeratorReservations(session.Id),
        note = "Private acceptance sample: shape/scope pass is not semantic proof. " +
               "Owner must review cited claim-level outcomes. No participant received any findings."
    }, new JsonSerializerOptions { WriteIndented = true }));

    Console.WriteLine("V4 isolated Ollama probe — PRIVATE, no participant path");
    Console.WriteLine("Model: " + model.ModelName);
    Console.WriteLine("Digest: " + model.ModelDigest);
    foreach (var sample in cases)
        Console.WriteLine(sample.Name + ": " +
            "verified=" + sample.Report.Summary.VerifiedClaims +
            " contradicted=" + sample.Report.Summary.ConfirmedContradictions +
            " insufficient=" + sample.Report.Summary.InsufficientEvidence +
            " failed=" + sample.Report.Summary.FailedClaims +
            " skipped=" + sample.Report.Summary.SkippedForBudget +
            " modelAttempts=" + sample.Report.VerificationAttemptsThisRun);

    Console.WriteLine("Private owner-review audit written: " + audit);

    if (cases.SelectMany(c => c.Report.ClaimResults)
        .Any(r => r.Status is VerificationOperationalStatus.TransportFailure or
             VerificationOperationalStatus.InvalidResponse or
             VerificationOperationalStatus.IncompatibleCheckpoint or
             VerificationOperationalStatus.Error))
    {
        Console.Error.WriteLine(
            "V4 OLLAMA TECHNICAL RED: one or more responses were invalid or unavailable.");
        return 1;
    }

    if (identity.Report.Summary.ConfirmedContradictions != 0)
    {
        Console.Error.WriteLine(
            "V4 OLLAMA RED: unsupported autobiography produced a material contradiction.");
        return 1;
    }

    Console.WriteLine(
        "V4 OLLAMA TECHNICAL GREEN: inspect the PRIVATE audit for semantic findings.");
    return 0;
}
finally
{
    // The database is a disposable isolated fixture; the explicit audit path
    // is the only retained private artifact.
    try { File.Delete(database); } catch (IOException) { }
}

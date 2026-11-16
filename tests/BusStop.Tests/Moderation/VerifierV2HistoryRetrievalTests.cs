using System.Security.Cryptography;
using System.Text;
using BusStop.Domain;
using BusStop.Moderation.Verification;
using BusStop.Persistence;

namespace BusStop.Tests.Moderation;

/// <summary>
/// Local, model-free contract and SQLite migration tests. These verifier-only
/// fixtures intentionally do NOT use ConversationEngine or participant prompts.
/// </summary>
public sealed class VerifierV2HistoryRetrievalTests
{
    private static ConversationStore Open()
    {
        var store = new ConversationStore("Data Source=:memory:");
        store.InitializeSchema();
        return store;
    }

    private static CharacterProfile Profile(string name, string persona = "No typed anchors.") =>
        new(CharacterId.New(), 1, name, persona, new[] { "thoughtful" });

    private static Session SeedSession(ConversationStore db,
        IReadOnlyList<CharacterProfile> cast, Briefing briefing, DateTimeOffset created)
    {
        foreach (var p in cast) db.SaveCharacter(p);
        db.SaveBriefing(briefing);
        var session = new Session(
            SessionId.New(), cast.Select(x => x.Id).ToArray(),
            SessionMode.FreshEncounter, briefing.Id,
            "floor-2", "fixture", "fixture", SessionStatus.Active, created, null);
        db.CreateSession(session);
        return session;
    }

    private static Briefing Brief(string text = "The report lists three outages.") =>
        new(BriefingId.New(), "Report", "fixture", "fixture:report", text);

    private static Utterance Speak(ConversationStore db, Session session,
        CharacterProfile speaker, string text, DateTimeOffset? time = null)
    {
        var boundary = db.MaxSequence(session.Id);
        var opportunity = new SpeakingOpportunity(
            OpportunityId.New(), session.Id, speaker.Id,
            OpportunityReason.FairOrder, boundary, OpportunityStatus.Offered);
        db.SaveOpportunity(opportunity, (int)boundary);
        return db.CommitUtterance(
            opportunity.Id, session.Id, speaker.Id, text, null, null,
            time ?? DateTimeOffset.UtcNow);
    }

    private static VerificationJobIdentity Job(Utterance u) => new(
        Guid.NewGuid(), u.Session, u.Opportunity, u.Id, u.Speaker,
        u.Sequence - 1, EvidenceVerifierV1.ContractVersion,
        Hash(u.Text));

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static CharacterHistoryEvent Proposed(
        CharacterProfile subject, Utterance from, string key, string statement,
        CharacterHistoryEventKind kind = CharacterHistoryEventKind.Provisional,
        Guid? previous = null, string? review = null) => new(
            Guid.NewGuid(), subject.Id, key, statement,
            from.Session, from.Id, from.Sequence, kind, previous, review,
            // Never trusted as the registration timestamp: the host overrides it.
            DateTimeOffset.MinValue);

    [Fact]
    public void Legacy_profiles_have_no_inferred_binding_anchors_and_migrate_idempotently()
    {
        using var db = Open();
        db.InitializeSchema(); // additive migrations can reopen older SQLite
        var a = Profile("Arthur", "Absolutely never had children.");
        var b = Profile("Bea");
        var session = SeedSession(db, new[] { a, b }, Brief(), DateTimeOffset.UtcNow);
        var target = Speak(db, session, a, "Actually, I have two children.");

        var own = db.SelectVerifierEvidence(
            Job(target), target, ClaimCategory.CharacterIdentity);
        Assert.Empty(own.Scope.AuthorizedItems);
        Assert.Equal(EvidenceCompletenessStatus.CompleteForDeclaredScope,
            own.Completeness.Status);
        // No typed pin and no owner-approved anchor = zero binding canon.
    }

    [Fact]
    public void Explicit_owner_seals_are_immutable_and_old_versions_cannot_repin()
    {
        using var db = Open();
        var a = Profile("Arthur");
        var b = Profile("Bea");
        var set = new[]
        {
            new CharacterAnchor(a.Id, 1, "occupation", "machinist",
                CharacterAnchorExposure.SubjectOnly)
        };
        db.SaveCharacter(a);
        db.SealVerifierAnchorSet(a.Id, 1, set, "owner-review:1");
        db.SealVerifierAnchorSet(a.Id, 1, set, "owner-review:1");

        Assert.Throws<InvalidOperationException>(() =>
            db.SealVerifierAnchorSet(a.Id, 1,
                new[] { set[0] with { Value = "pilot" } }, "owner-review:1"));
        Assert.Throws<InvalidDataException>(() =>
            db.SealVerifierAnchorSet(a.Id, 1, set, " "));

        var session = SeedSession(db, new[] { a, b }, Brief(), DateTimeOffset.UtcNow);
        db.PinVerifierCharacterVersion(session.Id, a.Id, 1);
        db.PinVerifierCharacterVersion(session.Id, a.Id, 1);
        Assert.Throws<InvalidOperationException>(() =>
            db.SealVerifierAnchorSet(b.Id, 1, Array.Empty<CharacterAnchor>(), "owner-review:2")
            // This version is not pinned yet: it is legal to seal an empty set.
        );
    }

    [Fact]
    public void Other_persons_private_anchors_are_never_part_of_speaker_scope()
    {
        using var db = Open();
        var a = Profile("Arthur");
        var b = Profile("Bea");
        db.SaveCharacter(a);
        db.SaveCharacter(b);
        db.SealVerifierAnchorSet(b.Id, 1, new[]
        {
            new CharacterAnchor(b.Id, 1, "private-family", "has a daughter",
                CharacterAnchorExposure.SubjectOnly),
            new CharacterAnchor(b.Id, 1, "shared-role", "violinist",
                CharacterAnchorExposure.SharedWithRoom)
        }, "owner:beas-profile");

        var session = SeedSession(db, new[] { a, b }, Brief(), DateTimeOffset.UtcNow);
        db.PinVerifierCharacterVersion(session.Id, b.Id, 1);
        var target = Speak(db, session, a, "Bea said she is a violinist.");
        var seen = db.SelectVerifierEvidence(
            Job(target), target, ClaimCategory.CharacterIdentity, b.Id);
        Assert.Single(seen.Scope.AuthorizedItems);
        Assert.Contains(seen.Scope.AuthorizedItems.Values,
            x => x.Excerpt.Contains("violinist", StringComparison.Ordinal));
        Assert.DoesNotContain(seen.Scope.AuthorizedItems.Values,
            x => x.Excerpt.Contains("daughter", StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() => db.SelectVerifierEvidence(
            Job(target), target, ClaimCategory.CharacterIdentity, CharacterId.New()));
    }

    [Fact]
    public void Anchors_are_session_pinned_and_cannot_be_added_after_first_turn()
    {
        using var db = Open();
        var a = Profile("Arthur");
        var b = Profile("Bea");
        db.SaveCharacter(a);
        db.SaveCharacter(b);
        db.SealVerifierAnchorSet(a.Id, 1, new[]
        {
            new CharacterAnchor(a.Id, 1, "job", "machinist",
                CharacterAnchorExposure.SubjectOnly)
        }, "owner:1");
        var session = SeedSession(db, new[] { a, b }, Brief(), DateTimeOffset.UtcNow);
        db.PinVerifierCharacterVersion(session.Id, a.Id, 1);
        var target = Speak(db, session, a, "I flew passenger jets.");
        var own = db.SelectVerifierEvidence(
            Job(target), target, ClaimCategory.CharacterIdentity);
        Assert.Contains(own.Scope.AuthorizedItems.Values,
            x => x.AuthorityTier == EvidenceAuthorityTier.BindingAnchor);

        var next = a with { Version = 2, Persona = "Different persona." };
        db.SaveCharacter(next);
        db.SealVerifierAnchorSet(a.Id, 2, new[]
        {
            new CharacterAnchor(a.Id, 2, "job", "pilot",
                CharacterAnchorExposure.SubjectOnly)
        }, "owner:2");

        var same = db.SelectVerifierEvidence(
            Job(target), target, ClaimCategory.CharacterIdentity);
        Assert.Contains(same.Scope.AuthorizedItems.Values,
            x => x.Excerpt.Contains("machinist", StringComparison.Ordinal));
        Assert.DoesNotContain(same.Scope.AuthorizedItems.Values,
            x => x.Excerpt.Contains("pilot", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() =>
            db.PinVerifierCharacterVersion(session.Id, a.Id, 2));
    }

    [Fact]
    public void Source_lane_exposes_only_session_pinned_passages_not_the_snapshot_universe()
    {
        using var db = Open();
        var a = Profile("Arthur");
        var b = Profile("Bea");
        const string sourceText = "Public first passage. Unexposed secret second passage.";
        var hash = Hash(sourceText);
        var snapshot = new EvidenceSnapshot(
            hash, new EvidenceSourceDescriptor(
                EvidenceSourceKind.UserDocument, "document", "fixture",
                null, null, "test-only"),
            hash, DateTimeOffset.UtcNow, sourceText,
            new[]
            {
                new EvidencePassage($"{hash}:p00001", 1, 0,
                    "Public first passage.".Length, "Public first passage."),
                new EvidencePassage($"{hash}:p00002", 2,
                    sourceText.IndexOf("Unexposed", StringComparison.Ordinal),
                    "Unexposed secret second passage.".Length,
                    "Unexposed secret second passage.")
            });
        var brief = new Briefing(BriefingId.New(), "Document",
            "fixture", $"local-evidence:{hash}", "Public first passage.");
        var session = SeedSession(db, new[] { a, b }, brief, DateTimeOffset.UtcNow);
        db.SaveEvidenceSnapshot(snapshot);
        db.PinSessionEvidence(session.Id, hash, new[] { snapshot.Passages[0].PassageId });

        var target = Speak(db, session, a, "The document says something else.");
        var result = db.SelectVerifierEvidence(Job(target), target, ClaimCategory.SourceFact);
        Assert.Contains(result.Scope.AuthorizedItems.Values,
            x => x.Kind == EvidenceItemKind.Passage &&
                 x.Excerpt == "Public first passage.");
        Assert.DoesNotContain(result.Scope.AuthorizedItems.Values,
            x => x.Excerpt.Contains("secret", StringComparison.Ordinal));
        Assert.Equal(EvidenceCompletenessStatus.CompleteForDeclaredScope,
            result.Completeness.Status);
        Assert.Equal(2, result.Scope.AuthorizedItems.Count);
    }

    [Fact]
    public void Speaker_history_requires_committed_self_claim_and_reviewed_transitions()
    {
        using var db = Open();
        var a = Profile("Arthur");
        var b = Profile("Bea");
        var session = SeedSession(db, new[] { a, b }, Brief(),
            DateTimeOffset.UtcNow.AddHours(-1));

        var first = Speak(db, session, a, "I worked at a shipyard.",
            DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Throws<InvalidDataException>(() =>
            db.RecordVerifierHistoryEvent(Proposed(b, first, "work", first.Text)));
        Assert.Throws<InvalidDataException>(() =>
            db.RecordVerifierHistoryEvent(Proposed(a, first, "work", "I flew planes.")));

        var p = db.RecordVerifierHistoryEvent(Proposed(a, first, "work", first.Text));
        Assert.NotEqual(DateTimeOffset.MinValue, p.RecordedAt);

        var again = Speak(db, session, a, "Yes, I really did work at the shipyard.");
        Assert.Throws<InvalidDataException>(() =>
            db.RecordVerifierHistoryEvent(Proposed(a, again, "work", again.Text,
                CharacterHistoryEventKind.ReviewedSelfReaffirmation, p.EventId)));

        var established = db.RecordVerifierHistoryEvent(Proposed(a, again, "work", again.Text,
            CharacterHistoryEventKind.ReviewedSelfReaffirmation, p.EventId, "owner:confirmed-reuse"));
        var beforeCorrection = Speak(db, session, a, "This is before I correct myself.");
        var observedBefore = db.SelectVerifierEvidence(
            Job(beforeCorrection), beforeCorrection, ClaimCategory.PersonalHistory);
        Assert.Contains(observedBefore.Scope.AuthorizedItems.Values,
            x => x.ReferenceId == $"history:{established.EventId:D}" &&
                 x.AuthorityTier == EvidenceAuthorityTier.EstablishedRecollection);
        Assert.Contains(observedBefore.Scope.AuthorizedItems.Values,
            x => x.ReferenceId == $"history:{p.EventId:D}" &&
                 x.IsSuperseded);

        var corrected = Speak(db, session, a, "Correction: it was a foundry, not a shipyard.");
        var correction = db.RecordVerifierHistoryEvent(Proposed(a, corrected,
            "work", corrected.Text, CharacterHistoryEventKind.ReviewedSelfCorrection,
            established.EventId, "owner:explicit-correction"));

        var viewAgain = db.SelectVerifierEvidence(
            Job(beforeCorrection), beforeCorrection, ClaimCategory.PersonalHistory);
        Assert.DoesNotContain(viewAgain.Scope.AuthorizedItems.Values,
            x => x.ReferenceId == $"history:{correction.EventId:D}");

        var afterCorrection = Speak(db, session, a, "That's the corrected history.");
        var view = db.SelectVerifierEvidence(
            Job(afterCorrection), afterCorrection, ClaimCategory.PersonalHistory);
        Assert.Contains(view.Scope.AuthorizedItems.Values,
            x => x.ReferenceId == $"history:{correction.EventId:D}" &&
                 x.AuthorityTier == EvidenceAuthorityTier.ProvisionalRecollection);
        Assert.Contains(view.Scope.AuthorizedItems.Values,
            x => x.ReferenceId == $"history:{established.EventId:D}" &&
                 x.AuthorityTier == EvidenceAuthorityTier.SupersededRecollection);
        Assert.ThrowsAny<Exception>(() =>
            db.RecordVerifierHistoryEvent(Proposed(a, afterCorrection,
                "work", afterCorrection.Text, CharacterHistoryEventKind.ReviewedSelfCorrection,
                established.EventId, "owner:second-fork")));
    }

    [Fact]
    public void Dialogue_visibility_is_limited_to_previous_turns_and_identical_cast_history()
    {
        using var db = Open();
        var a = Profile("Arthur");
        var b = Profile("Bea");
        var outsider = Profile("Outsider");
        var briefing = Brief();
        var past = SeedSession(db, new[] { a, b }, briefing,
            DateTimeOffset.UtcNow.AddDays(-2));
        var prior = Speak(db, past, b, "Bea said: it was Tuesday.",
            DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(1));
        db.UpdateSessionStatus(past.Id, SessionStatus.Ended,
            DateTimeOffset.UtcNow.AddDays(-1));

        var privatePast = SeedSession(db, new[] { a, outsider }, briefing,
            DateTimeOffset.UtcNow.AddDays(-2));
        var excluded = Speak(db, privatePast, outsider, "Other-room secret.",
            DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(2));
        db.UpdateSessionStatus(privatePast.Id, SessionStatus.Ended,
            DateTimeOffset.UtcNow.AddDays(-1));

        var current = SeedSession(db, new[] { a, b }, briefing,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var heard = Speak(db, current, b, "I agree with Bea's earlier remark.");
        var target = Speak(db, current, a, "Bea just said she agreed.");
        var future = Speak(db, current, b, "Not yet visible.");

        var selected = db.SelectVerifierEvidence(Job(target), target,
            ClaimCategory.DialogueAttribution);
        Assert.Contains(selected.Scope.AuthorizedItems.Values,
            x => x.SourceId == prior.Id.ToString());
        Assert.Contains(selected.Scope.AuthorizedItems.Values,
            x => x.SourceId == heard.Id.ToString());
        Assert.DoesNotContain(selected.Scope.AuthorizedItems.Values,
            x => x.SourceId == future.Id.ToString() ||
                 x.SourceId == excluded.Id.ToString());
        Assert.Equal(EvidenceCompletenessStatus.CompleteForDeclaredScope,
            selected.Completeness.Status);
    }

    [Fact]
    public void Bounded_selection_reports_partial_coverage_not_negative_proof()
    {
        using var db = Open();
        var a = Profile("Arthur");
        var b = Profile("Bea");
        var session = SeedSession(db, new[] { a, b }, Brief(), DateTimeOffset.UtcNow);
        Speak(db, session, b, "First.");
        Speak(db, session, b, "Second.");
        var target = Speak(db, session, a, "You said there were two previous turns.");
        var selected = db.SelectVerifierEvidence(Job(target), target,
            ClaimCategory.DialogueAttribution, maxItems: 1);
        Assert.Single(selected.Scope.AuthorizedItems);
        Assert.Equal(EvidenceCompletenessStatus.Partial,
            selected.Completeness.Status);
        Assert.NotNull(selected.Completeness.ExcludedRanges);
    }
}

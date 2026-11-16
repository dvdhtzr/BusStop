using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using BusStop.Domain;
using BusStop.Moderation.Verification;

namespace BusStop.Persistence;

/// <summary>
/// An authorized, as-of host scope for later V3 packet construction. No prompt,
/// Ollama call, or participant memory is created here.
/// </summary>
public sealed record VerifierEvidenceSelection(
    VerificationEvidenceScope Scope,
    EvidenceCompleteness Completeness);

public sealed partial class ConversationStore
{
    /// <summary>
    /// Build only evidence permitted to the observed speaker at the ORIGINAL
    /// committed boundary. A selected subject different from the speaker may
    /// reveal only intentionally room-shared typed anchors, never private history.
    /// Missing anchors are never inferred from the free-text Persona.
    ///
    /// Completeness is strictly limited to the declared host search universe;
    /// no absent result proves that a life event never happened.
    /// </summary>
    public VerifierEvidenceSelection SelectVerifierEvidence(
        VerificationJobIdentity job, Utterance observed, ClaimCategory category,
        CharacterId? subject = null, int maxItems = 16)
    {
        if (maxItems is < 1 or > 32 ||
            !Enum.IsDefined(typeof(ClaimCategory), category))
            throw new InvalidDataException("Invalid verifier retrieval selection limits.");

        EvidenceVerificationValidator.ValidateJob(job, observed);
        var actual = GetCommittedUtterances(observed.Session)
            .SingleOrDefault(x => x.Id == observed.Id);
        if (actual is null || actual != observed)
            throw new InvalidDataException(
                "Verifier retrieval requires an actual unchanged committed utterance.");

        var session = GetSession(observed.Session)
            ?? throw new InvalidDataException("Missing verifier session.");
        if (!session.Participants.Contains(job.SpeakerId))
            throw new InvalidDataException("Observed speaker does not belong to session.");

        var targetSubject = subject ?? job.SpeakerId;
        if (!session.Participants.Contains(targetSubject))
            throw new InvalidDataException("Claim subject is not in the observed room.");

        var choices = new List<EvidenceItem>();
        var clipped = false;
        string universe;

        switch (category)
        {
            case ClaimCategory.SourceFact:
                universe = "session briefing and explicitly exposed source passages";
                AddSourceEvidence(session, observed, choices, ref clipped);
                break;

            case ClaimCategory.CharacterIdentity:
            case ClaimCategory.PersonalHistory:
                universe = targetSubject == job.SpeakerId
                    ? "speaker's pinned anchors and reviewed/provisional own-history events as of speech"
                    : "room-shared typed anchors for the named participant only";
                AddAnchors(session, targetSubject, job.SpeakerId, choices, ref clipped);
                if (targetSubject == job.SpeakerId)
                    AddOwnHistory(session, observed, job, choices, ref clipped);
                break;

            case ClaimCategory.DialogueAttribution:
                universe = "prior committed turns in this session plus ended prior sessions with the identical cast";
                AddHeardDialogue(session, observed, job, choices, ref clipped);
                break;

            default:
                universe = "none: no approved deterministic retrieval for Other";
                break;
        }

        var incomplete = choices.Count > maxItems || clipped;
        var picked = choices.Take(maxItems).ToArray();
        var authorized = new Dictionary<string, EvidenceItem>(StringComparer.Ordinal);
        foreach (var item in picked)
            if (!authorized.TryAdd(item.ReferenceId, item))
                throw new InvalidDataException("Duplicate host evidence reference.");

        var scope = new VerificationEvidenceScope(
            job.SessionId, job.OpportunityId, job.SpeakerId,
            job.CommittedBoundary,
            new ReadOnlyDictionary<string, EvidenceItem>(authorized));

        var completeness = category == ClaimCategory.Other
            ? new EvidenceCompleteness(
                EvidenceCompletenessStatus.Unknown, universe,
                "no retrieval selector is defined for this claim category",
                "No authorized deterministic search was executed")
            : new EvidenceCompleteness(
                incomplete ? EvidenceCompletenessStatus.Partial :
                    EvidenceCompletenessStatus.CompleteForDeclaredScope,
                universe,
                "typed host indexes; pinned exposure, participant membership, source commit and review time",
                incomplete ? "Some eligible records or excerpts were omitted by safety limits" : null);

        return new VerifierEvidenceSelection(scope, completeness);
    }

    private void AddSourceEvidence(Session session, Utterance observed,
        List<EvidenceItem> items, ref bool clipped)
    {
        var briefing = GetBriefing(session.Briefing)
            ?? throw new InvalidDataException("Missing session-pinned briefing.");
        var text = Clip(briefing.Body, ref clipped);
        items.Add(new EvidenceItem(
            "briefing:" + briefing.Id,
            EvidenceItemKind.Briefing, EvidenceAuthorityTier.SourceSnapshot,
            EvidenceVisibility.ExposedToSpeaker,
            briefing.Id.ToString(), "briefing-v1", HashVerifierText(briefing.Body),
            text, 0));

        var pin = GetSessionEvidence(session.Id);
        if (pin is null) return; // Unpinned stored passages are NEVER readable.
        if (pin.Briefing != briefing.Id)
            throw new InvalidDataException("Session source pin no longer matches briefing.");

        var snapshot = GetEvidenceSnapshot(pin.SnapshotId)
            ?? throw new InvalidDataException("Pinned snapshot is missing.");
        foreach (var passageId in pin.ExposedPassageIds)
        {
            var passage = snapshot.Passages.SingleOrDefault(p => p.PassageId == passageId)
                ?? throw new InvalidDataException("Pinned passage was not found.");
            items.Add(new EvidenceItem(
                "passage:" + passage.PassageId, EvidenceItemKind.Passage,
                EvidenceAuthorityTier.SourceSnapshot,
                EvidenceVisibility.ExposedToSpeaker,
                snapshot.SnapshotId, snapshot.TextSha256,
                snapshot.TextSha256,
                Clip(passage.Text, ref clipped), 0,
                SourceRecordedAt: snapshot.CapturedAt));
        }
    }

    private void AddAnchors(Session session, CharacterId subject,
        CharacterId speaker, List<EvidenceItem> items, ref bool clipped)
    {
        foreach (var anchor in GetPinnedVerifierAnchors(session.Id, subject))
        {
            // Private other-character identity must not leak to the speaker.
            if (subject != speaker &&
                anchor.Exposure != CharacterAnchorExposure.SharedWithRoom)
                continue;

            var raw = anchor.Key + ": " + anchor.Value;
            items.Add(new EvidenceItem(
                $"anchor:{subject}:{anchor.ProfileVersion}:{anchor.Key}",
                EvidenceItemKind.BioAnchor,
                EvidenceAuthorityTier.BindingAnchor,
                subject == speaker ? EvidenceVisibility.OwnHistory :
                    EvidenceVisibility.SharedDialogue,
                subject.ToString(), $"profile-v{anchor.ProfileVersion}",
                HashVerifierText(raw), Clip(raw, ref clipped), 0));
        }
    }

    private void AddOwnHistory(Session session, Utterance observed,
        VerificationJobIdentity job, List<EvidenceItem> items, ref bool clipped)
    {
        var all = ReadVerifierHistoryEvents(job.SpeakerId);
        var eligible = new List<(CharacterHistoryEvent Event, Utterance Source)>();
        foreach (var e in all)
        {
            // A post-hoc index or later review never changes an earlier observation.
            if (e.RecordedAt > observed.CommittedAt) continue;
            var sourceSession = GetSession(e.SourceSession);
            if (sourceSession is null) throw new InvalidDataException("Missing history source session.");
            bool inCurrent = e.SourceSession == session.Id;
            if (!inCurrent &&
                (sourceSession.Status != SessionStatus.Ended ||
                 sourceSession.EndedAt is null ||
                 sourceSession.EndedAt > session.CreatedAt))
                continue;

            var source = GetCommittedUtterances(e.SourceSession)
                .SingleOrDefault(u => u.Id == e.SourceUtterance);
            if (source is null || source.Speaker != job.SpeakerId ||
                source.Sequence != e.SourceSequence ||
                source.CommittedAt >= observed.CommittedAt ||
                (inCurrent && source.Sequence > job.CommittedBoundary))
                continue;
            eligible.Add((e, source));
        }

        var children = eligible
            .Where(x => x.Event.PreviousEventId.HasValue)
            .ToDictionary(x => x.Event.PreviousEventId!.Value, x => x.Event.EventId);

        foreach (var (e, source) in eligible.OrderBy(x => x.Event.RecordedAt)
                     .ThenBy(x => x.Event.EventId))
        {
            var superseded = children.TryGetValue(e.EventId, out var child);
            var authority = superseded
                ? EvidenceAuthorityTier.SupersededRecollection
                : e.Kind == CharacterHistoryEventKind.ReviewedSelfReaffirmation
                    ? EvidenceAuthorityTier.EstablishedRecollection
                    : EvidenceAuthorityTier.ProvisionalRecollection;

            items.Add(new EvidenceItem(
                $"history:{e.EventId:D}", EvidenceItemKind.History, authority,
                EvidenceVisibility.OwnHistory,
                e.SourceUtterance.ToString(), $"history-event:{e.EventId:D}",
                HashVerifierText(source.Text), Clip(e.Statement, ref clipped),
                e.SourceSession == session.Id ? e.SourceSequence : 0,
                superseded, superseded ? $"history:{child:D}" : null,
                e.RecordedAt));
        }
    }

    private void AddHeardDialogue(Session session, Utterance observed,
        VerificationJobIdentity job, List<EvidenceItem> items, ref bool clipped)
    {
        // Only sessions with precisely the same cast can be assumed shared.
        var prior = FindPriorSessions(session.Participants, session.Id);
        foreach (var pastId in prior)
        {
            foreach (var u in GetCommittedUtterances(pastId))
            {
                if (u.CommittedAt >= observed.CommittedAt) continue;
                items.Add(ToDialogueItem(u, availableAt: 0, ref clipped));
            }
        }

        foreach (var u in GetCommittedUtterances(session.Id))
        {
            if (u.Sequence > job.CommittedBoundary ||
                u.Id == observed.Id ||
                u.CommittedAt >= observed.CommittedAt) continue;
            items.Add(ToDialogueItem(u, availableAt: u.Sequence, ref clipped));
        }
    }

    private static EvidenceItem ToDialogueItem(
        Utterance u, long availableAt, ref bool clipped) => new(
            $"utterance:{u.Session}:{u.Id}", EvidenceItemKind.Utterance,
            EvidenceAuthorityTier.PreviouslyHeardUtterance,
            EvidenceVisibility.SharedDialogue,
            u.Id.ToString(), $"session:{u.Session}:sequence:{u.Sequence}",
            HashVerifierText(u.Text), Clip(u.Text, ref clipped),
            availableAt, SourceRecordedAt: u.CommittedAt);

    private static string Clip(string text, ref bool clipped)
    {
        const int max = 8_192; // Never exceed the separate V1 packet contract.
        if (text.Length <= max) return text;
        clipped = true;
        return text[..max];
    }

    private static string HashVerifierText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
}

using System.Globalization;
using BusStop.Domain;
using Microsoft.Data.Sqlite;

namespace BusStop.Persistence;

/// <summary>
/// Explicit host-owned V2 sidecar. Nothing in this file is called from the
/// conversation engine, participant context builder or existing recollection service.
/// </summary>
public sealed partial class ConversationStore
{
    /// <summary>
    /// Seal an owner-reviewed, possibly empty anchor set for ONE profile version.
    /// Existing versions are never inferred from Persona, and a set cannot be
    /// added or changed after that version has been pinned to a verifier session.
    /// </summary>
    public void SealVerifierAnchorSet(CharacterId character, int profileVersion,
        IReadOnlyList<CharacterAnchor> anchors, string ownerReviewReference)
    {
        if (profileVersion < 1 || GetCharacter(character, profileVersion) is null ||
            anchors is null || !VerifierText(ownerReviewReference, 256) ||
            anchors.Count > 64)
            throw new InvalidDataException("Invalid or unapproved character anchor set.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in anchors)
        {
            if (anchor is null || anchor.Character != character ||
                anchor.ProfileVersion != profileVersion ||
                !VerifierText(anchor.Key, 128) ||
                !VerifierText(anchor.Value, 2_048) ||
                !Enum.IsDefined(typeof(CharacterAnchorExposure), anchor.Exposure) ||
                !keys.Add(anchor.Key))
                throw new InvalidDataException("Invalid, foreign or repeated character anchor.");
        }

        using var tx = _connection.BeginTransaction(deferred: false);
        // Replaying the exact same seal is allowed, but it is never an edit.
        using (var existing = _connection.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = """
                SELECT owner_review_ref FROM character_anchor_sets
                WHERE character_id = @c AND profile_version = @v;
                """;
            existing.Parameters.AddWithValue("@c", character.ToString());
            existing.Parameters.AddWithValue("@v", profileVersion);
            var previousReview = existing.ExecuteScalar() as string;
            if (previousReview is not null)
            {
                var sealedAnchors = ReadVerifierAnchors(character, profileVersion, tx);
                if (previousReview != ownerReviewReference ||
                    sealedAnchors.Count != anchors.Count ||
                    !sealedAnchors.OrderBy(x => x.Key, StringComparer.Ordinal)
                        .SequenceEqual(anchors.OrderBy(x => x.Key, StringComparer.Ordinal)))
                    throw new InvalidOperationException(
                        "This character version has a different sealed anchor set.");
                tx.Commit();
                return;
            }
        }

        using (var pinned = _connection.CreateCommand())
        {
            pinned.Transaction = tx;
            pinned.CommandText = """
                SELECT COUNT(*) FROM session_verifier_profile_pins
                WHERE character_id = @c AND profile_version = @v;
                """;
            pinned.Parameters.AddWithValue("@c", character.ToString());
            pinned.Parameters.AddWithValue("@v", profileVersion);
            if ((long)pinned.ExecuteScalar()! != 0)
                throw new InvalidOperationException(
                    "Cannot introduce binding anchors after a profile version was pinned.");
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO character_anchor_sets
                    (character_id, profile_version, owner_review_ref, sealed_at)
                VALUES (@c, @v, @review, @at);
                """;
            cmd.Parameters.AddWithValue("@c", character.ToString());
            cmd.Parameters.AddWithValue("@v", profileVersion);
            cmd.Parameters.AddWithValue("@review", ownerReviewReference);
            cmd.Parameters.AddWithValue("@at", Iso(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
        }

        foreach (var anchor in anchors)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO character_anchors
                    (character_id, profile_version, anchor_key, anchor_value, exposure)
                VALUES (@c, @v, @key, @value, @exposure);
                """;
            cmd.Parameters.AddWithValue("@c", character.ToString());
            cmd.Parameters.AddWithValue("@v", profileVersion);
            cmd.Parameters.AddWithValue("@key", anchor.Key);
            cmd.Parameters.AddWithValue("@value", anchor.Value);
            cmd.Parameters.AddWithValue("@exposure", anchor.Exposure.ToString());
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// A new session must explicitly pin a known profile version before its first
    /// speaking opportunity. Existing unpinned sessions have ZERO verifier anchors.
    /// No change is made to the actual participant profile used by the engine.
    /// </summary>
    public void PinVerifierCharacterVersion(SessionId session, CharacterId character,
        int profileVersion)
    {
        var current = GetSession(session);
        if (current is null || current.Status != SessionStatus.Active ||
            !current.Participants.Contains(character) ||
            profileVersion < 1 || GetCharacter(character, profileVersion) is null)
            throw new InvalidOperationException("Invalid verifier profile pin.");

        using var tx = _connection.BeginTransaction(deferred: false);
        using (var check = _connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = """
                SELECT COUNT(*) FROM speaking_opportunities
                WHERE session_id = @s;
                """;
            check.Parameters.AddWithValue("@s", session.ToString());
            if ((long)check.ExecuteScalar()! != 0)
                throw new InvalidOperationException(
                    "Verifier profile versions must be pinned before the first opportunity.");
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO session_verifier_profile_pins
                    (session_id, character_id, profile_version, pinned_at)
                VALUES (@s, @c, @v, @at)
                ON CONFLICT(session_id, character_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("@s", session.ToString());
            insert.Parameters.AddWithValue("@c", character.ToString());
            insert.Parameters.AddWithValue("@v", profileVersion);
            insert.Parameters.AddWithValue("@at", Iso(DateTimeOffset.UtcNow));
            insert.ExecuteNonQuery();
        }

        using (var check = _connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = """
                SELECT profile_version FROM session_verifier_profile_pins
                WHERE session_id = @s AND character_id = @c;
                """;
            check.Parameters.AddWithValue("@s", session.ToString());
            check.Parameters.AddWithValue("@c", character.ToString());
            if ((long)check.ExecuteScalar()! != profileVersion)
                throw new InvalidOperationException(
                    "Verifier profile pin is immutable; another version is already pinned.");
        }
        tx.Commit();
    }

    internal IReadOnlyList<CharacterAnchor> GetPinnedVerifierAnchors(
        SessionId session, CharacterId character)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.profile_version, a.anchor_key, a.anchor_value, a.exposure
            FROM session_verifier_profile_pins p
            JOIN character_anchors a
              ON a.character_id = p.character_id
             AND a.profile_version = p.profile_version
            WHERE p.session_id = @s AND p.character_id = @c
            ORDER BY a.anchor_key;
            """;
        cmd.Parameters.AddWithValue("@s", session.ToString());
        cmd.Parameters.AddWithValue("@c", character.ToString());
        var result = new List<CharacterAnchor>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(new CharacterAnchor(character, reader.GetInt32(0),
                reader.GetString(1), reader.GetString(2),
                Enum.Parse<CharacterAnchorExposure>(reader.GetString(3))));
        return result.AsReadOnly();
    }

    private IReadOnlyList<CharacterAnchor> ReadVerifierAnchors(
        CharacterId character, int version, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT anchor_key, anchor_value, exposure FROM character_anchors
            WHERE character_id = @c AND profile_version = @v
            ORDER BY anchor_key;
            """;
        cmd.Parameters.AddWithValue("@c", character.ToString());
        cmd.Parameters.AddWithValue("@v", version);
        var list = new List<CharacterAnchor>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new CharacterAnchor(character, version, reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<CharacterAnchorExposure>(reader.GetString(2))));
        return list.AsReadOnly();
    }

    /// <summary>
    /// Register a claim from the SUBJECT'S OWN committed speech. The host stamps
    /// recording time, so later reviews cannot be made available retrospectively.
    /// A human-reviewed self-reaffirmation/correction must link the actual earlier
    /// event. No model-supplied flag can silently promote a provisional memory.
    /// </summary>
    public CharacterHistoryEvent RecordVerifierHistoryEvent(CharacterHistoryEvent proposed)
    {
        if (proposed is null || proposed.EventId == Guid.Empty ||
            !VerifierText(proposed.Key, 128) ||
            !VerifierText(proposed.Statement, 2_048) ||
            !Enum.IsDefined(typeof(CharacterHistoryEventKind), proposed.Kind) ||
            proposed.SourceSequence < 1)
            throw new InvalidDataException("Invalid proposed character history event.");

        var source = GetCommittedUtterances(proposed.SourceSession)
            .SingleOrDefault(x => x.Id == proposed.SourceUtterance);
        if (source is null || source.Speaker != proposed.Subject ||
            source.Sequence != proposed.SourceSequence ||
            !source.Text.Contains(proposed.Statement, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Character history must quote the subject's own committed utterance.");

        var isProvisional = proposed.Kind == CharacterHistoryEventKind.Provisional;
        if (isProvisional != (proposed.PreviousEventId is null) ||
            (isProvisional && proposed.OwnerReviewReference is not null) ||
            (!isProvisional && !VerifierText(proposed.OwnerReviewReference, 256)))
            throw new InvalidDataException(
                "Reaffirmation/correction requires a predecessor and owner review.");

        using var tx = _connection.BeginTransaction(deferred: false);
        if (!isProvisional)
        {
            var previous = ReadVerifierHistoryEvent(proposed.PreviousEventId!.Value, tx)
                ?? throw new InvalidDataException("Unknown history predecessor.");
            var earlier = GetCommittedUtterances(previous.SourceSession)
                .Single(x => x.Id == previous.SourceUtterance);
            if (previous.Subject != proposed.Subject ||
                previous.Key != proposed.Key ||
                previous.SourceUtterance == proposed.SourceUtterance ||
                source.CommittedAt <= earlier.CommittedAt)
                throw new InvalidDataException(
                    "Cannot reaffirm/correct another subject, key or earlier statement.");

            // The UNIQUE(previous_event_id) database constraint also forbids forks.
        }
        else
        {
            // A new provisional event must not reuse an existing history key while
            // a chain already exists. A change must explicitly correct that chain.
            using var check = _connection.CreateCommand();
            check.Transaction = tx;
            check.CommandText = """
                SELECT COUNT(*) FROM character_history_events
                WHERE subject_id = @subject AND history_key = @key;
                """;
            check.Parameters.AddWithValue("@subject", proposed.Subject.ToString());
            check.Parameters.AddWithValue("@key", proposed.Key);
            if ((long)check.ExecuteScalar()! > 0)
                throw new InvalidOperationException(
                    "An existing recollection must be corrected or reaffirmed, not overwritten.");
        }

        var saved = proposed with { RecordedAt = DateTimeOffset.UtcNow };
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO character_history_events
                    (event_id, subject_id, history_key, statement, source_session_id,
                     source_utterance_id, source_sequence, event_kind,
                     previous_event_id, owner_review_ref, recorded_at)
                VALUES (@id, @subject, @key, @statement, @session,
                        @utterance, @seq, @kind, @previous, @review, @at);
                """;
            cmd.Parameters.AddWithValue("@id", saved.EventId.ToString("D"));
            cmd.Parameters.AddWithValue("@subject", saved.Subject.ToString());
            cmd.Parameters.AddWithValue("@key", saved.Key);
            cmd.Parameters.AddWithValue("@statement", saved.Statement);
            cmd.Parameters.AddWithValue("@session", saved.SourceSession.ToString());
            cmd.Parameters.AddWithValue("@utterance", saved.SourceUtterance.ToString());
            cmd.Parameters.AddWithValue("@seq", saved.SourceSequence);
            cmd.Parameters.AddWithValue("@kind", saved.Kind.ToString());
            cmd.Parameters.AddWithValue("@previous",
                (object?)saved.PreviousEventId?.ToString("D") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@review",
                (object?)saved.OwnerReviewReference ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@at", Iso(saved.RecordedAt));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return saved;
    }

    internal IReadOnlyList<CharacterHistoryEvent> ReadVerifierHistoryEvents(CharacterId subject)
    {
        var list = new List<CharacterHistoryEvent>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, subject_id, history_key, statement, source_session_id,
                   source_utterance_id, source_sequence, event_kind,
                   previous_event_id, owner_review_ref, recorded_at
            FROM character_history_events
            WHERE subject_id = @subject ORDER BY recorded_at, event_id;
            """;
        cmd.Parameters.AddWithValue("@subject", subject.ToString());
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(MapHistory(reader));
        return list.AsReadOnly();
    }

    private CharacterHistoryEvent? ReadVerifierHistoryEvent(Guid id, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT event_id, subject_id, history_key, statement, source_session_id,
                   source_utterance_id, source_sequence, event_kind,
                   previous_event_id, owner_review_ref, recorded_at
            FROM character_history_events WHERE event_id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapHistory(reader) : null;
    }

    private static CharacterHistoryEvent MapHistory(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        CharacterId.Parse(reader.GetString(1)),
        reader.GetString(2), reader.GetString(3),
        SessionId.Parse(reader.GetString(4)),
        UtteranceId.Parse(reader.GetString(5)),
        reader.GetInt64(6),
        Enum.Parse<CharacterHistoryEventKind>(reader.GetString(7)),
        reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture));

    private static bool VerifierText(string? text, int max) =>
        text is { Length: > 0 } && text.Length <= max && !string.IsNullOrWhiteSpace(text);
}

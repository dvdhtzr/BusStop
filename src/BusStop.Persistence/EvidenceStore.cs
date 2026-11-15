using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Domain;

namespace BusStop.Persistence;

/// <summary>The source pin is public session evidence, never a person's memory.
/// It lists exactly what the participants were shown in the pinned briefing.</summary>
public sealed record SessionEvidencePin(
    SessionId Session,
    string SnapshotId,
    BriefingId Briefing,
    IReadOnlyList<string> ExposedPassageIds);

public sealed partial class ConversationStore
{
    /// <summary>Save a frozen snapshot and its exact passages. A repeated identical
    /// capture is allowed; changing existing content or chunk boundaries fails closed.</summary>
    public void SaveEvidenceSnapshot(EvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SnapshotId.Length != 64 ||
            !snapshot.SnapshotId.All(Uri.IsHexDigit) ||
            snapshot.TextSha256.Length != 64 ||
            !snapshot.TextSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Evidence identity must be a SHA-256 hex string.");

        var hash = Convert.ToHexString(SHA256.HashData(
            new UTF8Encoding(false, true).GetBytes(snapshot.SnapshotText))).ToLowerInvariant();
        if (!string.Equals(hash, snapshot.TextSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Evidence text hash does not match the captured source.");
        if (snapshot.Passages.Count == 0)
            throw new InvalidDataException("Evidence must contain at least one passage.");

        for (var i = 0; i < snapshot.Passages.Count; i++)
        {
            var passage = snapshot.Passages[i];
            if (passage.Ordinal != i + 1 ||
                passage.PassageId != $"{snapshot.SnapshotId}:p{i + 1:D5}" ||
                passage.StartOffset < 0 || passage.Length < 1 ||
                passage.EndOffset > snapshot.SnapshotText.Length ||
                !string.Equals(passage.Text,
                    snapshot.SnapshotText.Substring(passage.StartOffset, passage.Length),
                    StringComparison.Ordinal))
                throw new InvalidDataException("Evidence passage does not match its source offsets.");
        }

        // Never modify an existing snapshot's passages, even when the same
        // source metadata/text is imported with a different chunking algorithm.
        var existing = GetEvidenceSnapshot(snapshot.SnapshotId);
        if (existing is not null)
        {
            RequireIdentical(existing, snapshot);
            return;
        }

        using (var tx = _connection.BeginTransaction(deferred: false))
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO evidence_snapshots
                        (id, kind, title, source_name, source_url, published_at,
                         rights_basis, text_sha256, captured_at, snapshot_text)
                    VALUES (@id, @kind, @title, @name, @url, @published,
                            @rights, @sha, @captured, @text)
                    ;
                    """;
                cmd.Parameters.AddWithValue("@id", snapshot.SnapshotId);
                cmd.Parameters.AddWithValue("@kind", snapshot.Descriptor.Kind.ToString());
                cmd.Parameters.AddWithValue("@title", snapshot.Descriptor.Title);
                cmd.Parameters.AddWithValue("@name", snapshot.Descriptor.SourceName);
                cmd.Parameters.AddWithValue("@url",
                    (object?)snapshot.Descriptor.SourceUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@published",
                    (object?)snapshot.Descriptor.PublishedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rights", snapshot.Descriptor.RightsBasis);
                cmd.Parameters.AddWithValue("@sha", snapshot.TextSha256);
                cmd.Parameters.AddWithValue("@captured", snapshot.CapturedAt.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("@text", snapshot.SnapshotText);
                cmd.ExecuteNonQuery();
            }

            foreach (var passage in snapshot.Passages)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO evidence_passages
                        (snapshot_id, ordinal, passage_id, start_offset, length, text)
                    VALUES (@snapshot, @ordinal, @id, @offset, @length, @text)
                    ;
                    """;
                cmd.Parameters.AddWithValue("@snapshot", snapshot.SnapshotId);
                cmd.Parameters.AddWithValue("@ordinal", passage.Ordinal);
                cmd.Parameters.AddWithValue("@id", passage.PassageId);
                cmd.Parameters.AddWithValue("@offset", passage.StartOffset);
                cmd.Parameters.AddWithValue("@length", passage.Length);
                cmd.Parameters.AddWithValue("@text", passage.Text);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        var stored = GetEvidenceSnapshot(snapshot.SnapshotId)
            ?? throw new InvalidDataException("Evidence snapshot could not be read back.");
        RequireIdentical(stored, snapshot);
    }

    private static void RequireIdentical(EvidenceSnapshot stored, EvidenceSnapshot incoming)
    {
        if (stored.Descriptor != incoming.Descriptor ||
            stored.TextSha256 != incoming.TextSha256 ||
            stored.SnapshotText != incoming.SnapshotText ||
            !stored.Passages.SequenceEqual(incoming.Passages))
            throw new InvalidOperationException(
                "Evidence snapshot identity was reused with different content or passages.");
    }

    public EvidenceSnapshot? GetEvidenceSnapshot(string snapshotId)
    {
        EvidenceSourceDescriptor descriptor;
        string digest, text;
        DateTimeOffset captured;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT kind, title, source_name, source_url, published_at,
                       rights_basis, text_sha256, captured_at, snapshot_text
                FROM evidence_snapshots WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", snapshotId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            descriptor = new EvidenceSourceDescriptor(
                Enum.Parse<EvidenceSourceKind>(reader.GetString(0)),
                reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(
                    reader.GetString(4), CultureInfo.InvariantCulture),
                reader.GetString(5));
            digest = reader.GetString(6);
            captured = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture);
            text = reader.GetString(8);
        }

        var passages = new List<EvidencePassage>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT passage_id, ordinal, start_offset, length, text
                FROM evidence_passages WHERE snapshot_id = @id ORDER BY ordinal;
                """;
            cmd.Parameters.AddWithValue("@id", snapshotId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                passages.Add(new EvidencePassage(
                    reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.GetString(4)));
        }

        return new EvidenceSnapshot(
            snapshotId, descriptor, digest, captured, text, passages.AsReadOnly());
    }

    /// <summary>Pin a frozen source to the session before the first speaking
    /// opportunity. A session may not silently switch sources during playback.</summary>
    public void PinSessionEvidence(
        SessionId sessionId, string snapshotId, IReadOnlyList<string> exposedPassageIds)
        => PinSessionEvidenceCore(sessionId, snapshotId, exposedPassageIds, dynamicRetrieval: false);

    /// <summary>R1c pins a source but exposes no static passages. Evidence is
    /// selected and recorded per speaking opportunity before model inference.</summary>
    public void PinSessionEvidenceForRetrieval(SessionId sessionId, string snapshotId)
        => PinSessionEvidenceCore(sessionId, snapshotId, Array.Empty<string>(),
            dynamicRetrieval: true);

    private void PinSessionEvidenceCore(
        SessionId sessionId, string snapshotId, IReadOnlyList<string> exposedPassageIds,
        bool dynamicRetrieval)
    {
        ArgumentNullException.ThrowIfNull(exposedPassageIds);
        var session = GetSession(sessionId)
            ?? throw new InvalidOperationException("Cannot pin evidence to an unknown session.");
        var snapshot = GetEvidenceSnapshot(snapshotId)
            ?? throw new InvalidOperationException("Cannot pin an unknown evidence snapshot.");
        var briefing = GetBriefing(session.Briefing)
            ?? throw new InvalidOperationException("Cannot pin evidence without a frozen briefing.");

        if (session.Status != SessionStatus.Active ||
            GetRecordedOpportunities(sessionId).Count != 0)
            throw new InvalidOperationException("Evidence must be pinned before the first turn.");
        if (briefing.Url != $"local-evidence:{snapshotId}")
            throw new InvalidOperationException("Session briefing does not identify the pinned snapshot.");
        if (dynamicRetrieval !=
            (session.ContextVersion == EvidenceContextVersions.DynamicLexicalRetrieval))
            throw new InvalidOperationException("Source retrieval mode and persisted context version disagree.");
        if ((!dynamicRetrieval && exposedPassageIds.Count == 0) ||
            (dynamicRetrieval && exposedPassageIds.Count != 0) ||
            exposedPassageIds.Distinct(StringComparer.Ordinal).Count() != exposedPassageIds.Count ||
            exposedPassageIds.Any(id => !snapshot.Passages.Any(p => p.PassageId == id)))
            throw new InvalidOperationException("Only unique, authorized passages may be statically exposed.");

        var ids = exposedPassageIds.ToArray();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO session_evidence (session_id, snapshot_id, briefing_id, passage_ids)
                VALUES (@session, @snapshot, @briefing, @passages)
                ON CONFLICT(session_id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("@session", sessionId.ToString());
            cmd.Parameters.AddWithValue("@snapshot", snapshotId);
            cmd.Parameters.AddWithValue("@briefing", briefing.Id.ToString());
            cmd.Parameters.AddWithValue("@passages", JsonSerializer.Serialize(ids));
            cmd.ExecuteNonQuery();
        }

        var persisted = GetSessionEvidence(sessionId)!;
        if (persisted.SnapshotId != snapshotId ||
            persisted.Briefing != briefing.Id ||
            !persisted.ExposedPassageIds.SequenceEqual(ids, StringComparer.Ordinal))
            throw new InvalidOperationException("Session evidence is immutable; repinning refused.");
    }

    public SessionEvidencePin? GetSessionEvidence(SessionId sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT snapshot_id, briefing_id, passage_ids
            FROM session_evidence WHERE session_id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", sessionId.ToString());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new SessionEvidencePin(
            sessionId,
            reader.GetString(0),
            BriefingId.Parse(reader.GetString(1)),
            Array.AsReadOnly(
                JsonSerializer.Deserialize<string[]>(reader.GetString(2))
                ?? throw new InvalidDataException("Missing pinned passage ids.")));
    }
}

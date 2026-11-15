using System.Text.Json;
using BusStop.Domain;

namespace BusStop.Persistence;

/// <summary>Source passages made visible to a particular speaker on one offered
/// turn. This is not an assertion that their spoken claims were grounded.</summary>
public sealed record OpportunitySourceRetrieval(
    OpportunityId Opportunity,
    SessionId Session,
    CharacterId Speaker,
    string SnapshotId,
    string Algorithm,
    string Query,
    IReadOnlyList<string> PassageIds,
    IReadOnlyList<double> Scores);

public sealed partial class ConversationStore
{
    public void SaveOpportunityRetrieval(
        SpeakingOpportunity opportunity,
        string snapshotId,
        string algorithm,
        string query,
        IReadOnlyList<EvidencePassage> passages,
        IReadOnlyList<double> scores)
    {
        ArgumentNullException.ThrowIfNull(passages);
        ArgumentNullException.ThrowIfNull(scores);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1024 ||
            string.IsNullOrWhiteSpace(algorithm) || algorithm.Length > 128 ||
            passages.Count is < 1 or > 3 || passages.Count != scores.Count ||
            scores.Any(x => !double.IsFinite(x) || x < 0))
            throw new InvalidOperationException("Invalid bounded source retrieval.");

        var pin = GetSessionEvidence(opportunity.Session)
            ?? throw new InvalidOperationException("Source must be pinned before retrieval.");
        if (pin.SnapshotId != snapshotId || pin.ExposedPassageIds.Count != 0)
            throw new InvalidOperationException("Turn retrieval requires the matching dynamic source pin.");

        var session = GetSession(opportunity.Session)
            ?? throw new InvalidOperationException("Missing session.");
        if (session.ContextVersion != EvidenceContextVersions.DynamicLexicalRetrieval ||
            session.Status != SessionStatus.Active)
            throw new InvalidOperationException("Session is not active dynamic-retrieval mode.");

        var recorded = GetRecordedOpportunities(opportunity.Session)
            .FirstOrDefault(o => o.Id == opportunity.Id);
        if (recorded is null || recorded.Participant != opportunity.IntendedParticipant)
            throw new InvalidOperationException("Retrieval must match an existing speaking opportunity.");
        if (GetAttempts(opportunity.Session).Any(a => a.Opportunity == opportunity.Id))
            throw new InvalidOperationException("Cannot create retrieval after model inference.");

        var snapshot = GetEvidenceSnapshot(snapshotId)
            ?? throw new InvalidOperationException("Pinned source is absent.");
        var ids = passages.Select(p => p.PassageId).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            passages.Any(p => !snapshot.Passages.Any(known =>
                known.PassageId == p.PassageId && known == p)))
            throw new InvalidOperationException("Retrieved passages must exactly match pinned source evidence.");

        var existing = GetOpportunityRetrievals(opportunity.Session)
            .FirstOrDefault(x => x.Opportunity == opportunity.Id);
        if (existing is not null)
        {
            if (existing.SnapshotId != snapshotId ||
                existing.Speaker != opportunity.IntendedParticipant ||
                existing.Algorithm != algorithm || existing.Query != query ||
                !existing.PassageIds.SequenceEqual(ids, StringComparer.Ordinal) ||
                !existing.Scores.SequenceEqual(scores))
                throw new InvalidOperationException("Recorded turn retrieval is immutable.");
            return;
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO opportunity_source_retrievals
                (opportunity_id, session_id, speaker_id, snapshot_id,
                 algorithm, query_text, passage_ids, scores)
            VALUES (@opportunity, @session, @speaker, @snapshot,
                    @algorithm, @query, @passages, @scores);
            """;
        cmd.Parameters.AddWithValue("@opportunity", opportunity.Id.ToString());
        cmd.Parameters.AddWithValue("@session", opportunity.Session.ToString());
        cmd.Parameters.AddWithValue("@speaker", opportunity.IntendedParticipant.ToString());
        cmd.Parameters.AddWithValue("@snapshot", snapshotId);
        cmd.Parameters.AddWithValue("@algorithm", algorithm);
        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@passages", JsonSerializer.Serialize(ids));
        cmd.Parameters.AddWithValue("@scores", JsonSerializer.Serialize(scores.ToArray()));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<OpportunitySourceRetrieval> GetOpportunityRetrievals(SessionId session)
    {
        var result = new List<OpportunitySourceRetrieval>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT r.opportunity_id, r.speaker_id, r.snapshot_id, r.algorithm,
                   r.query_text, r.passage_ids, r.scores
            FROM opportunity_source_retrievals r
            JOIN speaking_opportunities o ON o.id = r.opportunity_id
            WHERE r.session_id = @session AND o.session_id = @session
            ORDER BY o.ordinal ASC;
            """;
        cmd.Parameters.AddWithValue("@session", session.ToString());
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var ids = JsonSerializer.Deserialize<string[]>(reader.GetString(5))
                ?? throw new InvalidDataException("Missing retrieved passage ids.");
            var scores = JsonSerializer.Deserialize<double[]>(reader.GetString(6))
                ?? throw new InvalidDataException("Missing retrieval scores.");
            result.Add(new OpportunitySourceRetrieval(
                OpportunityId.Parse(reader.GetString(0)), session,
                CharacterId.Parse(reader.GetString(1)), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                Array.AsReadOnly(ids), Array.AsReadOnly(scores)));
        }
        return result;
    }
}

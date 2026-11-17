using BusStop.Domain;
using BusStop.Moderation.Verification;

namespace BusStop.Persistence;

/// <summary>Read-only V7c job discovery. Each result must independently pass
/// the existing V5a checkpoint-chain validator before it is counted.</summary>
public sealed partial class ConversationStore
{
    public IReadOnlyList<Guid> ListV7cVerificationJobs(
        SessionId session, string evaluationKey)
    {
        if (string.IsNullOrWhiteSpace(evaluationKey) ||
            evaluationKey.Length > 128 ||
            !evaluationKey.StartsWith(V5aLedgerContract.EvaluationPrefix,
                StringComparison.Ordinal))
            throw new InvalidDataException("Invalid read-only V7c evaluation key.");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT job_id FROM verifier_v5a_jobs
            WHERE session_id = @session AND evaluation_key = @evaluation
            ORDER BY registered_at, job_id;
            """;
        cmd.Parameters.AddWithValue("@session", session.ToString());
        cmd.Parameters.AddWithValue("@evaluation", evaluationKey);
        var ids = new List<Guid>();
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            if (!Guid.TryParse(rows.GetString(0), out var id) ||
                id == Guid.Empty)
                throw new InvalidDataException("Invalid V7c persisted verification job.");
            ids.Add(id);
        }
        if (ids.Count != ids.Distinct().Count())
            throw new InvalidDataException("Repeated V7c job identity.");
        return Array.AsReadOnly(ids.ToArray());
    }
}

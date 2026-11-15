using System.Text.Json;
using BusStop.Domain;
using BusStop.Moderation;

namespace BusStop.Persistence;

public sealed partial class ConversationStore
{
    // Additive migration: pre-M1a private audits remain readable (null traces).
    private void EnsureShadowAttemptColumn()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(shadow_moderation_reviews);";
        var exists = false;
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (r.GetString(1) == "attempts_json")
                    exists = true;
        if (!exists)
            Execute("ALTER TABLE shadow_moderation_reviews ADD COLUMN attempts_json TEXT NULL;");
    }

    /// <summary>Append-only private audit. An observation must match a real already
    /// committed utterance, including its exact original candidate text and boundary.</summary>
    public void AppendShadowReview(ShadowReviewRecord review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var c = review.Candidate;
        if (c.CandidateId == Guid.Empty ||
            GetModerationPolicy(c.Session).Mode != ModerationOperatingMode.Shadow)
            throw new InvalidOperationException("Shadow audit requires a shadow-mode session.");

        using (var check = _connection.CreateCommand())
        {
            check.CommandText = """
                SELECT 1 FROM utterances u
                JOIN speaking_opportunities o ON o.id = u.opportunity_id
                WHERE u.session_id = @session AND u.id = @utterance
                  AND u.opportunity_id = @opportunity AND u.speaker = @speaker
                  AND u.text = @text AND o.committed_boundary = @boundary
                  AND u.sequence = @next
                LIMIT 1;
                """;
            check.Parameters.AddWithValue("@session", c.Session.ToString());
            check.Parameters.AddWithValue("@utterance", review.CommittedUtterance.ToString());
            check.Parameters.AddWithValue("@opportunity", c.Opportunity.ToString());
            check.Parameters.AddWithValue("@speaker", c.Speaker.ToString());
            check.Parameters.AddWithValue("@text", c.Text);
            check.Parameters.AddWithValue("@boundary", c.CommittedBoundary);
            check.Parameters.AddWithValue("@next", c.CommittedBoundary + 1);
            if (check.ExecuteScalar() is null)
                throw new InvalidOperationException("Stale or uncommitted shadow candidate.");
        }

        if (review.Judgment is { } judgment)
        {
            var scope = new ModerationEvidenceScope(
                c.Session, c.Opportunity, c.Speaker, c.CommittedBoundary,
                review.AuthorizedReferences.ToHashSet(StringComparer.Ordinal));
            ModerationContractValidator.Validate(c, scope, judgment);
        }

        if (review.RequestId is { } requestId &&
            !GetModeratorReservations(c.Session).Any(r =>
                r.RequestId == requestId && r.Opportunity == c.Opportunity &&
                r.Purpose is ModerationInferencePurpose.ShadowSampling or
                    ModerationInferencePurpose.MaterialViolationReview))
            throw new InvalidOperationException("Shadow audit has no matching reserved inference.");

        if ((review.Status == ShadowReviewStatus.Reviewed) != (review.Judgment is not null))
            throw new InvalidOperationException("Only reviewed results can contain a validated judgment.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO shadow_moderation_reviews
                (candidate_id, session_id, opportunity_id, utterance_id, speaker_id,
                 committed_boundary, candidate_text, trigger_kind, authorized_refs,
                 model_name, model_digest, request_id, review_status, judgment_json,
                 severity, raw_response, failure_detail, recorded_at,
                attempts_json)
            VALUES
                (@candidate, @session, @opportunity, @utterance, @speaker,
                 @boundary, @text, @trigger, @authorized,
                 @model, @digest, @request, @status, @judgment,
                 @severity, @raw, @failure, @at, @attempts);
            """;
        cmd.Parameters.AddWithValue("@candidate", c.CandidateId.ToString("D"));
        cmd.Parameters.AddWithValue("@session", c.Session.ToString());
        cmd.Parameters.AddWithValue("@opportunity", c.Opportunity.ToString());
        cmd.Parameters.AddWithValue("@utterance", review.CommittedUtterance.ToString());
        cmd.Parameters.AddWithValue("@speaker", c.Speaker.ToString());
        cmd.Parameters.AddWithValue("@boundary", c.CommittedBoundary);
        cmd.Parameters.AddWithValue("@text", c.Text);
        cmd.Parameters.AddWithValue("@trigger", review.Trigger);
        cmd.Parameters.AddWithValue("@authorized", JsonSerializer.Serialize(review.AuthorizedReferences));
        cmd.Parameters.AddWithValue("@model", review.ModelName);
        cmd.Parameters.AddWithValue("@digest", review.ModelDigest);
        cmd.Parameters.AddWithValue("@request", (object?)review.RequestId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", review.Status.ToString());
        cmd.Parameters.AddWithValue("@judgment", review.Judgment is null
            ? DBNull.Value : JsonSerializer.Serialize(review.Judgment));
        cmd.Parameters.AddWithValue("@severity", review.Severity.ToString());
        cmd.Parameters.AddWithValue("@raw", (object?)review.RawResponse ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@failure", (object?)review.FailureDetail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@at", review.RecordedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("@attempts", review.Attempts is null
            ? DBNull.Value : JsonSerializer.Serialize(review.Attempts));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ShadowReviewRecord> GetShadowReviews(SessionId session)
    {
        var reviews = new List<ShadowReviewRecord>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT candidate_id, opportunity_id, utterance_id, speaker_id,
                   committed_boundary, candidate_text, trigger_kind, authorized_refs,
                   model_name, model_digest, request_id, review_status, judgment_json,
                   severity, raw_response, failure_detail, recorded_at,
                   attempts_json
            FROM shadow_moderation_reviews
            WHERE session_id = @session ORDER BY recorded_at, candidate_id;
            """;
        cmd.Parameters.AddWithValue("@session", session.ToString());
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var candidate = new ModerationCandidate(
                Guid.Parse(reader.GetString(0)), session,
                OpportunityId.Parse(reader.GetString(1)),
                CharacterId.Parse(reader.GetString(3)),
                reader.GetInt64(4), reader.GetString(5));
            reviews.Add(new ShadowReviewRecord(
                candidate, UtteranceId.Parse(reader.GetString(2)),
                reader.GetString(6),
                JsonSerializer.Deserialize<string[]>(reader.GetString(7))
                    ?? throw new InvalidDataException("Missing private audit references."),
                reader.GetString(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
                Enum.Parse<ShadowReviewStatus>(reader.GetString(11)),
                reader.IsDBNull(12) ? null :
                    JsonSerializer.Deserialize<ModerationJudgment>(reader.GetString(12)),
                Enum.Parse<ModeratorSeverity>(reader.GetString(13)),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                DateTimeOffset.Parse(reader.GetString(16)),
                reader.IsDBNull(17) ? null :
                    JsonSerializer.Deserialize<ShadowInferenceTrace[]>(reader.GetString(17))));
        }
        return reviews.AsReadOnly();
    }
}

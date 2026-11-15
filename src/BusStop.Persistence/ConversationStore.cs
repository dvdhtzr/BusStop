using BusStop.Moderation;
using System.Text.Json;
using BusStop.Domain;
using Microsoft.Data.Sqlite;

namespace BusStop.Persistence;

/// <summary>Durable, replayable store backed by SQLite. The recorded events are authoritative; the
/// model's output is not. Committing an utterance assigns the next sequence number inside a single
/// transaction, and a unique constraint on (session, sequence) plus one on (opportunity) make
/// duplication impossible even under retry or restart.</summary>
public sealed partial class ConversationStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public ConversationStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        Execute("PRAGMA foreign_keys = ON;");
    }

    public static ConversationStore OpenFile(string path) => new($"Data Source={path}");

    public void InitializeSchema()
    {
        Execute(Schema.Ddl);
        EnsureModerationMarkerColumn(); // add-only migration for older SQLite databases
        EnsureShadowSamplingColumn();
    }

    // --- writes -------------------------------------------------------------

    public void SaveCharacter(CharacterProfile c)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO characters (id, version, name, persona, traits)
            VALUES (@id, @version, @name, @persona, @traits)
            ON CONFLICT(id, version) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("@id", c.Id.ToString());
        cmd.Parameters.AddWithValue("@version", c.Version);
        cmd.Parameters.AddWithValue("@name", c.Name);
        cmd.Parameters.AddWithValue("@persona", c.Persona);
        cmd.Parameters.AddWithValue("@traits", JsonSerializer.Serialize(c.Traits));
        cmd.ExecuteNonQuery();

        // Character versions are append-only. Idempotent identical saves are permitted,
        // but an edit to the same version must be explicitly rejected.
        var stored = GetCharacter(c.Id, c.Version)
            ?? throw new InvalidOperationException("Character was not persisted.");
        if (!string.Equals(stored.Name, c.Name, StringComparison.Ordinal) ||
            !string.Equals(stored.Persona, c.Persona, StringComparison.Ordinal) ||
            !stored.Traits.SequenceEqual(c.Traits, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Character {c.Id} v{c.Version} is immutable; save a new version.");
    }

    public void SaveBriefing(Briefing b)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO briefings (id, title, source, url, body)
            VALUES (@id, @title, @source, @url, @body)
            ON CONFLICT(id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("@id", b.Id.ToString());
        cmd.Parameters.AddWithValue("@title", b.Title);
        cmd.Parameters.AddWithValue("@source", b.Source);
        cmd.Parameters.AddWithValue("@url", b.Url);
        cmd.Parameters.AddWithValue("@body", b.Body);
        cmd.ExecuteNonQuery();

        // A briefing ID must always describe the facts actually seen by the session.
        var stored = GetBriefing(b.Id)
            ?? throw new InvalidOperationException("Briefing was not persisted.");
        if (!string.Equals(stored.Title, b.Title, StringComparison.Ordinal) ||
            !string.Equals(stored.Source, b.Source, StringComparison.Ordinal) ||
            !string.Equals(stored.Url, b.Url, StringComparison.Ordinal) ||
            !string.Equals(stored.Body, b.Body, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Briefing {b.Id} is immutable; changed content needs a new ID.");
    }

    public void CreateSession(Session s, ModerationSessionPolicy? moderationPolicy = null)
    {
        var effectivePolicy = moderationPolicy ?? ModerationSessionPolicy.Disabled;
        effectivePolicy.Validate();
        using var tx = _connection.BeginTransaction();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sessions (id, mode, briefing_id, protocol_version, model_config_version,
                    context_version, status, created_at, ended_at, moderation_policy_marker)
                VALUES (@id, @mode, @briefing, @protocol, @model, @context, @status, @created, @ended, @moderation);
                """;
            cmd.Parameters.AddWithValue("@id", s.Id.ToString());
            cmd.Parameters.AddWithValue("@mode", s.Mode.ToString());
            cmd.Parameters.AddWithValue("@briefing", s.Briefing.ToString());
            cmd.Parameters.AddWithValue("@protocol", s.ProtocolVersion);
            cmd.Parameters.AddWithValue("@model", s.ModelConfigVersion);
            cmd.Parameters.AddWithValue("@context", s.ContextVersion);
            cmd.Parameters.AddWithValue("@moderation", effectivePolicy.ContractVersion);
            cmd.Parameters.AddWithValue("@status", s.Status.ToString());
            cmd.Parameters.AddWithValue("@created", Iso(s.CreatedAt));
            cmd.Parameters.AddWithValue("@ended", (object?)(s.EndedAt is { } e ? Iso(e) : null) ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        for (var i = 0; i < s.Participants.Count; i++)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO session_participants (session_id, ordinal, character_id)
                VALUES (@s, @o, @c);
                """;
            cmd.Parameters.AddWithValue("@s", s.Id.ToString());
            cmd.Parameters.AddWithValue("@o", i);
            cmd.Parameters.AddWithValue("@c", s.Participants[i].ToString());
            cmd.ExecuteNonQuery();
        }

        InsertModerationPolicy(tx, s.Id, effectivePolicy);
        tx.Commit();
    }

    public void UpdateSessionStatus(SessionId id, SessionStatus status, DateTimeOffset? endedAt = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET status = @status, ended_at = @ended WHERE id = @id;";
        cmd.Parameters.AddWithValue("@status", status.ToString());
        cmd.Parameters.AddWithValue("@ended", (object?)(endedAt is { } e ? Iso(e) : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public void SaveOpportunity(SpeakingOpportunity o, int ordinal)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO speaking_opportunities
                (id, session_id, ordinal, intended_participant, reason, committed_boundary, status)
            VALUES (@id, @s, @ordinal, @p, @reason, @boundary, @status);
            """;
        cmd.Parameters.AddWithValue("@id", o.Id.ToString());
        cmd.Parameters.AddWithValue("@s", o.Session.ToString());
        cmd.Parameters.AddWithValue("@ordinal", ordinal);
        cmd.Parameters.AddWithValue("@p", o.IntendedParticipant.ToString());
        cmd.Parameters.AddWithValue("@reason", o.Reason.ToString());
        cmd.Parameters.AddWithValue("@boundary", o.CommittedBoundary);
        cmd.Parameters.AddWithValue("@status", o.Status.ToString());
        cmd.ExecuteNonQuery();
    }

    public void UpdateOpportunityStatus(OpportunityId id, OpportunityStatus status)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE speaking_opportunities SET status = @status WHERE id = @id;";
        cmd.Parameters.AddWithValue("@status", status.ToString());
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reconstructs the ordered opportunities of a session with the outcome each one reached,
    /// derived from durable records: a committed utterance means Spoke; a Pass decision means Passed;
    /// anything else — including a would-be speak whose commit never landed — is Failed and was never
    /// delivered. This is what makes resume exactly-once.</summary>
    public IReadOnlyList<RecordedOpportunity> GetRecordedOpportunities(SessionId session)
    {
        var result = new List<RecordedOpportunity>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.ordinal, o.intended_participant, o.reason,
                   u.id, u.addressee,
                   d.action
            FROM speaking_opportunities o
            LEFT JOIN utterances u ON u.opportunity_id = o.id
            LEFT JOIN participant_decisions d ON d.opportunity_id = o.id
            WHERE o.session_id = @s
            ORDER BY o.ordinal ASC;
            """;
        cmd.Parameters.AddWithValue("@s", session.ToString());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var oppId = OpportunityId.Parse(r.GetString(0));
            var ordinal = r.GetInt32(1);
            var participant = CharacterId.Parse(r.GetString(2));
            var reason = Enum.Parse<OpportunityReason>(r.GetString(3));

            ReplayOutcome outcome;
            UtteranceId? utterance = null;
            CharacterId? addressee = null;

            if (!r.IsDBNull(4))
            {
                outcome = ReplayOutcome.Spoke;
                utterance = UtteranceId.Parse(r.GetString(4));
                addressee = r.IsDBNull(5) ? null : CharacterId.Parse(r.GetString(5));
            }
            else if (!r.IsDBNull(6) && Enum.Parse<DecisionAction>(r.GetString(6)) == DecisionAction.Pass)
            {
                outcome = ReplayOutcome.Passed;
            }
            else
            {
                outcome = ReplayOutcome.Failed;
            }

            result.Add(new RecordedOpportunity(oppId, ordinal, participant, reason, outcome, utterance, addressee));
        }

        return result;
    }

    public void SaveAttempt(ModelAttempt a)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO model_attempts
                (id, opportunity_id, participant, model_name, model_digest, protocol_version, outcome, retry_of, at)
            VALUES (@id, @opp, @p, @name, @digest, @protocol, @outcome, @retry, @at);
            """;
        cmd.Parameters.AddWithValue("@id", a.Id.ToString());
        cmd.Parameters.AddWithValue("@opp", a.Opportunity.ToString());
        cmd.Parameters.AddWithValue("@p", a.Participant.ToString());
        cmd.Parameters.AddWithValue("@name", a.ModelName);
        cmd.Parameters.AddWithValue("@digest", a.ModelDigest);
        cmd.Parameters.AddWithValue("@protocol", a.ProtocolVersion);
        cmd.Parameters.AddWithValue("@outcome", a.Outcome.ToString());
        cmd.Parameters.AddWithValue("@retry", (object?)(a.RetryOf?.ToString()) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@at", Iso(a.At));
        cmd.ExecuteNonQuery();
    }

    public void SaveDecision(ParticipantDecision d)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO participant_decisions
                (opportunity_id, participant, action, addressee, replies_to, attempt_id)
            VALUES (@opp, @p, @action, @addressee, @replies, @attempt);
            """;
        cmd.Parameters.AddWithValue("@opp", d.Opportunity.ToString());
        cmd.Parameters.AddWithValue("@p", d.Participant.ToString());
        cmd.Parameters.AddWithValue("@action", d.Action.ToString());
        cmd.Parameters.AddWithValue("@addressee", (object?)(d.Addressee?.ToString()) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@replies", (object?)(d.RepliesTo?.ToString()) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@attempt", d.Attempt.ToString());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Commits an utterance for an opportunity, assigning the next session sequence atomically.
    /// Throws if the opportunity already has a committed utterance (exactly-once) or if the assigned
    /// sequence would collide. This is the single commit point of the system.</summary>
    public Utterance CommitUtterance(
        OpportunityId opportunity,
        SessionId session,
        CharacterId speaker,
        string text,
        CharacterId? addressee,
        UtteranceId? repliesTo,
        DateTimeOffset committedAt)
    {
        using var tx = _connection.BeginTransaction(deferred: false);

        long next;
        using (var seqCmd = _connection.CreateCommand())
        {
            seqCmd.Transaction = tx;
            seqCmd.CommandText = "SELECT COALESCE(MAX(sequence), 0) + 1 FROM utterances WHERE session_id = @s;";
            seqCmd.Parameters.AddWithValue("@s", session.ToString());
            next = (long)seqCmd.ExecuteScalar()!;
        }

        var utterance = new Utterance(
            UtteranceId.New(), session, opportunity, speaker, next, text, addressee, repliesTo, committedAt);

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO utterances
                    (id, session_id, opportunity_id, speaker, sequence, text, addressee, replies_to, committed_at)
                VALUES (@id, @s, @opp, @speaker, @seq, @text, @addressee, @replies, @at);
                """;
            cmd.Parameters.AddWithValue("@id", utterance.Id.ToString());
            cmd.Parameters.AddWithValue("@s", session.ToString());
            cmd.Parameters.AddWithValue("@opp", opportunity.ToString());
            cmd.Parameters.AddWithValue("@speaker", speaker.ToString());
            cmd.Parameters.AddWithValue("@seq", next);
            cmd.Parameters.AddWithValue("@text", text);
            cmd.Parameters.AddWithValue("@addressee", (object?)(addressee?.ToString()) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@replies", (object?)(repliesTo?.ToString()) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@at", Iso(committedAt));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return utterance;
    }

    // --- reads --------------------------------------------------------------

    public long MaxSequence(SessionId session)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM utterances WHERE session_id = @s;";
        cmd.Parameters.AddWithValue("@s", session.ToString());
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<Utterance> GetCommittedUtterances(SessionId session)
    {
        var result = new List<Utterance>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, opportunity_id, speaker, sequence, text, addressee, replies_to, committed_at
            FROM utterances WHERE session_id = @s ORDER BY sequence ASC;
            """;
        cmd.Parameters.AddWithValue("@s", session.ToString());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new Utterance(
                UtteranceId.Parse(r.GetString(0)),
                session,
                OpportunityId.Parse(r.GetString(1)),
                CharacterId.Parse(r.GetString(2)),
                r.GetInt64(3),
                r.GetString(4),
                r.IsDBNull(5) ? null : CharacterId.Parse(r.GetString(5)),
                r.IsDBNull(6) ? null : UtteranceId.Parse(r.GetString(6)),
                DateTimeOffset.Parse(r.GetString(7))));
        }

        return result;
    }

    public CharacterProfile? GetCharacter(CharacterId id, int version)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name, persona, traits FROM characters WHERE id = @id AND version = @v;";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@v", version);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var traits = JsonSerializer.Deserialize<List<string>>(r.GetString(2)) ?? new List<string>();
        return new CharacterProfile(id, version, r.GetString(0), r.GetString(1), traits);
    }

    public Briefing? GetBriefing(BriefingId id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT title, source, url, body FROM briefings WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Briefing(id, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
    }

    public Session? GetSession(SessionId id)
    {
        List<CharacterId> participants = new();
        using (var pcmd = _connection.CreateCommand())
        {
            pcmd.CommandText = "SELECT character_id FROM session_participants WHERE session_id = @s ORDER BY ordinal;";
            pcmd.Parameters.AddWithValue("@s", id.ToString());
            using var pr = pcmd.ExecuteReader();
            while (pr.Read()) participants.Add(CharacterId.Parse(pr.GetString(0)));
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT mode, briefing_id, protocol_version, model_config_version, context_version,
                   status, created_at, ended_at
            FROM sessions WHERE id = @s;
            """;
        cmd.Parameters.AddWithValue("@s", id.ToString());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Session(
            id,
            participants,
            Enum.Parse<SessionMode>(r.GetString(0)),
            BriefingId.Parse(r.GetString(1)),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            Enum.Parse<SessionStatus>(r.GetString(5)),
            DateTimeOffset.Parse(r.GetString(6)),
            r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)));
    }

    public IReadOnlyList<ModelAttempt> GetAttempts(SessionId session)
    {
        var result = new List<ModelAttempt>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.opportunity_id, a.participant, a.model_name, a.model_digest,
                   a.protocol_version, a.outcome, a.retry_of, a.at
            FROM model_attempts a
            JOIN speaking_opportunities o ON o.id = a.opportunity_id
            WHERE o.session_id = @s
            ORDER BY a.at ASC;
            """;
        cmd.Parameters.AddWithValue("@s", session.ToString());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new ModelAttempt(
                AttemptId.Parse(r.GetString(0)),
                OpportunityId.Parse(r.GetString(1)),
                CharacterId.Parse(r.GetString(2)),
                r.GetString(3),
                r.GetString(4),
                r.GetInt32(5),
                Enum.Parse<AttemptOutcome>(r.GetString(6)),
                r.IsDBNull(7) ? null : AttemptId.Parse(r.GetString(7)),
                DateTimeOffset.Parse(r.GetString(8))));
        }

        return result;
    }

    /// <summary>Ended sessions whose participant set is exactly the one given, excluding one session,
    /// oldest first. This is the set of prior conversations "between these same people" that a continuing
    /// session draws its recollections from.</summary>
    public IReadOnlyList<SessionId> FindPriorSessions(IReadOnlyList<CharacterId> participants, SessionId exclude)
    {
        if (participants.Count == 0) return Array.Empty<SessionId>();

        // Never recall a conversation that ended after this encounter began.
        // A missing current session is not a license to retrieve arbitrary history.
        var current = GetSession(exclude);
        if (current is null || !participants.ToHashSet().SetEquals(current.Participants) ||
            participants.Count != current.Participants.Count)
            return Array.Empty<SessionId>();

        var placeholders = string.Join(",", participants.Select((_, i) => $"@p{i}"));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT s.id
            FROM sessions s
            JOIN session_participants sp ON sp.session_id = s.id
            WHERE s.status = 'Ended' AND s.id <> @exclude
              AND s.ended_at IS NOT NULL AND s.ended_at <= @current_created
              AND s.created_at <= @current_created
            GROUP BY s.id, s.created_at
            HAVING COUNT(*) = @count
               AND SUM(CASE WHEN sp.character_id IN ({placeholders}) THEN 1 ELSE 0 END) = @count
            ORDER BY s.created_at ASC;
            """;
        cmd.Parameters.AddWithValue("@exclude", exclude.ToString());
        cmd.Parameters.AddWithValue("@current_created", Iso(current.CreatedAt));
        cmd.Parameters.AddWithValue("@count", participants.Count);
        for (var i = 0; i < participants.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", participants[i].ToString());

        var result = new List<SessionId>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(SessionId.Parse(r.GetString(0)));
        return result;
    }

    // --- helpers ------------------------------------------------------------

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O");

    public void Dispose() => _connection.Dispose();
}

namespace BusStop.Persistence;

/// <summary>The SQLite schema. The two constraints that carry the integrity guarantees are
/// UNIQUE(session_id, sequence) on utterances (no duplicate or out-of-order committed speech) and
/// UNIQUE(opportunity_id) on utterances (at most one committed utterance per opportunity, which
/// makes replay exactly-once).</summary>
internal static class Schema
{
    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS characters (
            id          TEXT NOT NULL,
            version     INTEGER NOT NULL,
            name        TEXT NOT NULL,
            persona     TEXT NOT NULL,
            traits      TEXT NOT NULL,
            PRIMARY KEY (id, version)
        );

        CREATE TABLE IF NOT EXISTS briefings (
            id     TEXT PRIMARY KEY,
            title  TEXT NOT NULL,
            source TEXT NOT NULL,
            url    TEXT NOT NULL,
            body   TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sessions (
            id                   TEXT PRIMARY KEY,
            mode                 TEXT NOT NULL,
            briefing_id          TEXT NOT NULL,
            protocol_version     TEXT NOT NULL,
            model_config_version TEXT NOT NULL,
            context_version      TEXT NOT NULL,
            status               TEXT NOT NULL,
            created_at           TEXT NOT NULL,
            ended_at             TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS session_participants (
            session_id   TEXT NOT NULL,
            ordinal      INTEGER NOT NULL,
            character_id TEXT NOT NULL,
            PRIMARY KEY (session_id, ordinal)
        );

        CREATE TABLE IF NOT EXISTS speaking_opportunities (
            id                   TEXT PRIMARY KEY,
            session_id           TEXT NOT NULL,
            ordinal              INTEGER NOT NULL,
            intended_participant TEXT NOT NULL,
            reason               TEXT NOT NULL,
            committed_boundary   INTEGER NOT NULL,
            status               TEXT NOT NULL,
            UNIQUE (session_id, ordinal)
        );

        CREATE TABLE IF NOT EXISTS model_attempts (
            id               TEXT PRIMARY KEY,
            opportunity_id   TEXT NOT NULL,
            participant      TEXT NOT NULL,
            model_name       TEXT NOT NULL,
            model_digest     TEXT NOT NULL,
            protocol_version INTEGER NOT NULL,
            outcome          TEXT NOT NULL,
            retry_of         TEXT NULL,
            at               TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS participant_decisions (
            opportunity_id TEXT PRIMARY KEY,
            participant    TEXT NOT NULL,
            action         TEXT NOT NULL,
            addressee      TEXT NULL,
            replies_to     TEXT NULL,
            attempt_id     TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS utterances (
            id             TEXT PRIMARY KEY,
            session_id     TEXT NOT NULL,
            opportunity_id TEXT NOT NULL UNIQUE,
            speaker        TEXT NOT NULL,
            sequence       INTEGER NOT NULL,
            text           TEXT NOT NULL,
            addressee      TEXT NULL,
            replies_to     TEXT NULL,
            committed_at   TEXT NOT NULL,
            UNIQUE (session_id, sequence)
        );
        """;
}

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
            ended_at             TEXT NULL,
            moderation_policy_marker TEXT NULL
        );

        -- New sessions pin an immutable policy. Legacy sessions have a NULL marker
        -- and no policy row; a non-NULL marker with a missing row fails closed.
        CREATE TABLE IF NOT EXISTS session_moderation_policies (
            session_id                  TEXT PRIMARY KEY REFERENCES sessions(id),
            contract_version            TEXT NOT NULL,
            mode                        TEXT NOT NULL,
            rule_version                TEXT NOT NULL,
            prompt_version              TEXT NOT NULL,
            model_name                  TEXT NULL,
            model_digest                TEXT NULL,
            temperature                 REAL NULL,
            think                       INTEGER NULL,
            max_moderator_calls         INTEGER NOT NULL,
            max_revisions               INTEGER NOT NULL,
            stagnation_window           INTEGER NOT NULL,
            nudge_cooldown              INTEGER NOT NULL,
            shadow_sample_every         INTEGER NOT NULL DEFAULT 0
        );

        -- Reserved before future inference dispatch. Reserved/interrupted attempts
        -- count against the separate moderator ceiling even after a crash.
        CREATE TABLE IF NOT EXISTS moderator_inference_reservations (
            request_id       TEXT PRIMARY KEY,
            session_id       TEXT NOT NULL REFERENCES sessions(id),
            opportunity_id   TEXT NULL REFERENCES speaking_opportunities(id),
            purpose          TEXT NOT NULL,
            status           TEXT NOT NULL,
            reserved_at      TEXT NOT NULL
        );

        -- This is a PRIVATE observational ledger. It is never used to build
        -- participant prompts, recollections or the public conversation transcript.
        CREATE TABLE IF NOT EXISTS shadow_moderation_reviews (
            candidate_id        TEXT PRIMARY KEY,
            session_id          TEXT NOT NULL REFERENCES sessions(id),
            opportunity_id      TEXT NOT NULL UNIQUE REFERENCES speaking_opportunities(id),
            utterance_id        TEXT NOT NULL REFERENCES utterances(id),
            speaker_id          TEXT NOT NULL,
            committed_boundary  INTEGER NOT NULL,
            candidate_text      TEXT NOT NULL,
            trigger_kind        TEXT NOT NULL,
            authorized_refs     TEXT NOT NULL,
            model_name          TEXT NOT NULL,
            model_digest        TEXT NOT NULL,
            request_id          TEXT NULL REFERENCES moderator_inference_reservations(request_id),
            review_status       TEXT NOT NULL,
            judgment_json       TEXT NULL,
            severity            TEXT NOT NULL,
            raw_response        TEXT NULL,
            failure_detail      TEXT NULL,
            recorded_at         TEXT NOT NULL,
            attempts_json       TEXT NULL
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
        -- Source snapshots are separate from interpersonal recollections.
        -- The existing session/briefing tables remain unchanged.
        CREATE TABLE IF NOT EXISTS evidence_snapshots (
            id             TEXT PRIMARY KEY,
            kind           TEXT NOT NULL,
            title          TEXT NOT NULL,
            source_name    TEXT NOT NULL,
            source_url     TEXT NULL,
            published_at   TEXT NULL,
            rights_basis   TEXT NOT NULL,
            text_sha256    TEXT NOT NULL,
            captured_at    TEXT NOT NULL,
            snapshot_text  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS evidence_passages (
            snapshot_id    TEXT NOT NULL REFERENCES evidence_snapshots(id),
            ordinal        INTEGER NOT NULL,
            passage_id     TEXT NOT NULL UNIQUE,
            start_offset   INTEGER NOT NULL,
            length         INTEGER NOT NULL,
            text           TEXT NOT NULL,
            PRIMARY KEY (snapshot_id, ordinal)
        );

        CREATE TABLE IF NOT EXISTS session_evidence (
            session_id     TEXT PRIMARY KEY REFERENCES sessions(id),
            snapshot_id    TEXT NOT NULL REFERENCES evidence_snapshots(id),
            briefing_id    TEXT NOT NULL REFERENCES briefings(id),
            passage_ids    TEXT NOT NULL
        );
        """;
}

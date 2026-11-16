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

        -- V2 private verifier-only sidecar. No existing transcript or M1 table is changed.
        -- An absent anchor set is deliberately interpreted as ZERO binding anchors.
        CREATE TABLE IF NOT EXISTS character_anchor_sets (
            character_id       TEXT NOT NULL,
            profile_version    INTEGER NOT NULL,
            owner_review_ref   TEXT NOT NULL,
            sealed_at          TEXT NOT NULL,
            PRIMARY KEY (character_id, profile_version),
            FOREIGN KEY (character_id, profile_version)
                REFERENCES characters(id, version)
        );

        CREATE TABLE IF NOT EXISTS character_anchors (
            character_id       TEXT NOT NULL,
            profile_version    INTEGER NOT NULL,
            anchor_key         TEXT NOT NULL,
            anchor_value       TEXT NOT NULL,
            exposure           TEXT NOT NULL CHECK (exposure IN ('SubjectOnly', 'SharedWithRoom')),
            PRIMARY KEY (character_id, profile_version, anchor_key),
            FOREIGN KEY (character_id, profile_version)
                REFERENCES character_anchor_sets(character_id, profile_version)
        );

        -- These pins are explicit and only admitted before the first opportunity.
        -- Legacy sessions with no pins never inherit a new profile's anchors.
        CREATE TABLE IF NOT EXISTS session_verifier_profile_pins (
            session_id          TEXT NOT NULL REFERENCES sessions(id),
            character_id        TEXT NOT NULL,
            profile_version     INTEGER NOT NULL,
            pinned_at           TEXT NOT NULL,
            PRIMARY KEY (session_id, character_id),
            FOREIGN KEY (character_id, profile_version)
                REFERENCES characters(id, version)
        );

        CREATE TABLE IF NOT EXISTS character_history_events (
            event_id            TEXT PRIMARY KEY,
            subject_id          TEXT NOT NULL,
            history_key         TEXT NOT NULL,
            statement           TEXT NOT NULL,
            source_session_id   TEXT NOT NULL REFERENCES sessions(id),
            source_utterance_id TEXT NOT NULL REFERENCES utterances(id),
            source_sequence     INTEGER NOT NULL,
            event_kind          TEXT NOT NULL
                CHECK (event_kind IN ('Provisional', 'ReviewedSelfReaffirmation', 'ReviewedSelfCorrection')),
            previous_event_id   TEXT NULL UNIQUE REFERENCES character_history_events(event_id),
            owner_review_ref    TEXT NULL,
            recorded_at         TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_character_history_subject
            ON character_history_events(subject_id, history_key, recorded_at);

        -- Enforce append-only authority even if a future writer is careless.
        CREATE TRIGGER IF NOT EXISTS v2_anchor_sets_no_update BEFORE UPDATE ON character_anchor_sets
            BEGIN SELECT RAISE(ABORT, 'Immutable character anchor set'); END;
        CREATE TRIGGER IF NOT EXISTS v2_anchor_sets_no_delete BEFORE DELETE ON character_anchor_sets
            BEGIN SELECT RAISE(ABORT, 'Immutable character anchor set'); END;
        CREATE TRIGGER IF NOT EXISTS v2_anchors_no_update BEFORE UPDATE ON character_anchors
            BEGIN SELECT RAISE(ABORT, 'Immutable character anchor'); END;
        CREATE TRIGGER IF NOT EXISTS v2_anchors_no_delete BEFORE DELETE ON character_anchors
            BEGIN SELECT RAISE(ABORT, 'Immutable character anchor'); END;
        CREATE TRIGGER IF NOT EXISTS v2_profile_pins_no_update BEFORE UPDATE ON session_verifier_profile_pins
            BEGIN SELECT RAISE(ABORT, 'Immutable verifier profile pin'); END;
        CREATE TRIGGER IF NOT EXISTS v2_profile_pins_no_delete BEFORE DELETE ON session_verifier_profile_pins
            BEGIN SELECT RAISE(ABORT, 'Immutable verifier profile pin'); END;
        CREATE TRIGGER IF NOT EXISTS v2_history_no_update BEFORE UPDATE ON character_history_events
            BEGIN SELECT RAISE(ABORT, 'Immutable character history event'); END;
        CREATE TRIGGER IF NOT EXISTS v2_history_no_delete BEFORE DELETE ON character_history_events
            BEGIN SELECT RAISE(ABORT, 'Immutable character history event'); END;
        -- V4 is private and explicitly session-pinned, never default-enabled.
        -- M1 retains its original schema, values and independent audit rows.
        CREATE TABLE IF NOT EXISTS session_v4_verifier_policies (
            session_id             TEXT PRIMARY KEY REFERENCES sessions(id),
            contract_version       TEXT NOT NULL,
            mode                   TEXT NOT NULL CHECK(mode = 'Shadow'),
            rule_version           TEXT NOT NULL,
            extraction_prompt      TEXT NOT NULL,
            verification_prompt    TEXT NOT NULL,
            retrieval_selector     TEXT NOT NULL,
            verification_schema    TEXT NOT NULL,
            model_name             TEXT NOT NULL,
            model_digest           TEXT NOT NULL,
            temperature            REAL NOT NULL,
            think                  INTEGER NOT NULL CHECK(think IN (0, 1)),
            max_session_calls      INTEGER NOT NULL,
            max_utterance_calls    INTEGER NOT NULL,
            max_verification_calls INTEGER NOT NULL,
            generated_tokens       INTEGER NOT NULL,
            context_tokens         INTEGER NOT NULL
        );

        -- Phase is append-only and references the SAME shared charged request
        -- in moderator_inference_reservations. This is not a parallel budget.
        CREATE TABLE IF NOT EXISTS v4_verifier_attempt_phases (
            request_id    TEXT PRIMARY KEY REFERENCES moderator_inference_reservations(request_id),
            job_id        TEXT NOT NULL,
            session_id    TEXT NOT NULL REFERENCES sessions(id),
            utterance_id  TEXT NOT NULL REFERENCES utterances(id),
            phase         TEXT NOT NULL CHECK(phase IN ('ClaimExtraction', 'ClaimVerification')),
            policy_version TEXT NOT NULL,
            reserved_at    TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_v4_verifier_attempt_utterance
            ON v4_verifier_attempt_phases(utterance_id, phase);

        CREATE TRIGGER IF NOT EXISTS v4_policy_no_update BEFORE UPDATE ON session_v4_verifier_policies
            BEGIN SELECT RAISE(ABORT, 'Immutable V4 session policy'); END;
        CREATE TRIGGER IF NOT EXISTS v4_policy_no_delete BEFORE DELETE ON session_v4_verifier_policies
            BEGIN SELECT RAISE(ABORT, 'Immutable V4 session policy'); END;
        CREATE TRIGGER IF NOT EXISTS v4_phases_no_update BEFORE UPDATE ON v4_verifier_attempt_phases
            BEGIN SELECT RAISE(ABORT, 'Immutable V4 inference phase'); END;
        CREATE TRIGGER IF NOT EXISTS v4_phases_no_delete BEFORE DELETE ON v4_verifier_attempt_phases
            BEGIN SELECT RAISE(ABORT, 'Immutable V4 inference phase'); END;
        """;
}

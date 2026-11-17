namespace BusStop.Persistence;

/// <summary>
/// V5a is an independent PRIVATE ledger. Existing M1, V2 and V4 rows retain
/// their schema and semantics. Every ledger relation is append-only.
/// </summary>
internal static class V5aLedgerSchema
{
    public const string Version = "verifier-ledger-v5a-1";

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS verifier_v5a_schema (
            singleton       INTEGER PRIMARY KEY CHECK(singleton = 1),
            version         TEXT NOT NULL
        );
        INSERT OR IGNORE INTO verifier_v5a_schema(singleton, version)
            VALUES(1, 'verifier-ledger-v5a-1');

        CREATE TABLE IF NOT EXISTS verifier_v5a_jobs (
            job_id             TEXT PRIMARY KEY,
            session_id         TEXT NOT NULL REFERENCES sessions(id),
            opportunity_id     TEXT NOT NULL REFERENCES speaking_opportunities(id),
            utterance_id       TEXT NOT NULL REFERENCES utterances(id),
            speaker_id         TEXT NOT NULL,
            committed_boundary INTEGER NOT NULL CHECK(committed_boundary >= 0),
            utterance_sha256   TEXT NOT NULL,
            job_contract       TEXT NOT NULL,
            evaluation_key     TEXT NOT NULL,
            policy_json        TEXT NOT NULL,
            policy_sha256      TEXT NOT NULL,
            registered_at      TEXT NOT NULL,
            UNIQUE(utterance_id, evaluation_key)
        );

        CREATE TABLE IF NOT EXISTS verifier_v5a_events (
            job_id          TEXT NOT NULL REFERENCES verifier_v5a_jobs(job_id),
            sequence        INTEGER NOT NULL CHECK(sequence > 0),
            event_key       TEXT NOT NULL,
            stage           TEXT NOT NULL CHECK(stage IN
                ('Registered', 'ClaimsSealed', 'PacketsSealed',
                 'AttemptLinked', 'ClaimResultRecorded', 'SummarySealed')),
            payload_json    TEXT NOT NULL,
            payload_sha256  TEXT NOT NULL,
            previous_sha256 TEXT NULL,
            event_sha256    TEXT NOT NULL,
            recorded_at     TEXT NOT NULL,
            PRIMARY KEY(job_id, sequence),
            UNIQUE(job_id, event_key),
            UNIQUE(job_id, event_sha256)
        );

        CREATE TABLE IF NOT EXISTS verifier_v5a_claims (
            job_id         TEXT NOT NULL REFERENCES verifier_v5a_jobs(job_id),
            claim_id       TEXT NOT NULL,
            claim_index    INTEGER NOT NULL CHECK(claim_index >= 0),
            claim_json     TEXT NOT NULL,
            claim_sha256   TEXT NOT NULL,
            PRIMARY KEY(job_id, claim_id),
            UNIQUE(job_id, claim_index)
        );

        CREATE TABLE IF NOT EXISTS verifier_v5a_packets (
            packet_id           TEXT PRIMARY KEY,
            job_id              TEXT NOT NULL,
            claim_id            TEXT NOT NULL,
            canonical_json      TEXT NOT NULL,
            packet_sha256       TEXT NOT NULL,
            completeness        TEXT NOT NULL,
            retrieval_version   TEXT NOT NULL,
            prompt_version      TEXT NOT NULL,
            FOREIGN KEY(job_id, claim_id)
                REFERENCES verifier_v5a_claims(job_id, claim_id),
            UNIQUE(job_id, claim_id)
        );

        CREATE TABLE IF NOT EXISTS verifier_v5a_attempt_links (
            request_id         TEXT PRIMARY KEY
                REFERENCES moderator_inference_reservations(request_id),
            job_id             TEXT NOT NULL REFERENCES verifier_v5a_jobs(job_id),
            phase              TEXT NOT NULL
                CHECK(phase IN ('ClaimExtraction', 'ClaimVerification')),
            claim_ids_json     TEXT NOT NULL,
            claim_ids_sha256   TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS verifier_v5a_results (
            job_id            TEXT NOT NULL,
            claim_id          TEXT NOT NULL,
            operational_status TEXT NOT NULL,
            packet_id         TEXT NULL REFERENCES verifier_v5a_packets(packet_id),
            result_json       TEXT NOT NULL,
            result_sha256     TEXT NOT NULL,
            judgment_schema  TEXT NOT NULL,
            PRIMARY KEY(job_id, claim_id),
            FOREIGN KEY(job_id, claim_id)
                REFERENCES verifier_v5a_claims(job_id, claim_id)
        );

        CREATE TABLE IF NOT EXISTS verifier_v5a_summaries (
            job_id         TEXT PRIMARY KEY REFERENCES verifier_v5a_jobs(job_id),
            summary_json   TEXT NOT NULL,
            summary_sha256 TEXT NOT NULL,
            sealed_at      TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_v5a_jobs_session
            ON verifier_v5a_jobs(session_id, registered_at, job_id);
        CREATE INDEX IF NOT EXISTS ix_v5a_events_job_stage
            ON verifier_v5a_events(job_id, stage, sequence);
        CREATE INDEX IF NOT EXISTS ix_v5a_attempts_job
            ON verifier_v5a_attempt_links(job_id, phase);

        CREATE TRIGGER IF NOT EXISTS v5a_schema_no_update
            BEFORE UPDATE ON verifier_v5a_schema
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a schema version'); END;
        CREATE TRIGGER IF NOT EXISTS v5a_schema_no_delete
            BEFORE DELETE ON verifier_v5a_schema
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a schema version'); END;

        CREATE TRIGGER IF NOT EXISTS v5a_jobs_no_update
            BEFORE UPDATE ON verifier_v5a_jobs
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a job'); END;
        CREATE TRIGGER IF NOT EXISTS v5a_jobs_no_delete
            BEFORE DELETE ON verifier_v5a_jobs
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a job'); END;

        CREATE TRIGGER IF NOT EXISTS v5a_events_no_update
            BEFORE UPDATE ON verifier_v5a_events
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a stage event'); END;
        CREATE TRIGGER IF NOT EXISTS v5a_events_no_delete
            BEFORE DELETE ON verifier_v5a_events
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a stage event'); END;

        CREATE TRIGGER IF NOT EXISTS v5a_claims_no_update
            BEFORE UPDATE ON verifier_v5a_claims
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a claim'); END;
        CREATE TRIGGER IF NOT EXISTS v5a_claims_no_delete
            BEFORE DELETE ON verifier_v5a_claims
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a claim'); END;

        CREATE TRIGGER IF NOT EXISTS v5a_packets_no_update
            BEFORE UPDATE ON verifier_v5a_packets
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a packet'); END;
        CREATE TRIGGER IF NOT EXISTS v5a_packets_no_delete
            BEFORE DELETE ON verifier_v5a_packets
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a packet'); END;

        CREATE TRIGGER IF NOT EXISTS v5a_links_no_update
            BEFORE UPDATE ON verifier_v5a_attempt_links
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a attempt link'); END;
        CREATE TRIGGER IF NOT EXISTS v5a_links_no_delete
            BEFORE DELETE ON verifier_v5a_attempt_links
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a attempt link'); END;

        CREATE TRIGGER IF NOT EXISTS v5a_results_no_update
            BEFORE UPDATE ON verifier_v5a_results
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a claim result'); END;
        CREATE TRIGGER IF NOT EXISTS v5a_results_no_delete
            BEFORE DELETE ON verifier_v5a_results
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a claim result'); END;

        CREATE TRIGGER IF NOT EXISTS v5a_summaries_no_update
            BEFORE UPDATE ON verifier_v5a_summaries
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a summary'); END;
        CREATE TRIGGER IF NOT EXISTS v5a_summaries_no_delete
            BEFORE DELETE ON verifier_v5a_summaries
            BEGIN SELECT RAISE(ABORT, 'Immutable V5a summary'); END;
        """;
}

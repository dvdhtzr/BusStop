namespace BusStop.Persistence;

internal static class V5cRecoverySchema
{
    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS verifier_v5c_schema (
            singleton INTEGER PRIMARY KEY CHECK(singleton=1),
            version TEXT NOT NULL
        );
        INSERT OR IGNORE INTO verifier_v5c_schema(singleton, version)
        VALUES (1, 'verifier-recovery-v5c-1');

        -- Reconciliation is not a new attempt or epistemic verdict.
        -- It records the historical owner epoch and the recovery owner that
        -- classified an already charged Reserved attempt as Interrupted.
        CREATE TABLE IF NOT EXISTS verifier_v5c_reconciliations (
            request_id TEXT PRIMARY KEY
                REFERENCES v4_verifier_attempt_phases(request_id),
            job_id TEXT NOT NULL REFERENCES verifier_v5a_jobs(job_id),
            prior_epoch INTEGER NOT NULL,
            recovery_epoch INTEGER NOT NULL,
            recovery_owner_id TEXT NOT NULL,
            policy_sha256 TEXT NOT NULL,
            disposition TEXT NOT NULL CHECK(disposition='InterruptedAfterCrash'),
            recorded_at TEXT NOT NULL,
            CHECK(recovery_epoch > prior_epoch)
        );

        -- A corrupt/incompatible/missing checkpoint is terminal for this
        -- evaluation, not a new V5a summary and not a model "NoMaterialIssue".
        CREATE TABLE IF NOT EXISTS verifier_v5c_stops (
            job_id TEXT PRIMARY KEY REFERENCES verifier_v5a_jobs(job_id),
            disposition TEXT NOT NULL CHECK(disposition IN
                ('StoppedIncompatibleCheckpoint', 'StoppedMissingFrozenStage',
                 'StoppedMissingClaimPacket', 'StoppedUnknownAttemptOwner',
                 'StoppedNoRemainingBudget')),
            reason_code TEXT NOT NULL,
            owner_epoch INTEGER NOT NULL,
            owner_id TEXT NOT NULL,
            recorded_at TEXT NOT NULL
        );

        CREATE TRIGGER IF NOT EXISTS v5c_schema_no_update
            BEFORE UPDATE ON verifier_v5c_schema
            BEGIN SELECT RAISE(ABORT,'Immutable V5c schema'); END;
        CREATE TRIGGER IF NOT EXISTS v5c_schema_no_delete
            BEFORE DELETE ON verifier_v5c_schema
            BEGIN SELECT RAISE(ABORT,'Immutable V5c schema'); END;

        CREATE TRIGGER IF NOT EXISTS v5c_recon_no_update
            BEFORE UPDATE ON verifier_v5c_reconciliations
            BEGIN SELECT RAISE(ABORT,'Immutable V5c reconciliation'); END;
        CREATE TRIGGER IF NOT EXISTS v5c_recon_no_delete
            BEFORE DELETE ON verifier_v5c_reconciliations
            BEGIN SELECT RAISE(ABORT,'Immutable V5c reconciliation'); END;

        CREATE TRIGGER IF NOT EXISTS v5c_stops_no_update
            BEFORE UPDATE ON verifier_v5c_stops
            BEGIN SELECT RAISE(ABORT,'Immutable V5c stop'); END;
        CREATE TRIGGER IF NOT EXISTS v5c_stops_no_delete
            BEFORE DELETE ON verifier_v5c_stops
            BEGIN SELECT RAISE(ABORT,'Immutable V5c stop'); END;
        """;
}

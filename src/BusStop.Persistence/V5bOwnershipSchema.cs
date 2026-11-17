namespace BusStop.Persistence;

internal static class V5bOwnershipSchema
{
    public const string Version = V5bOwnershipContract.SchemaVersion;

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS verifier_v5b_schema (
            singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
            version TEXT NOT NULL
        );
        INSERT OR IGNORE INTO verifier_v5b_schema(singleton, version)
            VALUES(1, 'verifier-ownership-v5b-1');

        -- No mutable lease rows, wall-clock expiries or automatic steals.
        -- The latest durable epoch fences all earlier session owners.
        CREATE TABLE IF NOT EXISTS verifier_v5b_epochs (
            session_id TEXT NOT NULL REFERENCES sessions(id),
            epoch INTEGER NOT NULL CHECK(epoch > 0),
            owner_id TEXT NOT NULL,
            gate_id TEXT NOT NULL,
            acquired_at TEXT NOT NULL,
            PRIMARY KEY(session_id, epoch)
        );

        -- Release is a distinct, append-only fact. The OS file gate must be
        -- held before any acquisition or release. An abandoned active epoch
        -- can be superseded by a NEW gate holder, never by elapsed time.
        CREATE TABLE IF NOT EXISTS verifier_v5b_releases (
            session_id TEXT NOT NULL,
            epoch INTEGER NOT NULL,
            owner_id TEXT NOT NULL,
            released_at TEXT NOT NULL,
            PRIMARY KEY(session_id, epoch),
            FOREIGN KEY(session_id, epoch)
                REFERENCES verifier_v5b_epochs(session_id, epoch)
        );

        -- An inference reserved under epoch N cannot later be promoted
        -- to Completed by an owner of N+1. Do not rewrite old charge rows.
        CREATE TABLE IF NOT EXISTS verifier_v5b_attempt_epochs (
            request_id TEXT PRIMARY KEY
                REFERENCES v4_verifier_attempt_phases(request_id),
            session_id TEXT NOT NULL REFERENCES sessions(id),
            epoch INTEGER NOT NULL,
            owner_id TEXT NOT NULL,
            gate_id TEXT NOT NULL,
            FOREIGN KEY(session_id, epoch)
                REFERENCES verifier_v5b_epochs(session_id, epoch)
        );

        CREATE TRIGGER IF NOT EXISTS v5b_attempt_epochs_no_update
            BEFORE UPDATE ON verifier_v5b_attempt_epochs
            BEGIN SELECT RAISE(ABORT, 'Immutable V5b charged attempt epoch'); END;
        CREATE TRIGGER IF NOT EXISTS v5b_attempt_epochs_no_delete
            BEFORE DELETE ON verifier_v5b_attempt_epochs
            BEGIN SELECT RAISE(ABORT, 'Immutable V5b charged attempt epoch'); END;

        CREATE TRIGGER IF NOT EXISTS v5b_schema_no_update
            BEFORE UPDATE ON verifier_v5b_schema
            BEGIN SELECT RAISE(ABORT, 'Immutable V5b schema pin'); END;
        CREATE TRIGGER IF NOT EXISTS v5b_schema_no_delete
            BEFORE DELETE ON verifier_v5b_schema
            BEGIN SELECT RAISE(ABORT, 'Immutable V5b schema pin'); END;

        CREATE TRIGGER IF NOT EXISTS v5b_epochs_no_update
            BEFORE UPDATE ON verifier_v5b_epochs
            BEGIN SELECT RAISE(ABORT, 'Immutable V5b owner epoch'); END;
        CREATE TRIGGER IF NOT EXISTS v5b_epochs_no_delete
            BEFORE DELETE ON verifier_v5b_epochs
            BEGIN SELECT RAISE(ABORT, 'Immutable V5b owner epoch'); END;
        CREATE TRIGGER IF NOT EXISTS v5b_releases_no_update
            BEFORE UPDATE ON verifier_v5b_releases
            BEGIN SELECT RAISE(ABORT, 'Immutable V5b release'); END;
        CREATE TRIGGER IF NOT EXISTS v5b_releases_no_delete
            BEFORE DELETE ON verifier_v5b_releases
            BEGIN SELECT RAISE(ABORT, 'Immutable V5b release'); END;
        """;
}

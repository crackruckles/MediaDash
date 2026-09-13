-- Replay of MediaDashDb constructor's CREATE TABLE block + MigrateSchema (v0..v9).
-- Apply this to any old DB; result should match what the C# constructor produces.
-- Idempotent: runs safely against v0..v9 inputs. Does not set user_version until end.

-- Step 1: the fresh-install CREATE TABLE block (IF NOT EXISTS = no-op on existing tables).
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS issues (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    type INTEGER NOT NULL,
    item_id TEXT NOT NULL,
    path TEXT NOT NULL,
    details TEXT NOT NULL DEFAULT '{}',
    suggested_fix TEXT NOT NULL DEFAULT '',
    size_savings INTEGER NOT NULL DEFAULT 0,
    status INTEGER NOT NULL DEFAULT 0,
    detected_at_utc INTEGER NOT NULL,
    confidence REAL NULL
);
CREATE INDEX IF NOT EXISTS idx_issues_type_status ON issues(type, status);

CREATE TABLE IF NOT EXISTS probe_cache (
    path TEXT PRIMARY KEY,
    size INTEGER NOT NULL,
    mtime_utc INTEGER NOT NULL,
    probed_at_utc INTEGER NOT NULL,
    json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS decode_cache (
    path TEXT PRIMARY KEY,
    size INTEGER NOT NULL,
    mtime_utc INTEGER NOT NULL,
    checked_at_utc INTEGER NOT NULL,
    error TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS format_probe_cache (
    path TEXT PRIMARY KEY,
    size INTEGER NOT NULL,
    mtime_utc INTEGER NOT NULL,
    probed_at_utc INTEGER NOT NULL,
    ok INTEGER NOT NULL,
    reason TEXT NULL
);

CREATE TABLE IF NOT EXISTS file_hashes (
    path TEXT NOT NULL,
    size INTEGER NOT NULL,
    mtime INTEGER NOT NULL,
    hash TEXT NOT NULL,
    PRIMARY KEY (path, size, mtime)
);

CREATE TABLE IF NOT EXISTS history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    issue_id INTEGER NOT NULL,
    type INTEGER NOT NULL,
    path TEXT NOT NULL,
    action TEXT NOT NULL,
    bytes_freed INTEGER NOT NULL DEFAULT 0,
    recycle_path TEXT NULL,
    fixed_at_utc INTEGER NOT NULL,
    dry_run INTEGER NOT NULL DEFAULT 0,
    restored INTEGER NOT NULL DEFAULT 0,
    success INTEGER NOT NULL DEFAULT 1,
    acknowledged INTEGER NOT NULL DEFAULT 0,
    saved_bytes INTEGER NOT NULL DEFAULT 0,
    action_detail TEXT NULL
);

CREATE TABLE IF NOT EXISTS diagnostics (
    source TEXT NOT NULL,
    message_hash INTEGER NOT NULL,
    message TEXT NOT NULL,
    count INTEGER NOT NULL DEFAULT 1,
    at_utc INTEGER NOT NULL,
    last_at_utc INTEGER NOT NULL,
    PRIMARY KEY (source, message_hash)
);
CREATE INDEX IF NOT EXISTS idx_diagnostics_last_at_utc ON diagnostics(last_at_utc);

CREATE TABLE IF NOT EXISTS plugin_state (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS restored_paths (
    path TEXT NOT NULL,
    type INTEGER NOT NULL,
    restored_at_utc INTEGER NOT NULL,
    PRIMARY KEY (path, type)
);

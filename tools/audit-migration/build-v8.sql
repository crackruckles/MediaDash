-- Synthetic v8 mediadash.db: what a 1.0.7.5-user's DB looks like right before v9.
-- All tables + columns present as of v8, user_version=8, seeded with realistic rows across every table.
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS issues (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    type INTEGER NOT NULL, item_id TEXT NOT NULL, path TEXT NOT NULL,
    details TEXT NOT NULL DEFAULT '{}', suggested_fix TEXT NOT NULL DEFAULT '',
    size_savings INTEGER NOT NULL DEFAULT 0, status INTEGER NOT NULL DEFAULT 0,
    detected_at_utc INTEGER NOT NULL, confidence REAL NULL);
CREATE INDEX IF NOT EXISTS idx_issues_type_status ON issues(type, status);

CREATE TABLE IF NOT EXISTS probe_cache (
    path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime_utc INTEGER NOT NULL,
    probed_at_utc INTEGER NOT NULL, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS decode_cache (
    path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime_utc INTEGER NOT NULL,
    checked_at_utc INTEGER NOT NULL, error TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS format_probe_cache (
    path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime_utc INTEGER NOT NULL,
    probed_at_utc INTEGER NOT NULL, ok INTEGER NOT NULL, reason TEXT NULL);
CREATE TABLE IF NOT EXISTS file_hashes (
    path TEXT NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL, hash TEXT NOT NULL,
    PRIMARY KEY (path, size, mtime));
CREATE TABLE IF NOT EXISTS history (
    id INTEGER PRIMARY KEY AUTOINCREMENT, issue_id INTEGER NOT NULL, type INTEGER NOT NULL,
    path TEXT NOT NULL, action TEXT NOT NULL, bytes_freed INTEGER NOT NULL DEFAULT 0,
    recycle_path TEXT NULL, fixed_at_utc INTEGER NOT NULL, dry_run INTEGER NOT NULL DEFAULT 0,
    restored INTEGER NOT NULL DEFAULT 0, success INTEGER NOT NULL DEFAULT 1,
    acknowledged INTEGER NOT NULL DEFAULT 0, saved_bytes INTEGER NOT NULL DEFAULT 0,
    action_detail TEXT NULL);
CREATE TABLE IF NOT EXISTS diagnostics (
    source TEXT NOT NULL, message_hash INTEGER NOT NULL, message TEXT NOT NULL,
    count INTEGER NOT NULL DEFAULT 1, at_utc INTEGER NOT NULL, last_at_utc INTEGER NOT NULL,
    PRIMARY KEY (source, message_hash));
CREATE INDEX IF NOT EXISTS idx_diagnostics_last_at_utc ON diagnostics(last_at_utc);
CREATE TABLE IF NOT EXISTS plugin_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS restored_paths (
    path TEXT NOT NULL, type INTEGER NOT NULL, restored_at_utc INTEGER NOT NULL,
    PRIMARY KEY (path, type));

-- Realistic rows.
INSERT INTO issues (type,item_id,path,details,suggested_fix,size_savings,status,detected_at_utc,confidence) VALUES
  (0,'aa','C:\media\dup1.mkv','{}','delete',1000,0,17570000000000000,0.95),
  (1,'bb','C:\media\broken.mp4','{"reason":"no-video"}','remove',500,0,17570000000000000,NULL),
  (2,'cc','C:\media\big.mkv','{}','re-encode',2000,2,17570000000000000,NULL);
INSERT INTO probe_cache VALUES ('C:\media\dup1.mkv',1048576,17569990000000000,17570000000000000,'{"streams":[]}');
INSERT INTO decode_cache VALUES ('C:\media\broken.mp4',524288,17569990000000000,17570000000000000,'moov not found');
INSERT INTO format_probe_cache VALUES ('C:\media\big.mkv',2097152,17569990000000000,17570000000000000,1,NULL);
INSERT INTO file_hashes VALUES ('C:\media\dup1.mkv',1048576,17569990000000000,'sha1:abc');
INSERT INTO history (issue_id,type,path,action,bytes_freed,recycle_path,fixed_at_utc,dry_run,restored,success,acknowledged,saved_bytes,action_detail) VALUES
  (99,0,'C:\media\dup2.mkv','Removed duplicate "dup2.mkv"',1000,'C:\bin\dup2.mkv',17569000000000000,0,0,1,0,1000,NULL),
  (98,1,'C:\media\gone.mp4','Fix failed — the source file appears corrupt',0,NULL,17569000000000000,0,0,0,1,0,'ffmpeg: moov atom not found');
INSERT INTO diagnostics VALUES
  ('MediaSorter','12345','Bad target folder',3,17569000000000000,17570000000000000),
  ('Playability','67890','Truncating packet warning noise',5,17569000000000000,17570000000000000);
INSERT INTO plugin_state VALUES ('LastFixRun','2026-09-10T12:00:00Z');
INSERT INTO restored_paths VALUES ('C:\media\restored.mkv',0,17569500000000000);

-- v5 (post-1.0.6) fixture. Includes DISMISSED Playability rows to prove the v6
-- migration reverts the user's dismiss choice. Also FIXED Playability rows.
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS issues (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    type INTEGER NOT NULL, item_id TEXT NOT NULL, path TEXT NOT NULL,
    details TEXT NOT NULL DEFAULT '{}', suggested_fix TEXT NOT NULL DEFAULT '',
    size_savings INTEGER NOT NULL DEFAULT 0, status INTEGER NOT NULL DEFAULT 0,
    detected_at_utc INTEGER NOT NULL, confidence REAL NULL);
CREATE INDEX IF NOT EXISTS idx_issues_type_status ON issues(type, status);
CREATE TABLE IF NOT EXISTS probe_cache (path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime_utc INTEGER NOT NULL, probed_at_utc INTEGER NOT NULL, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS decode_cache (path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime_utc INTEGER NOT NULL, checked_at_utc INTEGER NOT NULL, error TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS format_probe_cache (path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime_utc INTEGER NOT NULL, probed_at_utc INTEGER NOT NULL, ok INTEGER NOT NULL, reason TEXT NULL);
CREATE TABLE IF NOT EXISTS file_hashes (path TEXT NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL, hash TEXT NOT NULL, PRIMARY KEY (path, size, mtime));
CREATE TABLE IF NOT EXISTS history (
    id INTEGER PRIMARY KEY AUTOINCREMENT, issue_id INTEGER NOT NULL, type INTEGER NOT NULL,
    path TEXT NOT NULL, action TEXT NOT NULL, bytes_freed INTEGER NOT NULL DEFAULT 0,
    recycle_path TEXT NULL, fixed_at_utc INTEGER NOT NULL, dry_run INTEGER NOT NULL DEFAULT 0,
    restored INTEGER NOT NULL DEFAULT 0, success INTEGER NOT NULL DEFAULT 1, acknowledged INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS diagnostics (source TEXT NOT NULL, message_hash INTEGER NOT NULL, message TEXT NOT NULL, count INTEGER NOT NULL DEFAULT 1, at_utc INTEGER NOT NULL, last_at_utc INTEGER NOT NULL, PRIMARY KEY (source, message_hash));
CREATE INDEX IF NOT EXISTS idx_diagnostics_last_at_utc ON diagnostics(last_at_utc);
CREATE TABLE IF NOT EXISTS plugin_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS restored_paths (path TEXT NOT NULL, type INTEGER NOT NULL, restored_at_utc INTEGER NOT NULL, PRIMARY KEY (path, type));

-- IssueStatus: Detected=0, Queued=1, Fixed=2, Dismissed=3. IssueType.Playability=1.
INSERT INTO issues (type,item_id,path,details,suggested_fix,size_savings,status,detected_at_utc,confidence) VALUES
  -- Playability rows in every status:
  (1,'p-det','C:\media\playdet.mp4','{"reason":"no-video"}','remove',100,0,17560000000000000,NULL),
  (1,'p-que','C:\media\playque.mp4','{"reason":"no-video"}','remove',100,1,17560000000000000,NULL),
  (1,'p-fix','C:\media\playfix.mp4','{"reason":"no-video"}','remove',100,2,17560000000000000,NULL),
  (1,'p-dis','C:\media\playdis.mp4','{"reason":"no-video"}','remove',100,3,17560000000000000,NULL),
  -- Non-Playability control rows (should survive):
  (0,'d-det','C:\media\dupdet.mkv','{}','delete',500,0,17560000000000000,0.9),
  (2,'q-det','C:\media\quadet.mkv','{}','re-encode',2000,0,17560000000000000,NULL),
  (3,'sl-det','C:\media\subs.mkv','{}','remove',0,3,17560000000000000,NULL);

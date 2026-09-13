-- Synthetic v0 mediadash.db: pre-migration schema (from 7ca4bf3).
-- Only `issues` + `probe_cache`, no user_version set.
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
    detected_at_utc INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_issues_type_status ON issues(type, status);

CREATE TABLE IF NOT EXISTS probe_cache (
    path TEXT PRIMARY KEY,
    size INTEGER NOT NULL,
    mtime_utc INTEGER NOT NULL,
    probed_at_utc INTEGER NOT NULL,
    json TEXT NOT NULL
);

-- Seed representative rows.
-- IssueType enum: Duplicate=0, Playability=1, Quality=2, SubtitleLanguage=3, AudioLanguage=4.
INSERT INTO issues (type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc) VALUES
  (0, '11111111111111111111111111111111', 'C:\media\v0-duplicate-a.mkv', '{"kind":"duplicate"}',  'delete duplicate', 1000, 0, 17000000000000000),
  (1, '44444444444444444444444444444444', 'C:\media\v0-play.mp4',        '{"reason":"no-video"}', 'remove',            500, 2, 17000000000000000),
  (2, '22222222222222222222222222222222', 'C:\media\v0-oversized.mkv',   '{"kind":"oversized"}',  're-encode',        2000, 0, 17000000000000000),
  (4, '33333333333333333333333333333333', 'C:\media\v0-audio.mkv',       '{"kind":"audiolang"}',  'remove track',        0, 0, 17000000000000000);

INSERT INTO probe_cache (path, size, mtime_utc, probed_at_utc, json) VALUES
  ('C:\media\v0-duplicate-a.mkv', 1048576, 16999999999999999, 17000000000000000, '{"streams":[]}'),
  ('C:\media\v0-oversized.mkv',   2097152, 16999999999999999, 17000000000000000, '{"streams":[]}');

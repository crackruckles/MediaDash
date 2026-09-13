-- v3 fixture: pre-1.0.6, only issues (no confidence), probe_cache, decode_cache, history (no acknowledged, no success from-v2 backfill), diagnostics (with GetHashCode based).
PRAGMA journal_mode = WAL;
CREATE TABLE issues (id INTEGER PRIMARY KEY AUTOINCREMENT, type INTEGER NOT NULL, item_id TEXT NOT NULL, path TEXT NOT NULL, details TEXT NOT NULL DEFAULT '{}', suggested_fix TEXT NOT NULL DEFAULT '', size_savings INTEGER NOT NULL DEFAULT 0, status INTEGER NOT NULL DEFAULT 0, detected_at_utc INTEGER NOT NULL);
CREATE INDEX idx_issues_type_status ON issues(type, status);
CREATE TABLE probe_cache (path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime_utc INTEGER NOT NULL, probed_at_utc INTEGER NOT NULL, json TEXT NOT NULL);
CREATE TABLE decode_cache (path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime_utc INTEGER NOT NULL, checked_at_utc INTEGER NOT NULL, error TEXT NOT NULL);
CREATE TABLE history (id INTEGER PRIMARY KEY AUTOINCREMENT, issue_id INTEGER NOT NULL, type INTEGER NOT NULL, path TEXT NOT NULL, action TEXT NOT NULL, bytes_freed INTEGER NOT NULL DEFAULT 0, recycle_path TEXT NULL, fixed_at_utc INTEGER NOT NULL, dry_run INTEGER NOT NULL DEFAULT 0, restored INTEGER NOT NULL DEFAULT 0, success INTEGER NOT NULL DEFAULT 1, acknowledged INTEGER NOT NULL DEFAULT 0);
INSERT INTO issues (type,item_id,path,details,suggested_fix,size_savings,status,detected_at_utc) VALUES
  (0,'a','C:\v3\dup.mkv','{}','delete',500,0,17400000000000000),
  (1,'b','C:\v3\broken.mp4','{}','remove',100,3,17400000000000000);
INSERT INTO history (issue_id,type,path,action,bytes_freed,recycle_path,fixed_at_utc,dry_run,restored,success,acknowledged) VALUES
  (1,0,'C:\v3\dupold.mkv','Removed duplicate',500,'C:\bin\dupold.mkv',17390000000000000,0,0,1,0),
  (2,1,'C:\v3\brokenold.mp4','Fix failed — decode error',0,NULL,17390000000000000,0,0,0,0);

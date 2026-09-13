#!/usr/bin/env bash
# Mirrors MediaDashDb.MigrateSchema (v1..v9) exactly. Usage: migrate.sh <db.sqlite>
# ponytail: shell replay of the C# migration, so tests need no dotnet build. If
# the C# migration drifts from this file the test loses signal — keep in sync.
set -euo pipefail
DB="${1:?usage: migrate.sh <db>}"
sq() { sqlite3 "$DB" "$@"; }

current=$(sq "PRAGMA user_version;")
echo "starting user_version=$current"

target=9
if [ "$current" -ge "$target" ]; then
  echo "already at $current, nothing to do"
  exit 0
fi

# Apply the fresh-install CREATE TABLE block first (matches C# constructor order).
sq < "$(dirname "$0")/apply-migration.sql"

has_col() {
  local table="$1" col="$2"
  sq "PRAGMA table_info($table);" | awk -F'|' -v c="$col" '$2==c{f=1} END{exit !f}'
}

if [ "$current" -lt 1 ]; then
  echo "-> v1: clear decode_cache"
  sq "DELETE FROM decode_cache;"
fi

if [ "$current" -lt 2 ]; then
  echo "-> v2: add history.success + backfill"
  if ! has_col history success; then
    sq "ALTER TABLE history ADD COLUMN success INTEGER NOT NULL DEFAULT 1;"
    sq "UPDATE history SET success = 0 WHERE action LIKE 'Fix failed%';"
  fi
fi

if [ "$current" -lt 3 ]; then
  echo "-> v3: add history.acknowledged"
  if ! has_col history acknowledged; then
    sq "ALTER TABLE history ADD COLUMN acknowledged INTEGER NOT NULL DEFAULT 0;"
  fi
fi

if [ "$current" -lt 4 ]; then
  echo "-> v4: add issues.confidence"
  if ! has_col issues confidence; then
    sq "ALTER TABLE issues ADD COLUMN confidence REAL NULL;"
  fi
fi

if [ "$current" -lt 5 ]; then
  echo "-> v5: create restored_paths (idempotent)"
  sq "CREATE TABLE IF NOT EXISTS restored_paths (path TEXT NOT NULL, type INTEGER NOT NULL, restored_at_utc INTEGER NOT NULL, PRIMARY KEY (path, type));"
fi

if [ "$current" -lt 6 ]; then
  echo "-> v6: clear decode_cache + drop queued Playability issues"
  sq "DELETE FROM decode_cache;"
  # IssueType.Playability = 1 (Data/IssueType.cs).
  sq "DELETE FROM issues WHERE type = 1;"
fi

if [ "$current" -lt 7 ]; then
  echo "-> v7: add history.saved_bytes"
  if ! has_col history saved_bytes; then
    sq "ALTER TABLE history ADD COLUMN saved_bytes INTEGER NOT NULL DEFAULT 0;"
  fi
fi

if [ "$current" -lt 8 ]; then
  echo "-> v8: add history.action_detail"
  if ! has_col history action_detail; then
    sq "ALTER TABLE history ADD COLUMN action_detail TEXT NULL;"
  fi
fi

if [ "$current" -lt 9 ]; then
  echo "-> v9: purge diagnostics"
  sq "DELETE FROM diagnostics;"
fi

sq "PRAGMA user_version = $target;"
echo "done, user_version=$(sq 'PRAGMA user_version;')"

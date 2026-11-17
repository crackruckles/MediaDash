using System;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

// Owns the physical schema: fresh-install DDL + the version ladder that upgrades pre-v6
// installs. Extracted from MediaDashDb in 05-A. The wire contract stays the same:
// * SchemaVersion / user_version stays 6 — no schema bump for a code move.
// * Every step is idempotent by design (DELETE on empty tables is a no-op; column probes precede
//   any ALTER TABLE; back-fills use conditional WHERE). PRAGMA user_version is only advanced
//   after every step succeeds, so a crash mid-migration re-runs cleanly on next boot.
// * No transaction wrapping. PRAGMA / DDL / VACUUM behavior is not interchangeable with ordinary
//   DDL under BEGIN — the pre-refactor code deliberately ran statements standalone and this
//   extraction preserves that boundary.
internal static class MediaDashSchema
{
    // Bump when a semantic change to the decode check would make old cache entries misleading.
    // v1: -xerror + exit-code-only in the decode check (2026-07-20) — previous stderr-noise-as-error entries invalidated.
    // v3: history.acknowledged column for redownload-warning acknowledgement (2026-08-17).
    // v4: issues.confidence column for the duplicate confidence ladder (2026-08-22).
    // v5: restored_paths table for auto-fix veto after user restore (2026-08-25).
    // v6: purge decode_cache after HasTruncationMarker "Truncating packet" fix (2026-08-31) — cached
    //     stderr from pre-fix ffmpeg runs poisoned every scan into re-flagging the same file.
    // v7: restore_operations table for owner-tokened restore claims + NeedsReview reconciliation
    //     (2026-09-22, plan §05-B). Replaces the `history.restored = 1` claim pattern that could
    //     not distinguish "restore in progress" from "restore completed" and had no owner token
    //     for release. history.restored still carries the completion signal for legacy readers.
    // v8: protection_rules table for persistent "Never modify" folder/library protection
    //     (roadmap i-110-protected-media). Enforced by ProtectionService at scan-emit and
    //     fix-execute time. Empty on upgrade.
    // v9: history.saved_bytes column persists FixResult.SavedBytes for Playability repair rows
    //     (previously the property existed on FixResult + HistoryEntry but was never written).
    //     Feeds Api.RepairSummary aggregations for the Overview "Broken files repaired" panel.
    //     Historical repair rows keep 0 — new repairs from this build onwards carry the
    //     pre-repair file size, which the UI footer discloses as "bytes counted from N onwards".
    // v10: MEDIA-02 / issue #76 — DecodeCheckAsync's default stream selection picked attached
    //     cover-art pictures on audio files (FLAC + JPEG embedded cover) and reported them as
    //     "truncated at 0s". Cached verdicts from pre-fix runs survive the upgrade on unchanged
    //     files (cache key is path+size+mtime); purge decode_cache once so corrected detection
    //     doesn't re-use the stale false-positive strings.
    // v11: issue #80 — HistoryStore.GetLatestAttemptsByIssueIds (new in 1.0.8.0) ran a correlated
    //     subquery against history with no supporting index; on users with substantial history +
    //     many open issues it stalled /MediaDash/Issues. The report also included disposed request
    //     services/memory-pool errors; their lifecycle cause has not been independently reproduced.
    //     Covering index on (issue_id, dry_run, fixed_at_utc) serves both the IN-filter and the
    //     MAX() subquery. Non-destructive — CREATE INDEX IF NOT EXISTS builds from existing rows.
    // v12: durable media-move manifests, path claims and the optional History operation link.
    // v13: native directory-anchor claims for atomic move/restore alias reservations.
    public const int Version = 14;

    // Sentinel stored in restored_paths.type when the restore came from a manifest-only bin entry
    // (RecycleBin/Items/Restore). We don't know which IssueType triggered the original recycle in
    // that case — the manifest sidecar only stores the origin path. Blocking auto-queue for ALL
    // types at that path is the safe default: the user restored the file, so they don't want any
    // scanner to auto-fix it again. Manual approval from the Issues tab still works. Lives on the
    // schema because it's a persisted-column value, not a per-store detail — IssueStore reads it
    // (auto-queue veto join) and MediaDashDb writes it (MarkPathRestoredForAnyType).
    internal const int RestoredPathAnyType = -1;

    // Runs the initial DDL. Safe on both fresh installs and on already-migrated DBs — every
    // statement uses IF NOT EXISTS and creates the target-shape schema. A fresh install ends
    // up on v6 shape immediately; MigrateSchema still runs afterwards to advance user_version
    // and to trigger the one-time semantic invalidations (decode_cache purge, playability
    // wipe) which are no-ops against the freshly-empty tables.
    public static void CreateTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
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

            CREATE TABLE IF NOT EXISTS audio_decode_cache (
                path TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                mtime_utc INTEGER NOT NULL,
                signature TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS format_probe_cache (
                path TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                mtime_utc INTEGER NOT NULL,
                probed_at_utc INTEGER NOT NULL,
                ok INTEGER NOT NULL,
                reason TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS interleave_cache (
                path TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                mtime_utc INTEGER NOT NULL,
                checked_at_utc INTEGER NOT NULL,
                offset_seconds REAL NOT NULL
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
                move_operation_id TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_history_issue_id_dry_run ON history(issue_id, dry_run, fixed_at_utc);

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

            CREATE TABLE IF NOT EXISTS restore_operations (
                operation_id TEXT PRIMARY KEY,
                entry_key TEXT NOT NULL UNIQUE,
                history_id INTEGER NULL,
                source_path TEXT NOT NULL,
                destination_path TEXT NOT NULL,
                state TEXT NOT NULL,
                started_utc_ticks INTEGER NOT NULL,
                detail TEXT NULL,
                native_claims_json TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_restore_operations_state ON restore_operations(state);

            CREATE TABLE IF NOT EXISTS media_move_operations (
                operation_id TEXT PRIMARY KEY,
                issue_id INTEGER NOT NULL,
                item_id TEXT NOT NULL,
                plan_version INTEGER NOT NULL,
                fingerprint TEXT NOT NULL DEFAULT '',
                manifest_json TEXT NOT NULL,
                claim_paths_json TEXT NOT NULL,
                state TEXT NOT NULL,
                owner_token TEXT NOT NULL,
                history_id INTEGER NULL,
                created_utc_ticks INTEGER NOT NULL,
                updated_utc_ticks INTEGER NOT NULL,
                detail TEXT NULL,
                cleanup_complete INTEGER NOT NULL DEFAULT 0,
                undo_of TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_media_move_operations_state ON media_move_operations(state, cleanup_complete);
            CREATE INDEX IF NOT EXISTS idx_media_move_operations_issue ON media_move_operations(issue_id, fingerprint);

            CREATE TABLE IF NOT EXISTS protection_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL,
                target TEXT NOT NULL,
                scanners_mask TEXT NOT NULL DEFAULT '',
                fixers_mask TEXT NOT NULL DEFAULT '',
                note TEXT NOT NULL DEFAULT '',
                created_at_utc INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // Each step below is idempotent by design (DELETE on empty table is a no-op; column probes
    // precede any ALTER TABLE; back-fills use conditional WHERE). PRAGMA user_version is only
    // advanced after every step succeeds, so a crash mid-migration re-runs cleanly on next boot.
    // Do not restructure this method into an all-or-nothing transaction without preserving that
    // invariant, or upgrades from pre-v1 installs will start failing on transient IO errors.
    public static void Migrate(SqliteConnection connection)
    {
        using var getVersion = connection.CreateCommand();
        getVersion.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(getVersion.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        if (current >= Version)
        {
            return;
        }

        if (current < 1)
        {
            using var clearDecode = connection.CreateCommand();
            clearDecode.CommandText = "DELETE FROM decode_cache";
            clearDecode.ExecuteNonQuery();
        }

        if (current < 2)
        {
            // Add the success column on an existing v1 database. A fresh install already gets the column
            // via CREATE TABLE above; guard the ALTER so it doesn't blow up with "duplicate column name".
            if (!HasColumn(connection, "history", "success", StringComparison.Ordinal))
            {
                using var addColumn = connection.CreateCommand();
                addColumn.CommandText = "ALTER TABLE history ADD COLUMN success INTEGER NOT NULL DEFAULT 1";
                addColumn.ExecuteNonQuery();

                // Back-fill legacy rows by parsing the action string. FixTask's catch handlers historically
                // prefixed failure messages with "Fix failed"; other fixer-returned failures don't share the
                // prefix, but they're rare and any mistake here only mis-labels a History filter chip.
                using var backfill = connection.CreateCommand();
                backfill.CommandText = "UPDATE history SET success = 0 WHERE action LIKE 'Fix failed%'";
                backfill.ExecuteNonQuery();
            }
        }

        if (current < 3)
        {
            if (!HasColumn(connection, "history", "acknowledged", StringComparison.Ordinal))
            {
                using var addColumn = connection.CreateCommand();
                addColumn.CommandText = "ALTER TABLE history ADD COLUMN acknowledged INTEGER NOT NULL DEFAULT 0";
                addColumn.ExecuteNonQuery();
            }
        }

        if (current < 4)
        {
            // Add issues.confidence for the duplicate confidence ladder. Nullable so pre-migration
            // rows (all non-Duplicate types + already-detected Duplicate rows) still queue normally
            // — the auto-queue gate treats NULL as "not confidence-gated" (see QueueDetectedIssues).
            if (!HasColumn(connection, "issues", "confidence", StringComparison.OrdinalIgnoreCase))
            {
                using var addColumn = connection.CreateCommand();
                addColumn.CommandText = "ALTER TABLE issues ADD COLUMN confidence REAL NULL";
                addColumn.ExecuteNonQuery();
            }
        }

        if (current < 5)
        {
            // restored_paths tracks every (path, type) tuple a user has restored from the recycle
            // bin. FixTask's auto-queue step filters against this table so a restored file never
            // gets auto-fixed again — the user explicitly reversed the fix; treat that as a strong
            // "don't touch this" signal. Manual approval from the Issues tab still works.
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS restored_paths (
                    path TEXT NOT NULL,
                    type INTEGER NOT NULL,
                    restored_at_utc INTEGER NOT NULL,
                    PRIMARY KEY (path, type)
                )
                """;
            create.ExecuteNonQuery();
        }

        if (current < 6)
        {
            // HasTruncationMarker used to treat "Truncating packet" ffmpeg warnings as truncation
            // (v1.0.7.2 and earlier). Cached decode-error strings from those runs poisoned every
            // subsequent scan into re-flagging the same MP4/M2TS/AVI file — path+size+mtime unchanged
            // so the cache hit returned the stale error. Wipe the cache once so v1.0.7.3+ scanners
            // get a fresh probe on every file. Also drop already-Queued Playability rows so users
            // aren't stuck approving fake reports on their next fix run.
            using var clearDecode = connection.CreateCommand();
            clearDecode.CommandText = "DELETE FROM decode_cache";
            clearDecode.ExecuteNonQuery();
            using var clearQueued = connection.CreateCommand();
#pragma warning disable CA2100 // (int)enum is a compile-time constant, not user input.
            clearQueued.CommandText = "DELETE FROM issues WHERE type = " + (int)IssueType.Playability;
#pragma warning restore CA2100
            clearQueued.ExecuteNonQuery();
        }

        if (current < 7)
        {
            // restore_operations owns the owner-tokened claim previously encoded (unsafely) as
            // `history.restored = 1`. Fresh install already got the table via CreateTables above;
            // pre-v7 installs get it here. IF NOT EXISTS keeps the ALTER-style migration idempotent.
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS restore_operations (
                    operation_id TEXT PRIMARY KEY,
                    entry_key TEXT NOT NULL UNIQUE,
                    history_id INTEGER NULL,
                    source_path TEXT NOT NULL,
                    destination_path TEXT NOT NULL,
                    state TEXT NOT NULL,
                    started_utc_ticks INTEGER NOT NULL,
                    detail TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_restore_operations_state ON restore_operations(state);
                """;
            create.ExecuteNonQuery();
        }

        if (current < 8)
        {
            // protection_rules powers the "Never modify" folder/library gate. Fresh install already
            // got the table via CreateTables above; pre-v8 installs get it here. Empty on upgrade.
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS protection_rules (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind TEXT NOT NULL,
                    target TEXT NOT NULL,
                    scanners_mask TEXT NOT NULL DEFAULT '',
                    fixers_mask TEXT NOT NULL DEFAULT '',
                    note TEXT NOT NULL DEFAULT '',
                    created_at_utc INTEGER NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        if (current < 9)
        {
            // saved_bytes persists FixResult.SavedBytes on history rows so the Playability
            // repair Overview panel can sum "bytes rescued from deletion" per rung.
            // FixResult + HistoryEntry already carried the SavedBytes property; only the
            // DB persistence was missing. Historical rows keep 0 by default.
            if (!HasColumn(connection, "history", "saved_bytes", StringComparison.OrdinalIgnoreCase))
            {
                using var addColumn = connection.CreateCommand();
                addColumn.CommandText = "ALTER TABLE history ADD COLUMN saved_bytes INTEGER NOT NULL DEFAULT 0";
                addColumn.ExecuteNonQuery();
            }
        }

        if (current < 10)
        {
            // MEDIA-02 / issue #76: audio files with embedded JPEG cover art were falsely
            // flagged as "truncated at 0s" because the decode check ran against the attached
            // picture stream. The classifier is fixed from this version onwards, but cached
            // decode-error strings on unchanged files would otherwise keep re-reporting the
            // old false positive (cache key is path+size+mtime, which didn't change). Purge
            // once so corrected detection runs on every file. Also drop already-Queued
            // Playability rows so a false positive the user had approved doesn't execute.
            using var clearDecode = connection.CreateCommand();
            clearDecode.CommandText = "DELETE FROM decode_cache";
            clearDecode.ExecuteNonQuery();
            using var clearQueued = connection.CreateCommand();
#pragma warning disable CA2100 // (int)enum is a compile-time constant, not user input.
            clearQueued.CommandText = "DELETE FROM issues WHERE type = " + (int)IssueType.Playability + " AND status = " + (int)IssueStatus.Queued;
#pragma warning restore CA2100
            clearQueued.ExecuteNonQuery();
        }

        if (current < 11)
        {
            // Issue #80: covering index for HistoryStore.GetLatestAttemptsByIssueIds, which runs
            // a correlated subquery (WHERE h2.issue_id = h1.issue_id AND dry_run = 0) that was
            // table-scanning on every /MediaDash/Issues call. Non-destructive: SQLite builds the
            // index from existing rows in place. Fresh installs got the index via CreateTables.
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE INDEX IF NOT EXISTS idx_history_issue_id_dry_run ON history(issue_id, dry_run, fixed_at_utc)";
            create.ExecuteNonQuery();
        }

        if (current < 12 && !HasColumn(connection, "history", "move_operation_id", StringComparison.OrdinalIgnoreCase))
        {
            using var addMoveLink = connection.CreateCommand();
            addMoveLink.CommandText = "ALTER TABLE history ADD COLUMN move_operation_id TEXT NULL";
            addMoveLink.ExecuteNonQuery();
        }

        if (current < 13 && !HasColumn(connection, "restore_operations", "native_claims_json", StringComparison.OrdinalIgnoreCase))
        {
            using var addClaims = connection.CreateCommand();
            addClaims.CommandText = "ALTER TABLE restore_operations ADD COLUMN native_claims_json TEXT NULL";
            addClaims.ExecuteNonQuery();
        }

        if (current < 14)
        {
            using var indexes = connection.CreateCommand();
            indexes.CommandText = "CREATE INDEX IF NOT EXISTS idx_issues_type_path ON issues(type, path); "
                + "CREATE INDEX IF NOT EXISTS idx_history_type_path ON history(type, path)";
            indexes.ExecuteNonQuery();
        }

        using var setVersion = connection.CreateCommand();
#pragma warning disable CA2100 // SchemaVersion is a compile-time constant, and PRAGMA does not accept bound parameters.
        setVersion.CommandText = $"PRAGMA user_version = {Version}";
#pragma warning restore CA2100
        setVersion.ExecuteNonQuery();
    }

    // Column-comparison choice is passed by the caller because the pre-refactor v2/v3 migrations
    // used Ordinal while v4 used OrdinalIgnoreCase. In practice this is a distinction without a
    // difference (SQLite reports the declared column name), but 05-A is a mechanical move — keep
    // each step's exact comparison so a hand-modified DB behaves identically to the pre-refactor
    // build.
    private static bool HasColumn(SqliteConnection connection, string table, string column, StringComparison comparison)
    {
        using var probe = connection.CreateCommand();
#pragma warning disable CA2100 // table name is a compile-time literal from the caller; PRAGMA takes no parameters
        probe.CommandText = "PRAGMA table_info(" + table + ")";
#pragma warning restore CA2100
        using var reader = probe.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, comparison))
            {
                return true;
            }
        }

        return false;
    }
}

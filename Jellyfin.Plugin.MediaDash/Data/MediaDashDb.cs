using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// SQLite persistence for scan results, the fix queue, history and the probe cache.
/// </summary>
public sealed class MediaDashDb
{
    // Bump when a semantic change to the decode check would make old cache entries misleading.
    // v1: -xerror + exit-code-only in the decode check (2026-07-20) — previous stderr-noise-as-error entries invalidated.
    // v3: history.acknowledged column for redownload-warning acknowledgement (2026-08-17).
    private const int SchemaVersion = 3;

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDashDb"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    public MediaDashDb(IApplicationPaths applicationPaths)
        : this(EnsureDbPath(applicationPaths))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDashDb"/> class against an explicit database file (tests).
    /// </summary>
    /// <param name="dbPath">Full path of the SQLite database file.</param>
    internal MediaDashDb(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        using var connection = Open();
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
                acknowledged INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();

        MigrateSchema(connection);
    }

    // Each step below is idempotent by design (DELETE on empty table is a no-op; column probes precede
    // any ALTER TABLE; back-fills use conditional WHERE). PRAGMA user_version is only advanced after
    // every step succeeds, so a crash mid-migration re-runs cleanly on next boot. Do not restructure
    // this method into an all-or-nothing transaction without preserving that invariant, or
    // upgrades from pre-v1 installs will start failing on transient IO errors.
    private static void MigrateSchema(SqliteConnection connection)
    {
        using var getVersion = connection.CreateCommand();
        getVersion.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(getVersion.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        if (current >= SchemaVersion)
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
            var hasSuccess = false;
            using (var probe = connection.CreateCommand())
            {
                probe.CommandText = "PRAGMA table_info(history)";
                using var pr = probe.ExecuteReader();
                while (pr.Read())
                {
                    if (string.Equals(pr.GetString(1), "success", StringComparison.Ordinal))
                    {
                        hasSuccess = true;
                        break;
                    }
                }
            }

            if (!hasSuccess)
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
            var hasAck = false;
            using (var probe = connection.CreateCommand())
            {
                probe.CommandText = "PRAGMA table_info(history)";
                using var pr = probe.ExecuteReader();
                while (pr.Read())
                {
                    if (string.Equals(pr.GetString(1), "acknowledged", StringComparison.Ordinal))
                    {
                        hasAck = true;
                        break;
                    }
                }
            }

            if (!hasAck)
            {
                using var addColumn = connection.CreateCommand();
                addColumn.CommandText = "ALTER TABLE history ADD COLUMN acknowledged INTEGER NOT NULL DEFAULT 0";
                addColumn.ExecuteNonQuery();
            }
        }

        using var setVersion = connection.CreateCommand();
#pragma warning disable CA2100 // SchemaVersion is a compile-time constant, and PRAGMA does not accept bound parameters.
        setVersion.CommandText = $"PRAGMA user_version = {SchemaVersion}";
#pragma warning restore CA2100
        setVersion.ExecuteNonQuery();
    }

    private static string EnsureDbPath(IApplicationPaths applicationPaths)
    {
        var dataDir = Path.Combine(applicationPaths.DataPath, "mediadash");
        Directory.CreateDirectory(dataDir);

        // Mark the dir as cache-like so backup tools (rsync --exclude-caches, restic, borg, Time Machine
        // via .nobackup patterns) skip our SQLite + probe cache + recycle bin by default. Spec:
        // https://bford.info/cachedir/ — first line must be exactly the Signature: line with no BOM.
        var tagPath = Path.Combine(dataDir, "CACHEDIR.TAG");
        if (!File.Exists(tagPath))
        {
            File.WriteAllText(
                tagPath,
                "Signature: 8a477f597d28d172789f06886806bc55\n# This file is a cache directory tag created by MediaDash.\n# For information about cache directory tags see https://bford.info/cachedir/\n",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return Path.Combine(dataDir, "mediadash.db");
    }

    /// <summary>
    /// Replaces detected issues of a type with fresh scan results, but only for the paths that were actually scanned —
    /// a scan scoped to one library must not wipe findings from other libraries.
    /// Issues the user dismissed are preserved and not re-inserted for the same path.
    /// </summary>
    /// <param name="type">The issue type being refreshed.</param>
    /// <param name="issues">The freshly detected issues.</param>
    /// <param name="scannedPaths">All file paths covered by this scan; null means the scan covered everything.</param>
    public void ReplaceDetectedIssues(IssueType type, IReadOnlyList<Issue> issues, IReadOnlyCollection<string>? scannedPaths = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        if (scannedPaths is null)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM issues WHERE type = @type AND status = @detected";
            delete.Parameters.AddWithValue("@type", (int)type);
            delete.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            delete.ExecuteNonQuery();
        }
        else
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM issues WHERE type = @type AND status = @detected AND path = @path";
            delete.Parameters.AddWithValue("@type", (int)type);
            delete.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            var pPath = delete.Parameters.Add("@path", SqliteType.Text);
            foreach (var path in scannedPaths)
            {
                pPath.Value = path;
                delete.ExecuteNonQuery();
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            // Dismissed rows suppress re-detection (the user said "never show this again");
            // queued rows suppress duplicates. Fixed rows must NOT suppress — the same path can break again later.
            insert.CommandText = """
                INSERT INTO issues (type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc)
                SELECT @type, @itemId, @path, @details, @suggestedFix, @sizeSavings, @detected, @detectedAt
                WHERE NOT EXISTS (
                    SELECT 1 FROM issues WHERE type = @type AND path = @path AND status IN (@queued, @dismissed))
                """;
            insert.Parameters.AddWithValue("@queued", (int)IssueStatus.Queued);
            insert.Parameters.AddWithValue("@dismissed", (int)IssueStatus.Dismissed);
            var pType = insert.Parameters.Add("@type", SqliteType.Integer);
            var pItemId = insert.Parameters.Add("@itemId", SqliteType.Text);
            var pPath = insert.Parameters.Add("@path", SqliteType.Text);
            var pDetails = insert.Parameters.Add("@details", SqliteType.Text);
            var pSuggestedFix = insert.Parameters.Add("@suggestedFix", SqliteType.Text);
            var pSizeSavings = insert.Parameters.Add("@sizeSavings", SqliteType.Integer);
            insert.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            var pDetectedAt = insert.Parameters.Add("@detectedAt", SqliteType.Integer);

            foreach (var issue in issues)
            {
                pType.Value = (int)type;
                pItemId.Value = issue.ItemId.ToString("N");
                pPath.Value = issue.Path;
                pDetails.Value = issue.DetailsJson;
                pSuggestedFix.Value = issue.SuggestedFix;
                pSizeSavings.Value = issue.SizeSavings;
                pDetectedAt.Value = issue.DetectedAtUtc.Ticks;
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Gets issues, optionally filtered by type and status.
    /// </summary>
    /// <param name="type">Filter by issue type, or null for all types.</param>
    /// <param name="status">Filter by status, or null for all statuses.</param>
    /// <returns>The matching issues, newest first.</returns>
    public IReadOnlyList<Issue> GetIssues(IssueType? type = null, IssueStatus? status = null)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc FROM issues"
            + " WHERE (@type IS NULL OR type = @type) AND (@status IS NULL OR status = @status) ORDER BY id DESC";
        cmd.Parameters.AddWithValue("@type", type is null ? DBNull.Value : (int)type);
        cmd.Parameters.AddWithValue("@status", status is null ? DBNull.Value : (int)status);

        var result = new List<Issue>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Issue
            {
                Id = reader.GetInt64(0),
                Type = (IssueType)reader.GetInt32(1),
                ItemId = Guid.ParseExact(reader.GetString(2), "N"),
                Path = reader.GetString(3),
                DetailsJson = reader.GetString(4),
                SuggestedFix = reader.GetString(5),
                SizeSavings = reader.GetInt64(6),
                Status = (IssueStatus)reader.GetInt32(7),
                DetectedAtUtc = new DateTime(reader.GetInt64(8), DateTimeKind.Utc)
            });
        }

        return result;
    }

    /// <summary>
    /// Gets per-type counts and potential savings for open issues (Detected + Queued), plus the newest detection time.
    /// Queued items are already approved and waiting for the fix task, so their savings still count toward "space you could reclaim".
    /// </summary>
    /// <returns>One summary row per issue type that has open issues.</returns>
    public IReadOnlyList<IssueSummary> GetSummary()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type, COUNT(*), SUM(size_savings), MAX(detected_at_utc) FROM issues WHERE status IN (@detected, @queued) GROUP BY type";
        cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
        cmd.Parameters.AddWithValue("@queued", (int)IssueStatus.Queued);

        var result = new List<IssueSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new IssueSummary
            {
                Type = (IssueType)reader.GetInt32(0),
                Count = reader.GetInt32(1),
                PotentialSavings = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                NewestDetectedUtc = new DateTime(reader.GetInt64(3), DateTimeKind.Utc)
            });
        }

        return result;
    }

    /// <summary>
    /// Looks up a cached ffprobe result that is still valid for the file's current size and modification time.
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>The cached ffprobe JSON, or null when absent or stale.</returns>
    public string? GetCachedProbe(string path, long size, long mtimeUtcTicks)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT json FROM probe_cache WHERE path = @path AND size = @size AND mtime_utc = @mtime";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Stores an ffprobe result in the cache, replacing any previous entry for the path.
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size in bytes at probe time.</param>
    /// <param name="mtimeUtcTicks">Last-write time in UTC ticks at probe time.</param>
    /// <param name="json">Raw ffprobe JSON output.</param>
    public void StoreProbe(string path, long size, long mtimeUtcTicks, string json)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO probe_cache (path, size, mtime_utc, probed_at_utc, json)
            VALUES (@path, @size, @mtime, @probedAt, @json)
            ON CONFLICT(path) DO UPDATE SET size = @size, mtime_utc = @mtime, probed_at_utc = @probedAt, json = @json
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        cmd.Parameters.AddWithValue("@probedAt", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("@json", json);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Looks up a cached format-probe result that is still valid for the file's current size and mtime.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>The cached result, or null when absent or stale.</returns>
    public FormatProbeResult? GetCachedFormatProbe(string path, long size, long mtimeUtcTicks)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ok, reason FROM format_probe_cache WHERE path = @path AND size = @size AND mtime_utc = @mtime";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var ok = reader.GetInt32(0) != 0;
        var reason = reader.IsDBNull(1) ? null : reader.GetString(1);
        return new FormatProbeResult(ok, reason);
    }

    /// <summary>Stores a format-probe result, replacing any previous entry for the path.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size in bytes at probe time.</param>
    /// <param name="mtimeUtcTicks">Last-write time in UTC ticks at probe time.</param>
    /// <param name="ok">True when the file's container parsed cleanly.</param>
    /// <param name="reason">Human-readable failure reason when <paramref name="ok"/> is false, else null.</param>
    public void StoreFormatProbe(string path, long size, long mtimeUtcTicks, bool ok, string? reason)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO format_probe_cache (path, size, mtime_utc, probed_at_utc, ok, reason)
            VALUES (@path, @size, @mtime, @probedAt, @ok, @reason)
            ON CONFLICT(path) DO UPDATE SET size = @size, mtime_utc = @mtime, probed_at_utc = @probedAt, ok = @ok, reason = @reason
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        cmd.Parameters.AddWithValue("@probedAt", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("@ok", ok ? 1 : 0);
        cmd.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Looks up a cached decode-check result that is still valid for the file's current size and modification time.
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">Current file size in bytes.</param>
    /// <param name="mtimeUtcTicks">Current last-write time in UTC ticks.</param>
    /// <returns>Empty string when the file decoded cleanly, the error text when it did not, or null when not cached.</returns>
    public string? GetCachedDecode(string path, long size, long mtimeUtcTicks)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT error FROM decode_cache WHERE path = @path AND size = @size AND mtime_utc = @mtime";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Stores a decode-check result, replacing any previous entry for the path.
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="size">File size in bytes at check time.</param>
    /// <param name="mtimeUtcTicks">Last-write time in UTC ticks at check time.</param>
    /// <param name="error">Empty string for a clean decode, otherwise the error text.</param>
    public void StoreDecode(string path, long size, long mtimeUtcTicks, string error)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO decode_cache (path, size, mtime_utc, checked_at_utc, error)
            VALUES (@path, @size, @mtime, @checkedAt, @error)
            ON CONFLICT(path) DO UPDATE SET size = @size, mtime_utc = @mtime, checked_at_utc = @checkedAt, error = @error
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtimeUtcTicks);
        cmd.Parameters.AddWithValue("@checkedAt", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates the status of a single issue.
    /// </summary>
    /// <param name="issueId">The issue id.</param>
    /// <param name="status">The new status.</param>
    /// <returns>True when a row was updated.</returns>
    public bool UpdateIssueStatus(long issueId, IssueStatus status)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE issues SET status = @status WHERE id = @id";
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@id", issueId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Reads a single issue's current status. Used by the fix loop to re-verify that a queued
    /// item hasn't been dismissed since the run's initial snapshot — otherwise a user pressing
    /// "Ignore" during a long fix run has no effect on the file that's about to be touched.
    /// </summary>
    /// <param name="issueId">The issue id.</param>
    /// <returns>The current status, or null if the row is gone.</returns>
    public IssueStatus? GetIssueStatus(long issueId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM issues WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", issueId);
        var raw = cmd.ExecuteScalar();
        return raw is null || raw is DBNull ? null : (IssueStatus)Convert.ToInt32(raw, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Guarded bulk status update. Only transitions rows currently in Detected or Queued to the
    /// target status — Dismissed and Fixed rows are left alone. Prevents a stale client snapshot
    /// (from before the last poll) from un-ignoring items via bulk-approve, or un-fixing them.
    /// </summary>
    /// <param name="ids">Issue ids to update.</param>
    /// <param name="target">Target status.</param>
    /// <returns>Number of rows actually updated.</returns>
    public int BulkUpdateOpenIssueStatus(IReadOnlyList<long> ids, IssueStatus target)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        // SQLite's default parameter limit is 999; a fresh install with 5000+ duplicates otherwise
        // throws SqliteException here. Chunk into 500-id batches — each batch runs its own UPDATE.
        const int chunkSize = 500;
        using var connection = Open();
        var total = 0;
        for (var offset = 0; offset < ids.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, ids.Count - offset);
            using var cmd = connection.CreateCommand();
            var placeholders = string.Join(",", Enumerable.Range(0, count).Select(i => "@id" + i.ToString(CultureInfo.InvariantCulture)));
#pragma warning disable CA2100, CA3001 // placeholder list is machine-composed from an int range, ids are bound as parameters — no user text reaches the SQL string
            cmd.CommandText = "UPDATE issues SET status = @target WHERE id IN (" + placeholders + ") AND status IN (@detected, @queued)";
#pragma warning restore CA2100, CA3001
            cmd.Parameters.AddWithValue("@target", (int)target);
            cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            cmd.Parameters.AddWithValue("@queued", (int)IssueStatus.Queued);
            for (var i = 0; i < count; i++)
            {
                cmd.Parameters.AddWithValue("@id" + i.ToString(CultureInfo.InvariantCulture), ids[offset + i]);
            }

            total += cmd.ExecuteNonQuery();
        }

        return total;
    }

    /// <summary>
    /// Re-points every issue whose stored path is exactly <paramref name="oldPath"/>, or lives under
    /// <paramref name="oldPath"/> as a directory prefix, to the equivalent location under
    /// <paramref name="newPath"/>. Call this after a move-style fixer completes so other queued issues
    /// on the same file/folder don't fail with "no longer exists" the next time they run.
    /// </summary>
    /// <param name="oldPath">The pre-move source path (file or directory).</param>
    /// <param name="newPath">The post-move target path.</param>
    /// <returns>The number of issue rows updated.</returns>
    public int RelocateIssuePaths(string oldPath, string newPath)
    {
        if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath))
        {
            return 0;
        }

        // Callers pass paths from different sources — some already TrimEndingDirectorySeparator,
        // some don't. A trailing separator collapses the boundary check (@oldSlash duplicates the
        // separator) and the prefix match silently misses. Normalize here so the callers don't have to.
        oldPath = Path.TrimEndingDirectorySeparator(oldPath);
        newPath = Path.TrimEndingDirectorySeparator(newPath);

        if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            return 0;
        }

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        // Exact match (file move) → rewrite in place; the SUBSTR tail is empty so @new stays @new.
        // Prefix match with '/' or '\' boundary (folder move) → rewrite with the folder prefix swapped.
        // Uses SUBSTR equality rather than LIKE to avoid % / _ being treated as wildcards on paths.
        // COLLATE NOCASE on the WHERE — on Windows the stored path can differ in case from the fixer's
        // post-move path (drive letter, case-fold varying scanner sources); without this the sibling
        // issues never re-point and every subsequent fix run fails with "no longer exists".
        cmd.CommandText = @"
            UPDATE issues
               SET path = @new || SUBSTR(path, LENGTH(@old) + 1)
             WHERE path = @old COLLATE NOCASE
                OR SUBSTR(path, 1, LENGTH(@old) + 1) = @oldSlash COLLATE NOCASE
                OR SUBSTR(path, 1, LENGTH(@old) + 1) = @oldBackslash COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@old", oldPath);
        cmd.Parameters.AddWithValue("@new", newPath);
        cmd.Parameters.AddWithValue("@oldSlash", oldPath + "/");
        cmd.Parameters.AddWithValue("@oldBackslash", oldPath + "\\");
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Moves all detected issues of a type into the queue (used by automatic mode).
    /// </summary>
    /// <param name="type">The issue type.</param>
    /// <returns>The number of issues queued.</returns>
    public int QueueDetectedIssues(IssueType type)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE issues SET status = @queued WHERE type = @type AND status = @detected";
        cmd.Parameters.AddWithValue("@queued", (int)IssueStatus.Queued);
        cmd.Parameters.AddWithValue("@type", (int)type);
        cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets a single issue by id.
    /// </summary>
    /// <param name="issueId">The issue id.</param>
    /// <returns>The issue, or null.</returns>
    public Issue? GetIssue(long issueId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc FROM issues WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", issueId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new Issue
        {
            Id = reader.GetInt64(0),
            Type = (IssueType)reader.GetInt32(1),
            ItemId = Guid.ParseExact(reader.GetString(2), "N"),
            Path = reader.GetString(3),
            DetailsJson = reader.GetString(4),
            SuggestedFix = reader.GetString(5),
            SizeSavings = reader.GetInt64(6),
            Status = (IssueStatus)reader.GetInt32(7),
            DetectedAtUtc = new DateTime(reader.GetInt64(8), DateTimeKind.Utc)
        };
    }

    /// <summary>
    /// Records a fix action in the history.
    /// </summary>
    /// <param name="entry">The history entry.</param>
    public void AddHistory(HistoryEntry entry)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, restored, success)
            VALUES (@issueId, @type, @path, @action, @bytesFreed, @recyclePath, @fixedAt, @dryRun, 0, @success)
            """;
        cmd.Parameters.AddWithValue("@issueId", entry.IssueId);
        cmd.Parameters.AddWithValue("@type", (int)entry.Type);
        cmd.Parameters.AddWithValue("@path", entry.Path);
        cmd.Parameters.AddWithValue("@action", entry.Action);
        cmd.Parameters.AddWithValue("@bytesFreed", entry.BytesFreed);
        cmd.Parameters.AddWithValue("@recyclePath", (object?)entry.RecyclePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fixedAt", entry.FixedAtUtc.Ticks);
        cmd.Parameters.AddWithValue("@dryRun", entry.WasDryRun ? 1 : 0);
        cmd.Parameters.AddWithValue("@success", entry.Success ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets fix history, newest first.
    /// </summary>
    /// <param name="limit">Maximum rows returned.</param>
    /// <returns>The history entries.</returns>
    public IReadOnlyList<HistoryEntry> GetHistory(int limit = 500)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, restored, success, acknowledged"
            + " FROM history ORDER BY id DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var result = new List<HistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HistoryEntry
            {
                Id = reader.GetInt64(0),
                IssueId = reader.GetInt64(1),
                Type = (IssueType)reader.GetInt32(2),
                Path = reader.GetString(3),
                Action = reader.GetString(4),
                BytesFreed = reader.GetInt64(5),
                RecyclePath = reader.IsDBNull(6) ? null : reader.GetString(6),
                FixedAtUtc = new DateTime(reader.GetInt64(7), DateTimeKind.Utc),
                WasDryRun = reader.GetInt32(8) != 0,
                Restored = reader.GetInt32(9) != 0,
                Success = reader.GetInt32(10) != 0,
                Acknowledged = reader.GetInt32(11) != 0
            });
        }

        return result;
    }

    /// <summary>
    /// Marks a history row as acknowledged so the redownload-warning banner stops flagging it.
    /// No-op when the row does not exist.
    /// </summary>
    /// <param name="historyId">The history row id.</param>
    /// <returns>True when a row was updated.</returns>
    public bool AcknowledgeHistoryEntry(long historyId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE history SET acknowledged = 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", historyId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Gets a single history row by id.</summary>
    /// <param name="historyId">The row id.</param>
    /// <returns>The row, or null.</returns>
    public HistoryEntry? GetHistoryEntry(long historyId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, restored, success, acknowledged"
            + " FROM history WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", historyId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new HistoryEntry
        {
            Id = reader.GetInt64(0),
            IssueId = reader.GetInt64(1),
            Type = (IssueType)reader.GetInt32(2),
            Path = reader.GetString(3),
            Action = reader.GetString(4),
            BytesFreed = reader.GetInt64(5),
            RecyclePath = reader.IsDBNull(6) ? null : reader.GetString(6),
            FixedAtUtc = new DateTime(reader.GetInt64(7), DateTimeKind.Utc),
            WasDryRun = reader.GetInt32(8) != 0,
            Restored = reader.GetInt32(9) != 0,
            Success = reader.GetInt32(10) != 0,
            Acknowledged = reader.GetInt32(11) != 0
        };
    }

    /// <summary>
    /// Counts failed non-dry-run history rows since the given watermark (Config.HistoryHiddenBeforeUtcTicks).
    /// Powers the History tab's badge. 0 for a fresh install or when everything has been cleared.
    /// </summary>
    /// <param name="hiddenBeforeUtcTicks">The Clear-history watermark; 0 = count everything.</param>
    /// <returns>Row count.</returns>
    public int GetFailedHistoryCount(long hiddenBeforeUtcTicks)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM history WHERE success = 0 AND dry_run = 0 AND fixed_at_utc >= @hidden";
        cmd.Parameters.AddWithValue("@hidden", hiddenBeforeUtcTicks);
        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : 0;
    }

    /// <summary>
    /// Returns the lifetime total of bytes reclaimed by successful non-dry-run fixes. Same
    /// success=1 AND dry_run=0 filter as the monthly aggregate so the number the user sees on
    /// Overview matches what actually happened to their disk. Returns 0 for a fresh install.
    /// </summary>
    /// <returns>Total bytes freed across the entire history.</returns>
    public long GetLifetimeBytesFreed()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(bytes_freed), 0) FROM history WHERE success = 1 AND dry_run = 0";
        var raw = cmd.ExecuteScalar();
        return raw is long l ? l : 0L;
    }

    /// <summary>
    /// Per-type breakdown of the lifetime reclaim total. Powers the donut on the right half of the
    /// Overview reclaim card so the user can see which fix type has recovered the most disk.
    /// </summary>
    /// <returns>One row per type that ever produced a successful non-dry-run fix.</returns>
    public IReadOnlyList<IssueSummary> GetLifetimeSummary()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type, COUNT(*), COALESCE(SUM(bytes_freed), 0)"
            + " FROM history"
            + " WHERE success = 1 AND dry_run = 0"
            + " GROUP BY type";

        var result = new List<IssueSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new IssueSummary
            {
                Type = (IssueType)reader.GetInt32(0),
                Count = reader.GetInt32(1),
                PotentialSavings = reader.GetInt64(2),
                NewestDetectedUtc = DateTime.MinValue
            });
        }

        return result;
    }

    /// <summary>
    /// Aggregates the history for a given month into per-type success counts + total bytes freed.
    /// Filters to success=1 and dry_run=0 so dry-run rehearsals never inflate the analytics numbers.
    /// </summary>
    /// <param name="monthStartUtc">First instant of the target month (UTC).</param>
    /// <param name="monthEndUtc">First instant of the following month (UTC) — exclusive upper bound.</param>
    /// <returns>A per-type count map plus the total bytes freed.</returns>
    public MonthAggregate GetMonthAggregate(DateTime monthStartUtc, DateTime monthEndUtc)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type, COUNT(*), COALESCE(SUM(bytes_freed), 0)"
            + " FROM history"
            + " WHERE success = 1 AND dry_run = 0"
            + " AND fixed_at_utc >= @start AND fixed_at_utc < @end"
            + " GROUP BY type";
        cmd.Parameters.AddWithValue("@start", monthStartUtc.Ticks);
        cmd.Parameters.AddWithValue("@end", monthEndUtc.Ticks);

        var byType = new Dictionary<IssueType, int>();
        long totalBytes = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var type = (IssueType)reader.GetInt32(0);
            byType[type] = reader.GetInt32(1);
            totalBytes += reader.GetInt64(2);
        }

        return new MonthAggregate(byType, totalBytes);
    }

    /// <summary>
    /// Wipes all scan state — issues, probe cache, decode cache — so the next scan starts from scratch.
    /// Fix history (and the recycle bin it points into) is preserved so users can still restore recently-removed files.
    /// </summary>
    public void ResetScanState()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[] { "issues", "probe_cache", "decode_cache", "format_probe_cache" })
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
#pragma warning disable CA2100 // table name is a compile-time literal from the enumeration above.
            cmd.CommandText = "DELETE FROM " + table;
#pragma warning restore CA2100
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Marks a history entry as restored from the recycle bin.
    /// </summary>
    /// <param name="historyId">The history entry id.</param>
    public void MarkRestored(long historyId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE history SET restored = 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", historyId);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch (SqliteException ex)
        {
            // Backup tool / virus scanner / WAL checkpoint holding a Windows file lock produces a
            // hard failure with no breadcrumb otherwise. Record so admins can see it in the Errors tab
            // and correlate with the resulting API 500. Rethrow — callers cannot recover from a
            // completely-unopenable database.
            connection.Dispose();
            Api.Diagnostics.Record("MediaDashDb.Open", "Could not open SQLite database: " + ex.Message + ". Something else may be holding a lock on the file (backup software, antivirus, running scan).");
            throw;
        }
    }
}

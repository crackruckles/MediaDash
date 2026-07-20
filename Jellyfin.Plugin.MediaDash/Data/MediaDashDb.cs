using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    private const int SchemaVersion = 1;

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
                restored INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();

        MigrateSchema(connection);
    }

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
    /// Gets per-type counts and potential savings for issues awaiting a decision, plus the newest detection time.
    /// </summary>
    /// <returns>One summary row per issue type that has detected issues.</returns>
    public IReadOnlyList<IssueSummary> GetSummary()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type, COUNT(*), SUM(size_savings), MAX(detected_at_utc) FROM issues WHERE status = @detected GROUP BY type";
        cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);

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
        foreach (var issue in GetIssues())
        {
            if (issue.Id == issueId)
            {
                return issue;
            }
        }

        return null;
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
            INSERT INTO history (issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, restored)
            VALUES (@issueId, @type, @path, @action, @bytesFreed, @recyclePath, @fixedAt, @dryRun, 0)
            """;
        cmd.Parameters.AddWithValue("@issueId", entry.IssueId);
        cmd.Parameters.AddWithValue("@type", (int)entry.Type);
        cmd.Parameters.AddWithValue("@path", entry.Path);
        cmd.Parameters.AddWithValue("@action", entry.Action);
        cmd.Parameters.AddWithValue("@bytesFreed", entry.BytesFreed);
        cmd.Parameters.AddWithValue("@recyclePath", (object?)entry.RecyclePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fixedAt", entry.FixedAtUtc.Ticks);
        cmd.Parameters.AddWithValue("@dryRun", entry.WasDryRun ? 1 : 0);
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
        cmd.CommandText = "SELECT id, issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, restored"
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
                Restored = reader.GetInt32(9) != 0
            });
        }

        return result;
    }

    /// <summary>
    /// Wipes all scan state — issues, probe cache, decode cache — so the next scan starts from scratch.
    /// Fix history (and the recycle bin it points into) is preserved so users can still restore recently-removed files.
    /// </summary>
    public void ResetScanState()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[] { "issues", "probe_cache", "decode_cache" })
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
        connection.Open();
        return connection;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Owns every SQL statement that reads or writes the <c>history</c> table. Extracted from
/// <see cref="MediaDashDb"/> in 05-C.3; the facade retains one-line forwarders for the same
/// methods so pre-existing callers (tests + un-migrated call sites) keep working while consumers
/// (AnalyticsReporter, FileBrowserController, TrickplayOptimizeScanner, RestoreService, RecycleBin,
/// RedownloadDetector, MediaDashController) are pointed at the narrower type one by one.
/// </summary>
/// <remarks>
/// <c>MediaDashDb.RecordFixOutcome</c> stays on the facade — it is the ONE method that atomically
/// touches both <c>history</c> and <c>issues</c> in a single SQLite transaction, and plan §05-C
/// warns against exposing raw connections across stores. Treat <c>MediaDashDb</c> itself as the
/// deliberate cross-store transaction entry point: single-table operations live on the per-family
/// stores, cross-table transactions live on the facade.
/// </remarks>
public sealed class HistoryStore
{
    private readonly MediaDashConnectionFactory _connectionFactory;

    /// <summary>Initializes a new instance of the <see cref="HistoryStore"/> class.</summary>
    /// <param name="connectionFactory">Shared factory; every 05-C store family uses the same instance.</param>
    public HistoryStore(MediaDashConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>Records a fix action in the history.</summary>
    /// <param name="entry">The history entry.</param>
    public void AddHistory(HistoryEntry entry)
    {
        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        Insert(connection, transaction, entry);
        transaction.Commit();
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, HistoryEntry entry)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO history (issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, restored, success, saved_bytes, move_operation_id)
            VALUES (@issueId, @type, @path, @action, @bytesFreed, @recyclePath, @fixedAt, @dryRun, 0, @success, @savedBytes, @move)
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
        cmd.Parameters.AddWithValue("@savedBytes", entry.SavedBytes);
        cmd.Parameters.AddWithValue("@move", (object?)entry.MoveOperationId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        if (entry.MoveOperationId is { } operationId)
        {
            MediaMoveStore.LinkInsertedHistory(connection, transaction, operationId);
        }
    }

    /// <summary>Gets fix history, newest first.</summary>
    /// <param name="limit">Maximum rows returned.</param>
    /// <returns>The history entries.</returns>
    public IReadOnlyList<HistoryEntry> GetHistory(int limit = 500)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + HistoryRowReader.ProjectionColumns
            + " FROM history ORDER BY id DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var result = new List<HistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (HistoryRowReader.TryRead(reader, "history", out var entry))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>Gets the action text of every applied grouping move ("grouped A → B").</summary>
    /// <returns>Action texts, oldest first.</returns>
    internal IReadOnlyList<string> GetGroupedMoveActions()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT action FROM history WHERE dry_run = 0 AND type IN (5, 10) AND action LIKE 'grouped %' ORDER BY id";
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                result.Add(reader.GetString(0));
            }
        }

        return result;
    }

    /// <summary>Gets all unrestored recycle history, including entries older than the History page.</summary>
    /// <returns>History rows with an active recycle reference.</returns>
    public IReadOnlyList<HistoryEntry> GetActiveRecycleEntries()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + HistoryRowReader.ProjectionColumns
            + " FROM history WHERE recycle_path IS NOT NULL AND restored = 0 AND dry_run = 0 ORDER BY id DESC";
        var result = new List<HistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (HistoryRowReader.TryRead(reader, "history", out var entry))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    internal long? FindActiveManagedMetadataRecycleHistory(string recyclePath, string originalPath)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM history WHERE recycle_path = @recycle AND path = @original
                AND type IN (@artwork, @orphan) AND success = 1 AND restored = 0 AND dry_run = 0
            ORDER BY id DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@recycle", recyclePath);
        cmd.Parameters.AddWithValue("@original", originalPath);
        cmd.Parameters.AddWithValue("@artwork", (int)IssueType.CorruptArtwork);
        cmd.Parameters.AddWithValue("@orphan", (int)IssueType.OrphanedDebris);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>
    /// Gets every distinct recycle path recorded by non-dry-run history. These paths are authoritative
    /// evidence for adopting pre-marker batches after an upgrade.
    /// </summary>
    /// <returns>Recorded recycle paths.</returns>
    public IReadOnlyList<string> GetRecyclePaths()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT recycle_path FROM history"
            + " WHERE recycle_path IS NOT NULL AND recycle_path <> '' AND dry_run = 0";

        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
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
        using var connection = _connectionFactory.Open();
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
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + HistoryRowReader.ProjectionColumns
            + " FROM history WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", historyId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return HistoryRowReader.TryRead(reader, "history", out var entry) ? entry : null;
    }

    /// <summary>
    /// Returns the most recent successful, non-dry-run <c>fixed_at_utc</c> for a given issue type and
    /// path, or null when there's no such row. Used by <see cref="Scanners.TrickplayOptimizeScanner"/>
    /// as a rescan cutoff so folders where the fixer intentionally left small "won't-shrink" sprites
    /// don't get re-flagged forever.
    /// </summary>
    /// <param name="type">The issue type.</param>
    /// <param name="path">The exact path recorded on the history row (trickplay folder for LargeTrickplay).</param>
    /// <returns>UTC timestamp of the newest matching row, or null when none.</returns>
    public DateTime? GetLastSuccessfulFixUtc(IssueType type, string path)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(fixed_at_utc) FROM history WHERE type = @type AND path = @path AND success = 1 AND dry_run = 0";
        cmd.Parameters.AddWithValue("@type", (int)type);
        cmd.Parameters.AddWithValue("@path", path);
        var raw = cmd.ExecuteScalar();
        // R05 sibling (audit 2026-09-19): the 1.0.7.8 SafeUtcFromTicks sweep missed this site.
        // MAX() over a malformed fixed_at_utc row returns the corrupt value; the pre-fix raw
        // ctor threw ArgumentOutOfRangeException and killed TrickplayOptimizeScanner's rescan
        // cutoff for the whole run.
        return raw is long ticks and > 0 ? RowHydration.SafeUtcFromTicks(ticks, "history (GetLastSuccessfulFixUtc MAX)") : null;
    }

    internal Dictionary<string, long> GetTranscodeLogCutoffs(DateTime cutoff, StringComparer pathComparer)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT path, MAX(fixed_at_utc) FROM history
            WHERE type IN (@quality, @heavy, @failed)
                AND success = 1 AND dry_run = 0 AND restored = 0
                AND fixed_at_utc >= @cutoff AND fixed_at_utc <= @now
            GROUP BY path
            """;
        cmd.Parameters.AddWithValue("@quality", (int)IssueType.Quality);
        cmd.Parameters.AddWithValue("@heavy", (int)IssueType.HeavyTranscode);
        cmd.Parameters.AddWithValue("@failed", (int)IssueType.FailedTranscode);
        cmd.Parameters.AddWithValue("@cutoff", cutoff.Ticks);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
        var result = new Dictionary<string, long>(pathComparer);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.GetValue(1) is not long ticks)
            {
                continue;
            }

            var path = reader.GetString(0);
            result[path] = Math.Max(ticks, result.GetValueOrDefault(path));
        }

        return result;
    }

    /// <summary>
    /// Batch lookup: for each issue id, returns the most recent non-dry-run history row
    /// (successful or failed). Used by the Issues tab to render "last attempt: X, Y ago" on
    /// rows that keep failing silently. Chunks the IN-list so SQLite's 999-parameter cap can't
    /// throw on a "show 500 issues at once" page.
    /// </summary>
    /// <param name="issueIds">Issue ids to look up (deduplicated inside; empty is a no-op).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Dictionary keyed by issue id; ids without any history row are absent.</returns>
    public IReadOnlyDictionary<long, HistoryEntry> GetLatestAttemptsByIssueIds(IReadOnlyList<long> issueIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new Dictionary<long, HistoryEntry>();
        if (issueIds is null || issueIds.Count == 0)
        {
            return result;
        }

        var distinct = issueIds.Where(id => id > 0).Distinct().ToList();
        if (distinct.Count == 0)
        {
            return result;
        }

        const int chunkSize = 500;
        using var connection = _connectionFactory.Open();
        for (var offset = 0; offset < distinct.Count; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(chunkSize, distinct.Count - offset);
            using var cmd = connection.CreateCommand();
            var requestedValues = string.Join(",", Enumerable.Range(0, count).Select(i => "(@id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"));
#pragma warning disable CA2100, CA3001 // placeholder list is machine-composed from an int range
            // One indexed seek per requested issue, rather than revisiting every prior retry.
            cmd.CommandText = "WITH requested(issue_id) AS (VALUES " + requestedValues + ")"
                + " SELECT " + HistoryRowReader.ProjectionColumns + " FROM history WHERE id IN"
                + " (SELECT (SELECT h.id FROM history h WHERE h.issue_id = requested.issue_id AND h.dry_run = 0"
                + " ORDER BY h.fixed_at_utc DESC, h.id DESC LIMIT 1) FROM requested)";
#pragma warning restore CA2100, CA3001
            for (var i = 0; i < count; i++)
            {
                cmd.Parameters.AddWithValue("@id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), distinct[offset + i]);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HistoryRowReader.TryRead(reader, "history", out var entry) && !result.ContainsKey(entry.IssueId))
                {
                    result[entry.IssueId] = entry;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Counts failed non-dry-run history rows since the given watermark (Config.HistoryHiddenBeforeUtcTicks).
    /// Powers the History tab's badge. 0 for a fresh install or when everything has been cleared.
    /// </summary>
    /// <param name="hiddenBeforeUtcTicks">The Clear-history watermark; 0 = count everything.</param>
    /// <returns>Row count.</returns>
    public int GetFailedHistoryCount(long hiddenBeforeUtcTicks)
    {
        using var connection = _connectionFactory.Open();
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
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(bytes_freed), 0) FROM history WHERE success = 1 AND dry_run = 0";
        var raw = cmd.ExecuteScalar();
        return raw is long l ? l : 0L;
    }

    /// <summary>
    /// Returns every successful non-dry-run history row as (path, bytes_freed). Used by the History
    /// tab's per-library chart so the totals reflect ALL history — not just the newest 500 rows the
    /// paginated <see cref="GetHistory(int)"/> returns.
    /// </summary>
    /// <returns>One row per successful non-dry-run fix.</returns>
    public IReadOnlyList<(string Path, long BytesFreed)> GetLifetimePathTotals()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT path, bytes_freed FROM history WHERE success = 1 AND dry_run = 0 AND bytes_freed > 0";
        var result = new List<(string, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return result;
    }

    /// <summary>
    /// Groups successful, non-dry-run FormatCompatibility history rows by the <c>converter</c>
    /// field in the originating issue's DetailsJson, for the given month window. Returns both
    /// fix counts and bytes reclaimed per converter. Rows without a parsable converter key are
    /// bucketed as <c>"unknown"</c>.
    /// Called by <see cref="Analytics.AnalyticsReporter"/>; internal so tests can call it directly.
    /// </summary>
    /// <param name="monthStartUtc">Inclusive lower bound (UTC).</param>
    /// <param name="monthEndUtc">Exclusive upper bound (UTC).</param>
    /// <returns>Per-converter fixed counts and net bytes reclaimed.</returns>
    public FormatCompatConverterAggregate GroupFormatCompatibilityHistoryByConverter(DateTime monthStartUtc, DateTime monthEndUtc)
    {
        // JOIN history → issues so we can read the converter key from DetailsJson.
        // The issue row may have been deleted after the fix ran (e.g. WipeDetected), in which case
        // the LEFT JOIN gives a NULL details column — those rows fall into the "unknown" bucket.
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT h.bytes_freed, i.details
            FROM history h
            LEFT JOIN issues i ON i.id = h.issue_id
            WHERE h.type = @type
              AND h.success = 1
              AND h.dry_run = 0
              AND h.fixed_at_utc >= @start
              AND h.fixed_at_utc < @end
            """;
        cmd.Parameters.AddWithValue("@type", (int)IssueType.FormatCompatibility);
        cmd.Parameters.AddWithValue("@start", monthStartUtc.Ticks);
        cmd.Parameters.AddWithValue("@end", monthEndUtc.Ticks);

        var fixedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var bytesReclaimed = new Dictionary<string, long>(StringComparer.Ordinal);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var bytes = reader.GetInt64(0);
            var details = reader.IsDBNull(1) ? null : reader.GetString(1);
            var key = IssueStore.ExtractConverterKey(details);

            fixedCounts.TryGetValue(key, out var n);
            fixedCounts[key] = n + 1;

            bytesReclaimed.TryGetValue(key, out var b);
            bytesReclaimed[key] = b + bytes;
        }

        return new FormatCompatConverterAggregate(fixedCounts, bytesReclaimed);
    }

    /// <summary>Per-type breakdown of the lifetime reclaim total.</summary>
    /// <returns>One row per type that ever produced a successful non-dry-run fix.</returns>
    public IReadOnlyList<IssueSummary> GetLifetimeSummary()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type, COUNT(*), COALESCE(SUM(bytes_freed), 0)"
            + " FROM history"
            + " WHERE success = 1 AND dry_run = 0"
            + " GROUP BY type";

        var result = new List<IssueSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!RowHydration.TryHydrateIssueType(reader.GetInt32(0), rowId: 0, "history (lifetime aggregate)", out var lifetimeType))
            {
                continue;
            }

            result.Add(new IssueSummary
            {
                Type = lifetimeType,
                Count = reader.GetInt32(1),
                PotentialSavings = reader.GetInt64(2),
                NewestDetectedUtc = DateTime.MinValue
            });
        }

        return result;
    }

    /// <summary>Aggregates the history for a given month into per-type success counts + total bytes freed.</summary>
    /// <param name="monthStartUtc">First instant of the target month (UTC).</param>
    /// <param name="monthEndUtc">First instant of the following month (UTC) — exclusive upper bound.</param>
    /// <returns>A per-type count map plus the total bytes freed.</returns>
    public MonthAggregate GetMonthAggregate(DateTime monthStartUtc, DateTime monthEndUtc)
    {
        using var connection = _connectionFactory.Open();
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
            if (!RowHydration.TryHydrateIssueType(reader.GetInt32(0), rowId: 0, "history (month aggregate)", out var monthType))
            {
                continue;
            }

            byType[monthType] = reader.GetInt32(1);
            totalBytes += reader.GetInt64(2);
        }

        return new MonthAggregate(byType, totalBytes);
    }
}

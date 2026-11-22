using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Fixers;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Owns every SQL statement that reads or writes the <c>issues</c> table. Extracted from
/// <see cref="MediaDashDb"/> in 05-C.2; the facade retains one-line forwarders for the same
/// methods so pre-existing callers (tests + un-migrated call sites) keep working while consumers
/// (fixers, scheduled tasks, controller endpoints) are pointed at the narrower type one by one.
/// </summary>
/// <remarks>
/// The store opens every connection through the shared <see cref="MediaDashConnectionFactory"/>
/// (05-C.1) so PRAGMAs / busy-timeout / diagnostic strings stay identical to the pre-05-C paths.
/// <c>RecordFixOutcome</c> stays on <see cref="MediaDashDb"/> because it is the sole cross-store
/// transaction — the plan (§05-C) calls that out explicitly as a "deliberate transaction entry
/// point" and warns against exposing raw connections across stores.
/// </remarks>
public sealed class IssueStore
{
    private readonly MediaDashConnectionFactory _connectionFactory;

    /// <summary>Initializes a new instance of the <see cref="IssueStore"/> class.</summary>
    /// <param name="connectionFactory">Shared factory; every store family in 05-C uses the same instance.</param>
    public IssueStore(MediaDashConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    internal static void InvalidateUndoneMove(SqliteConnection connection, SqliteTransaction transaction, long issueId, string restoredPath)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        TryTransitionOpenIssue(connection, transaction, issueId, [IssueStatus.Detected, IssueStatus.Queued, IssueStatus.Fixed], IssueStatus.Detected);
        command.CommandText = "UPDATE issues SET path=@path,details=@details,suggested_fix=@message WHERE id=@id AND status<>@dismissed";
        const string message = "This move was undone. Re-scan and preview the current item before approving another move.";
        command.Parameters.AddWithValue("@dismissed", (int)IssueStatus.Dismissed);
        command.Parameters.AddWithValue("@details", JsonSerializer.Serialize(new { requiresRescan = true, warnings = new[] { new IssueWarning { Code = "move-undone", Blocking = true, Message = message } } }));
        command.Parameters.AddWithValue("@message", message);
        command.Parameters.AddWithValue("@path", restoredPath);
        command.Parameters.AddWithValue("@id", issueId);
        command.ExecuteNonQuery();
    }

    internal static void InvalidateMovedPaths(SqliteConnection connection, SqliteTransaction transaction, MediaMovePlan plan, long primaryIssueId, string operationId)
    {
        var rows = new List<(long Id, string Path, string Details, string ItemId)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id,path,details,item_id FROM issues WHERE status IN (@detected,@queued,@dismissed) AND id<>@primary";
            read.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            read.Parameters.AddWithValue("@queued", (int)IssueStatus.Queued);
            read.Parameters.AddWithValue("@dismissed", (int)IssueStatus.Dismissed);
            read.Parameters.AddWithValue("@primary", primaryIssueId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                }
                catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
                {
                    throw new IOException("A stored finding could not be revalidated after the move. Preserve the recorded move locations for review.", error);
                }
            }
        }

        var affectedItems = plan.ItemBindings.Select(binding => binding.ItemId).Append(plan.ItemId).Where(id => id != Guid.Empty).ToHashSet();
        foreach (var row in rows)
        {
            var mapped = row.Path;
            foreach (var entry in plan.Entries)
            {
                if (plan.CatalogPath.Length > 0 && string.Equals(row.Path, plan.CatalogPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    && LibraryGuard.IsUnder(plan.ItemPath, entry.Source))
                {
                    mapped = Path.GetFullPath(Path.Combine(entry.Destination, Path.GetRelativePath(entry.Source, plan.ItemPath)));
                    break;
                }

                if (Path.IsPathFullyQualified(row.Path) && LibraryGuard.IsUnder(row.Path, entry.Source))
                {
                    mapped = string.Equals(row.Path, entry.Source, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                        ? entry.Destination : Path.GetFullPath(Path.Combine(entry.Destination, Path.GetRelativePath(entry.Source, row.Path)));
                    break;
                }
            }

            var affected = mapped != row.Path || (Guid.TryParse(row.ItemId, out var itemId) && affectedItems.Contains(itemId));
            affected |= Path.IsPathFullyQualified(row.Path) && plan.Entries.Any(entry => MediaMovePlanner.Overlap(row.Path, entry.Source) || MediaMovePlanner.Overlap(row.Path, entry.Destination));
            if (!affected)
            {
                try
                {
                    using var details = JsonDocument.Parse(row.Details);
                    affected = HasMovedBinding(details.RootElement, plan);
                }
                catch (JsonException)
                {
                    // Unrelated malformed findings remain handled by their own fixer guards.
                }
            }

            if (!affected)
            {
                continue;
            }

            const string message = "A recorded move changed this item's paths or references. Scan again before approving a new fix.";
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            TryTransitionOpenIssue(connection, transaction, row.Id, [IssueStatus.Detected, IssueStatus.Queued], IssueStatus.Detected);
            update.CommandText = "UPDATE issues SET path=@path,details=@details,suggested_fix=@message,size_savings=0 WHERE id=@id";
            update.Parameters.AddWithValue("@path", mapped);
            update.Parameters.AddWithValue("@details", JsonSerializer.Serialize(new
            {
                requiresRescan = true,
                moveOperationId = operationId,
                warnings = new[] { new IssueWarning { Code = "move-paths-changed", Blocking = true, Message = message } }
            }));
            update.Parameters.AddWithValue("@message", message);
            update.Parameters.AddWithValue("@id", row.Id);
            update.ExecuteNonQuery();
        }
    }

    private static bool HasMovedBinding(JsonElement value, MediaMovePlan plan, bool pathValue = false)
    {
        if (value.ValueKind == JsonValueKind.String && pathValue)
        {
            var path = value.GetString();
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                && (string.Equals(path, plan.CatalogPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || plan.Entries.Any(entry => LibraryGuard.IsUnder(path, entry.Source) || LibraryGuard.IsUnder(path, entry.Destination)));
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Any(element => HasMovedBinding(element, plan, pathValue));
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.EnumerateObject().Any(property => HasMovedBinding(
                property.Value,
                plan,
                property.Name.ToUpperInvariant() is "PATH" or "PATHS" or "FILES" or "SOURCE" or "SOURCEPATH" or "TARGET" or "TARGETPATH"
                    or "DESTINATION" or "APPROVEDSOURCE" or "KEEPERPATH" or "ORIGINALPATH" or "RECOVERYPATH" or "COMPANIONPATHS"));
        }

        return false;
    }

    internal bool InvalidateStaleMoveApproval(long issueId, string fingerprint, string message)
    {
        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT " + IssueRowReader.ProjectionColumns + " FROM issues WHERE id=@id";
            read.Parameters.AddWithValue("@id", issueId);
            using var reader = read.ExecuteReader();
            if (!reader.Read() || !IssueRowReader.TryRead(reader, "issues", out var current) || current.Status != IssueStatus.Queued
                || MediaMovePlan.ReadIssue(current)?.Fingerprint != fingerprint)
            {
                return false;
            }
        }

        if (!TryTransitionOpenIssue(connection, transaction, issueId, [IssueStatus.Queued], IssueStatus.Detected))
        {
            return false;
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE issues SET details=@details,suggested_fix=@message,size_savings=0 WHERE id=@id";
        update.Parameters.AddWithValue("@details", JsonSerializer.Serialize(new
        {
            requiresRescan = true, previousMoveFingerprint = fingerprint,
            warnings = new[] { new IssueWarning { Code = "move-preview-stale", Blocking = true, Message = message } }
        }));
        update.Parameters.AddWithValue("@message", message);
        update.Parameters.AddWithValue("@id", issueId);
        update.ExecuteNonQuery();
        transaction.Commit();
        return true;
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
        if (type == IssueType.Playability && issues.Count > 0)
        {
            var failed = GetIssues(type, IssueStatus.Detected).Where(PlayabilityRepairState.IsCurrent)
                .GroupBy(i => i.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var issue in issues.Where(i => failed.ContainsKey(i.Path)))
            {
                using var prior = JsonDocument.Parse(failed[issue.Path].DetailsJson);
                var identity = prior.RootElement.GetProperty("repair_failed");
                PlayabilityRepairState.Apply(issue, identity.GetProperty("length").GetInt64(), identity.GetProperty("mtime_ticks").GetInt64());
            }
        }

        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var reserved = MediaMoveStore.CaptureReservationCheck(connection, transaction, out var hasReservations, out var itemReserved);
        var preserved = new List<long>();
        if (hasReservations)
        {
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT id,path,item_id FROM issues WHERE type=@type AND status=@detected";
            read.Parameters.AddWithValue("@type", (int)type);
            read.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                if ((reader.GetValue(1) is string path && reserved(path))
                    || (Guid.TryParse(reader.GetString(2), out var itemId) && itemReserved(itemId)))
                {
                    preserved.Add(reader.GetInt64(0));
                }
            }
        }

        if (scannedPaths is null)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM issues WHERE type = @type AND status = @detected AND id NOT IN (SELECT value FROM json_each(@preserved))";
            delete.Parameters.AddWithValue("@type", (int)type);
            delete.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            delete.Parameters.AddWithValue("@preserved", JsonSerializer.Serialize(preserved));
            delete.ExecuteNonQuery();
        }
        else
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM issues WHERE type = @type AND status = @detected AND path = @path AND id NOT IN (SELECT value FROM json_each(@preserved))";
            delete.Parameters.AddWithValue("@type", (int)type);
            delete.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            delete.Parameters.AddWithValue("@preserved", JsonSerializer.Serialize(preserved));
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
                INSERT INTO issues (type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc, confidence)
                SELECT @type, @itemId, @path, @details, @suggestedFix, @sizeSavings, @detected, @detectedAt, @confidence
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
            var pConfidence = insert.Parameters.Add("@confidence", SqliteType.Real);

            foreach (var issue in issues)
            {
                if (reserved(issue.Path) || itemReserved(issue.ItemId))
                {
                    continue;
                }

                pType.Value = (int)type;
                pItemId.Value = issue.ItemId.ToString("N");
                pPath.Value = issue.Path;
                pDetails.Value = issue.DetailsJson;
                pSuggestedFix.Value = issue.SuggestedFix;
                pSizeSavings.Value = issue.SizeSavings;
                pDetectedAt.Value = issue.DetectedAtUtc.Ticks;
                pConfidence.Value = issue.Confidence.HasValue ? issue.Confidence.Value : (object)DBNull.Value;
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Wipes Detected rows for every supplied issue type. Queued and Dismissed rows are left
    /// untouched — the user's approval / suppression outlives a config edit.
    /// </summary>
    /// <param name="types">The affected issue types.</param>
    /// <returns>The number of rows removed.</returns>
    public int WipeDetectedForTypes(IEnumerable<IssueType> types)
    {
        var typeList = types?.Distinct().ToList() ?? new List<IssueType>();
        if (typeList.Count == 0)
        {
            return 0;
        }

        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM issues WHERE type = @type AND status = @detected";
        delete.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
        var pType = delete.Parameters.Add("@type", SqliteType.Integer);

        var total = 0;
        foreach (var t in typeList)
        {
            pType.Value = (int)t;
            total += delete.ExecuteNonQuery();
        }

        transaction.Commit();
        return total;
    }

    /// <summary>Gets issues, optionally filtered by type and status.</summary>
    /// <param name="type">Filter by issue type, or null for all types.</param>
    /// <param name="status">Filter by status, or null for all statuses.</param>
    /// <returns>The matching issues, newest first.</returns>
    public IReadOnlyList<Issue> GetIssues(IssueType? type = null, IssueStatus? status = null)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + IssueRowReader.ProjectionColumns + " FROM issues"
            + " WHERE (@type IS NULL OR type = @type) AND (@status IS NULL OR status = @status) ORDER BY id DESC";
        cmd.Parameters.AddWithValue("@type", type is null ? DBNull.Value : (int)type);
        cmd.Parameters.AddWithValue("@status", status is null ? DBNull.Value : (int)status);

        var result = new List<Issue>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (IssueRowReader.TryRead(reader, "issues", out var issue))
            {
                result.Add(issue);
            }
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
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type, COUNT(*), SUM(size_savings), MAX(detected_at_utc) FROM issues WHERE status IN (@detected, @queued) GROUP BY type";
        cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
        cmd.Parameters.AddWithValue("@queued", (int)IssueStatus.Queued);

        var result = new List<IssueSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!RowHydration.TryHydrateIssueType(reader.GetInt32(0), rowId: 0, "issues (GetSummary)", out var summaryType))
            {
                continue;
            }

            result.Add(new IssueSummary
            {
                Type = summaryType,
                Count = reader.GetInt32(1),
                PotentialSavings = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                // Aggregate MAX(): a single malformed detected_at_utc row makes the SQL
                // aggregate return the malformed value here. SafeUtcFromTicks clamps so the
                // whole /Status endpoint stays readable — the individual malformed row still
                // gets flagged via GetIssues's per-row Diagnostics.Record.
                NewestDetectedUtc = RowHydration.SafeUtcFromTicks(reader.GetInt64(3), "issues (GetSummary MAX)")
            });
        }

        return result;
    }

    /// <summary>Guarded status transition. See <see cref="MediaDashDb.TryTransitionIssueStatus"/> for full contract.</summary>
    /// <param name="issueId">The issue id.</param>
    /// <param name="fromAny">Statuses that permit this transition. Empty means "any status" is rejected.</param>
    /// <param name="to">Target status.</param>
    /// <returns>Applied / NotFound / Conflict.</returns>
    public StatusTransitionResult TryTransitionIssueStatus(long issueId, IReadOnlyCollection<IssueStatus> fromAny, IssueStatus to)
    {
        if (fromAny is null || fromAny.Count == 0)
        {
            return GetIssueStatus(issueId) is null ? StatusTransitionResult.NotFound : StatusTransitionResult.Conflict;
        }

        using var connection = _connectionFactory.Open();
        if (TryTransitionOpenIssue(connection, null, issueId, fromAny, to))
        {
            return StatusTransitionResult.Applied;
        }

        return GetIssueStatus(issueId) is null ? StatusTransitionResult.NotFound : StatusTransitionResult.Conflict;
    }

    private static bool TryTransitionOpenIssue(SqliteConnection connection, SqliteTransaction? transaction, long issueId, IReadOnlyCollection<IssueStatus> fromAny, IssueStatus to)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        var names = new List<string>(fromAny.Count);
        var i = 0;
        foreach (var s in fromAny)
        {
            var name = "@from" + i.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            cmd.Parameters.AddWithValue(name, (int)s);
            i++;
        }

#pragma warning disable CA2100, CA3001 // placeholder list is machine-composed from an int range, ids/statuses are bound as parameters — no user text reaches the SQL string
        cmd.CommandText = "UPDATE issues SET status = @to WHERE id = @id AND status IN (" + string.Join(",", names) + ")";
#pragma warning restore CA2100, CA3001
        cmd.Parameters.AddWithValue("@to", (int)to);
        cmd.Parameters.AddWithValue("@id", issueId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Reads a single issue's current status.</summary>
    /// <param name="issueId">The issue id.</param>
    /// <returns>The current status, or null if the row is gone.</returns>
    public IssueStatus? GetIssueStatus(long issueId)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM issues WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", issueId);
        var raw = cmd.ExecuteScalar();
        if (raw is null || raw is DBNull)
        {
            return null;
        }

        var value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        return RowHydration.TryHydrateIssueStatus(value, issueId, "issues", out var status) ? status : null;
    }

    /// <summary>Guarded bulk status update. See <see cref="MediaDashDb.BulkUpdateOpenIssueStatus"/> for full contract.</summary>
    /// <param name="ids">Issue ids to update.</param>
    /// <param name="target">Target status.</param>
    /// <returns>IDs of rows actually transitioned.</returns>
    public IReadOnlyList<long> BulkUpdateOpenIssueStatus(IReadOnlyList<long> ids, IssueStatus target)
    {
        if (ids.Count == 0)
        {
            return Array.Empty<long>();
        }

        // SQLite's default parameter limit is 999; a fresh install with 5000+ duplicates otherwise
        // throws SqliteException here. Chunk into 500-id batches — each batch runs its own UPDATE.
        const int chunkSize = 500;
        // Source-status filter depends on the target. For a Queued target (Approve action) we only
        // touch Detected rows so that RETURNING id gives us the exact just-transitioned set —
        // callers use that list to run the blocking-warning consent rollback and mustn't confuse
        // "just approved by this bulk op" with "already queued from an earlier manual approve".
        // Approving an already-Queued row was a semantic no-op anyway (SET status = Queued WHERE
        // status = Queued), so filtering it out costs nothing. For Dismissed target both Detected
        // and Queued rows still get dismissed — dismissing an already-queued row is a legitimate
        // "cancel this fix" action.
        var sourceFilter = target == IssueStatus.Queued
            ? "status = @detected"
            : target == IssueStatus.Detected
                ? "status IN (@dismissed, @queued)"
                : "status IN (@detected, @queued)";
        using var connection = _connectionFactory.Open();
        var transitioned = new List<long>(ids.Count);
        for (var offset = 0; offset < ids.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, ids.Count - offset);
            using var cmd = connection.CreateCommand();
            var placeholders = string.Join(",", Enumerable.Range(0, count).Select(i => "@id" + i.ToString(CultureInfo.InvariantCulture)));
#pragma warning disable CA2100, CA3001 // placeholder list is machine-composed from an int range, ids/statuses are bound as parameters — no user text reaches the SQL string
            cmd.CommandText = "UPDATE issues SET status = @target WHERE id IN (" + placeholders + ") AND " + sourceFilter + " RETURNING id";
#pragma warning restore CA2100, CA3001
            cmd.Parameters.AddWithValue("@target", (int)target);
            cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            cmd.Parameters.AddWithValue("@queued", (int)IssueStatus.Queued);
            cmd.Parameters.AddWithValue("@dismissed", (int)IssueStatus.Dismissed);
            for (var i = 0; i < count; i++)
            {
                cmd.Parameters.AddWithValue("@id" + i.ToString(CultureInfo.InvariantCulture), ids[offset + i]);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                transitioned.Add(reader.GetInt64(0));
            }
        }

        return transitioned;
    }

    /// <summary>Bulk un-ignore: moves Dismissed rows back to Detected.</summary>
    /// <param name="ids">Issue ids to revert.</param>
    /// <returns>Number of rows actually updated.</returns>
    public int BulkRevertDismissedIssues(IReadOnlyList<long> ids)
    {
        return BulkUpdateOpenIssueStatus(ids, IssueStatus.Detected).Count;
    }

    /// <summary>Re-points issue rows after a move-style fixer runs. See <see cref="MediaDashDb.RelocateIssuePaths"/>.</summary>
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

        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        // Exact match (file move) → rewrite in place; the SUBSTR tail is empty so @new stays @new.
        // Prefix match with '/' or '\' boundary (folder move) → rewrite with the folder prefix swapped.
        // Uses SUBSTR equality rather than LIKE to avoid % / _ being treated as wildcards on paths.
        //
        // Bug-pattern audit round 4 P-06 (2026-09-19): the unconditional COLLATE NOCASE below
        // came from a legitimate 1.0.0-era Windows fix (stored paths can differ in case from
        // the fixer's post-move path because scanner sources vary in case-folding), but it
        // silently merged case-only sibling directories on case-sensitive hosts. On a Linux
        // ext4/xfs/btrfs library where `/media/Film` and `/media/film` are legitimately distinct
        // directories, relocating just `/media/Film` rewrote BOTH — issues in `/media/film` now
        // pointed at the wrong destination and subsequent queued fixes silently targeted the
        // wrong media. Gate NOCASE behind Windows; on other hosts fall back to case-sensitive
        // matching. macOS default APFS is case-insensitive but can be case-sensitive on a
        // per-volume basis — erring case-sensitive there is safer: a missed rewrite becomes a
        // "no longer exists" that a fresh scan corrects, while an over-match points at the
        // wrong file (much harder to undo).
        var nocase = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;
        cmd.CommandText = @"
            UPDATE issues
               SET path = @new || SUBSTR(path, LENGTH(@old) + 1)
             WHERE path = @old" + nocase + @"
                OR SUBSTR(path, 1, LENGTH(@old) + 1) = @oldSlash" + nocase + @"
                OR SUBSTR(path, 1, LENGTH(@old) + 1) = @oldBackslash" + nocase;
        cmd.Parameters.AddWithValue("@old", oldPath);
        cmd.Parameters.AddWithValue("@new", newPath);
        cmd.Parameters.AddWithValue("@oldSlash", oldPath + "/");
        cmd.Parameters.AddWithValue("@oldBackslash", oldPath + "\\");
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Auto-queue detected issues, respecting the restored_paths veto and confidence gate.</summary>
    /// <param name="type">The issue type.</param>
    /// <param name="minConfidence">Optional minimum <c>Issue.Confidence</c> threshold. Only pass a non-null value for confidence-gated types (currently Duplicate).</param>
    /// <returns>The IDs of the rows this call moved from Detected to Queued.</returns>
    /// <param name="requireConsent">Atomically retain blocking findings as Detected during automatic queuing.</param>
    public IReadOnlyList<long> QueueDetectedIssues(IssueType type, double? minConfidence = null, bool requireConsent = false)
    {
        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        // The NOT EXISTS gate against restored_paths implements the "if a user restored a file, no
        // scanner may auto-fix it again" invariant. Manual approval via /Issues/{id}/Approve still
        // goes through the guarded TryTransitionIssueStatus helper and doesn't touch this method.
        // Select consent-safe candidates and transition them in one transaction so a crash
        // cannot expose a partly approved blocking finding to the next fix run.
        var baseSql = """
            SELECT id, details FROM issues
            WHERE type = @type AND status = @detected
              AND NOT EXISTS (
                  SELECT 1 FROM restored_paths rp
                  WHERE rp.path = issues.path AND (rp.type = issues.type OR rp.type = @any)
              )
            """;

        if (minConfidence is double gate)
        {
            cmd.CommandText = baseSql + " AND (confidence IS NULL OR confidence >= @gate)";
            cmd.Parameters.AddWithValue("@gate", gate);
        }
        else
        {
            cmd.CommandText = baseSql;
        }

        cmd.Parameters.AddWithValue("@type", (int)type);
        cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
        cmd.Parameters.AddWithValue("@any", MediaDashSchema.RestoredPathAnyType);

        var candidates = new List<long>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var issue = new Issue { DetailsJson = reader.GetString(1) };
                if (!requireConsent || (!issue.HasBlockingWarnings && !issue.IsReportOnly && !issue.RequiresRescan))
                {
                    candidates.Add(reader.GetInt64(0));
                }
            }
        }

        var queuedIds = new List<long>(candidates.Count);
        foreach (var id in candidates)
        {
            if (TryTransitionOpenIssue(connection, transaction, id, [IssueStatus.Detected], IssueStatus.Queued))
            {
                queuedIds.Add(id);
            }
        }

        transaction.Commit();
        return queuedIds;
    }

    /// <summary>Reverts auto-queued issues whose DetailsJson carries a blocking warning back to Detected.</summary>
    /// <param name="justAutoQueuedIds">Exact set of IDs the current auto-queue pass just moved from Detected to Queued.</param>
    /// <returns>The number of issues reverted from Queued to Detected.</returns>
    public int RollbackAutoQueuedBlockingWarnings(IReadOnlyCollection<long> justAutoQueuedIds)
    {
        if (justAutoQueuedIds is null || justAutoQueuedIds.Count == 0)
        {
            return 0;
        }

        // Filter down to rows that (a) are in the just-queued set, (b) are still Queued (the fixer
        // may have already touched some), and (c) carry a blocking warning in DetailsJson.
        var justQueuedSet = new HashSet<long>(justAutoQueuedIds);
        var toRevert = GetIssues(status: IssueStatus.Queued)
            .Where(i => justQueuedSet.Contains(i.Id) && (i.HasBlockingWarnings || i.IsReportOnly))
            .Select(i => i.Id)
            .ToList();

        if (toRevert.Count == 0)
        {
            return 0;
        }

        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        var reverted = 0;
        foreach (var id in toRevert)
        {
            if (TryTransitionOpenIssue(connection, transaction, id, [IssueStatus.Queued], IssueStatus.Detected))
            {
                reverted++;
            }
        }

        transaction.Commit();
        return reverted;
    }

    /// <summary>
    /// Counts open FormatCompatibility issues (Detected + Queued) grouped by the <c>converter</c>
    /// field in each row's DetailsJson. Rows without a parsable <c>converter</c> key are bucketed
    /// as <c>"unknown"</c>. Called by <see cref="Analytics.AnalyticsReporter"/> for the per-converter
    /// breakdown; internal so the static helper is directly testable.
    /// </summary>
    /// <returns>Dictionary of converter key → count.</returns>
    public IReadOnlyDictionary<string, int> GroupFormatCompatibilityIssuesByConverter()
    {
        var rows = GetIssues(IssueType.FormatCompatibility);
        return GroupByConverter(rows.Select(i => i.DetailsJson));
    }

    /// <summary>
    /// Extracts the <c>converter</c> string from each DetailsJson blob and groups counts by it.
    /// Rows without a parsable <c>converter</c> string fall into the <c>"unknown"</c> bucket.
    /// Static and internal so tests can call it without a live store.
    /// </summary>
    /// <param name="detailsJsonSequence">Sequence of DetailsJson strings (one per issue/history row).</param>
    /// <returns>Dictionary of converter key → count.</returns>
    internal static IReadOnlyDictionary<string, int> GroupByConverter(IEnumerable<string> detailsJsonSequence)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var json in detailsJsonSequence)
        {
            var key = ExtractConverterKey(json);
            counts.TryGetValue(key, out var n);
            counts[key] = n + 1;
        }

        return counts;
    }

    /// <summary>Parses the <c>converter</c> field from a DetailsJson blob; returns <c>"unknown"</c> on any parse failure.</summary>
    /// <param name="json">The raw DetailsJson string from the DB, or null.</param>
    /// <returns>The converter key string, or <c>"unknown"</c> when absent or unparsable.</returns>
    internal static string ExtractConverterKey(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "unknown";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("converter", out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                var val = prop.GetString();
                return string.IsNullOrEmpty(val) ? "unknown" : val;
            }
        }
        catch (JsonException)
        {
            // ponytail: malformed JSON → unknown bucket, not a throw
        }

        return "unknown";
    }

    /// <summary>Overwrites the details column for a single issue row.</summary>
    /// <param name="issueId">The issue id.</param>
    /// <param name="detailsJson">The replacement JSON string.</param>
    /// <returns>True when the row was found and updated; false when no such row exists.</returns>
    public bool UpdateDetails(long issueId, string detailsJson)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE issues SET details = @details WHERE id = @id";
        cmd.Parameters.AddWithValue("@details", detailsJson);
        cmd.Parameters.AddWithValue("@id", issueId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Replaces one finding's details and suggested fix with a fresh plan for the same item.</summary>
    /// <param name="issueId">The issue id.</param>
    /// <param name="replacement">The re-planned finding.</param>
    /// <returns>True when the row was still open and was updated.</returns>
    internal bool ReplacePlan(long issueId, Issue replacement)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE issues SET path = @path, details = @details, suggested_fix = @fix WHERE id = @id AND status = @detected";
        cmd.Parameters.AddWithValue("@path", replacement.Path);
        cmd.Parameters.AddWithValue("@details", replacement.DetailsJson);
        cmd.Parameters.AddWithValue("@fix", replacement.SuggestedFix);
        cmd.Parameters.AddWithValue("@id", issueId);
        cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Gets a single issue by id.</summary>
    /// <param name="issueId">The issue id.</param>
    /// <returns>The issue, or null when the row is gone or malformed.</returns>
    public Issue? GetIssue(long issueId)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + IssueRowReader.ProjectionColumns + " FROM issues WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", issueId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        // F-014 (single-row variant): tolerate malformed item_id / enum / ticks — return null
        // rather than throwing all the way up. Every caller of GetIssue already handles the
        // "no such row" null return, so the malformed case reuses that path via IssueRowReader.
        return IssueRowReader.TryRead(reader, "issues", out var issue) ? issue : null;
    }
}

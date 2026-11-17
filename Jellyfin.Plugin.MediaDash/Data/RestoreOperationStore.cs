using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Fixers;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Owns the <c>restore_operations</c> table (schema v7, added by 05-B). Every restore route
/// through <see cref="Fixers.RestoreService"/> serializes on this table: the <c>entry_key</c>
/// UNIQUE constraint is the sole synchronisation point for physical bin-entry access, and
/// <c>operation_id</c> is the owner token that only the claim-holder can Complete / Abort /
/// MarkNeedsReview. Extracted from <see cref="MediaDashDb"/> in 05-C.6; the facade retains
/// one-line forwarders so pre-existing test callers (RestoreOperationsTests + SchemaUpgradeTests)
/// keep working.
/// </summary>
/// <remarks>
/// The pre-05-B <c>history.restored = 1</c> claim pattern (with its `TryClaimRestore` /
/// `ReleaseRestoreClaim` helpers) was deleted in this sub-substep along with
/// <c>RestoreClaimTests.cs</c>. The equivalent (and strictly stronger) atomicity is provided by
/// the entry_key UNIQUE constraint here and covered by <c>RestoreOperationsTests</c>'s 20-trial
/// concurrency test.
/// </remarks>
public sealed class RestoreOperationStore
{
    private readonly MediaDashConnectionFactory _connectionFactory;

    /// <summary>Initializes a new instance of the <see cref="RestoreOperationStore"/> class.</summary>
    /// <param name="connectionFactory">Shared factory; every 05-C store family uses the same instance.</param>
    public RestoreOperationStore(MediaDashConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>
    /// Atomically claims the exclusive right to restore the bin entry identified by
    /// <paramref name="entryKey"/>. Returns a fresh operation id (owner token) on success, or
    /// <c>null</c> when another Pending / NeedsReview / Completed row already exists for the same key.
    /// </summary>
    /// <param name="entryKey">Canonical identity of the bin entry.</param>
    /// <param name="historyId">Optional history row that triggered this restore (null for manifest-only).</param>
    /// <param name="sourcePath">The bin file/directory being restored.</param>
    /// <param name="destinationPath">The resolved final target on disk.</param>
    /// <returns>Owner token or null when the entry is already claimed.</returns>
    public string? TryBeginRestore(string entryKey, long? historyId, string sourcePath, string destinationPath)
    {
        if (string.IsNullOrEmpty(entryKey))
        {
            return null;
        }

        var operationId = Guid.NewGuid().ToString("N");
        var paths = new[] { sourcePath, destinationPath };
        MediaMovePathClaims.Claim[] nativeClaims;
        try
        {
            // Native metadata is captured before acquiring the SQLite write lock.
            nativeClaims = MediaMovePathClaims.Capture(paths);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (MediaMoveStore.HasActivePathConflict(connection, transaction, paths, nativeClaims)
            || HasActivePathConflict(connection, transaction, paths, nativeClaims))
        {
            return null;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        // ON CONFLICT DO NOTHING makes the INSERT idempotent against the entry_key UNIQUE
        // constraint — the row we care about is "who owns this entry_key right now". Absence of a
        // row + successful INSERT = we won the claim. Any conflict (Pending / NeedsReview /
        // Completed row already present for the same key) leaves the existing row untouched and
        // returns 0 affected rows.
        cmd.CommandText = """
            INSERT INTO restore_operations
                (operation_id, entry_key, history_id, source_path, destination_path, state, started_utc_ticks, detail, native_claims_json)
            VALUES (@op, @ek, @hid, @src, @dst, 'Pending', @ticks, NULL, @claims)
            ON CONFLICT(entry_key) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@op", operationId);
        cmd.Parameters.AddWithValue("@claims", JsonSerializer.Serialize(nativeClaims, MediaMovePlan.JsonOptions));
        cmd.Parameters.AddWithValue("@ek", entryKey);
        cmd.Parameters.AddWithValue("@hid", historyId.HasValue ? historyId.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@src", sourcePath ?? string.Empty);
        cmd.Parameters.AddWithValue("@dst", destinationPath ?? string.Empty);
        cmd.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
        var acquired = cmd.ExecuteNonQuery() > 0;
        transaction.Commit();
        return acquired ? operationId : null;
    }

    internal static bool HasActivePathConflict(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<string> paths, MediaMovePathClaims.Claim[]? nativeClaims = null)
    {
        var incoming = paths.Select(MediaMoveStore.NormalizeClaimPath).ToArray();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT source_path,destination_path,native_claims_json FROM restore_operations WHERE state<>'Completed'";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                var source = MediaMoveStore.NormalizeClaimPath(reader.GetString(0));
                var destination = MediaMoveStore.NormalizeClaimPath(reader.GetString(1));
                if (source is null || destination is null || incoming.Any(path => path is null
                    || MediaMovePlanner.Overlap(path, source) || MediaMovePlanner.Overlap(path, destination))
                    || MediaMovePathClaims.Overlap(MediaMovePathClaims.Read(reader.IsDBNull(2) ? null : reader.GetString(2), [source, destination]), nativeClaims))
                {
                    return true;
                }
            }
            catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Marks a Pending restore as successful and removes the row so the entry_key is free to be re-claimed.</summary>
    /// <param name="operationId">The owner token returned by <see cref="TryBeginRestore"/>.</param>
    /// <param name="finalDestination">The actual destination path the FS work landed at.</param>
    /// <returns>True when the row was Pending under this owner and successfully removed.</returns>
    public bool CompleteRestore(string operationId, string finalDestination)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            return false;
        }

        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        // Two-step: UPDATE to Completed (records the final destination), then DELETE. If the
        // process dies between the two, reconciliation-at-startup DELETEs any lingering Completed
        // rows. Net observable outcome of a successful restore is "no row" — the entry_key stays
        // free for re-restore later.
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE restore_operations SET state = 'Completed', destination_path = @dst WHERE operation_id = @op AND state = 'Pending'";
            update.Parameters.AddWithValue("@op", operationId);
            update.Parameters.AddWithValue("@dst", finalDestination ?? string.Empty);
            if (update.ExecuteNonQuery() == 0)
            {
                transaction.Rollback();
                return false;
            }
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM restore_operations WHERE operation_id = @op AND state = 'Completed'";
            delete.Parameters.AddWithValue("@op", operationId);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    /// <summary>Releases a Pending claim without recording success. Owner-mismatched or non-Pending rows untouched.</summary>
    /// <param name="operationId">The owner token returned by <see cref="TryBeginRestore"/>.</param>
    /// <returns>True when a Pending row owned by this caller was removed.</returns>
    public bool AbortRestore(string operationId)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            return false;
        }

        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM restore_operations WHERE operation_id = @op AND state = 'Pending'";
        cmd.Parameters.AddWithValue("@op", operationId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Transitions a Pending operation to NeedsReview. Used when filesystem mutation started but
    /// couldn't be verifiably completed. The row stays in the table so <see cref="GetRestoreOperations"/>
    /// surfaces it for manual triage; another restore of the same entry is refused until an admin cleans up.
    /// </summary>
    /// <param name="operationId">The owner token returned by <see cref="TryBeginRestore"/>.</param>
    /// <param name="detail">Human-readable explanation of what left the operation uncertain.</param>
    /// <returns>True when a Pending row owned by this caller was transitioned.</returns>
    public bool MarkRestoreNeedsReview(string operationId, string detail)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            return false;
        }

        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE restore_operations SET state = 'NeedsReview', detail = @d WHERE operation_id = @op AND state = 'Pending'";
        cmd.Parameters.AddWithValue("@op", operationId);
        cmd.Parameters.AddWithValue("@d", (object?)detail ?? DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Startup sweep: any Pending row is from a process that died mid-restore. Convert every
    /// Pending row to NeedsReview with a detail explaining process death; also DELETE any
    /// Completed rows left over from a crash between UPDATE-to-Completed and the DELETE inside
    /// <see cref="CompleteRestore"/>.
    /// </summary>
    /// <returns>The count of Pending rows that were reclassified as NeedsReview.</returns>
    public int ReconcilePendingRestoresAtStartup()
    {
        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();

        // Cleanup leftover Completed rows first — they represent successful restores where the
        // process died between the UPDATE and the DELETE inside CompleteRestore. entry_key is
        // free again after this.
        using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM restore_operations WHERE state = 'Completed'";
            cleanup.ExecuteNonQuery();
        }

        int reconciled;
        using (var reconcile = connection.CreateCommand())
        {
            reconcile.Transaction = transaction;
            reconcile.CommandText = "UPDATE restore_operations SET state = 'NeedsReview', detail = @d WHERE state = 'Pending'";
            reconcile.Parameters.AddWithValue("@d", "Process died mid-restore during a previous run. Inspect source_path (was in the recycle bin) and destination_path before deciding whether to retry or delete this row.");
            reconciled = reconcile.ExecuteNonQuery();
        }

        transaction.Commit();
        return reconciled;
    }

    /// <summary>Returns every row currently in restore_operations. Used by NeedsReview surfaces and tests.</summary>
    /// <returns>All rows, newest-first by <c>started_utc_ticks</c>.</returns>
    public IReadOnlyList<RestoreOperationRow> GetRestoreOperations()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT operation_id, entry_key, history_id, source_path, destination_path, state, started_utc_ticks, detail"
            + " FROM restore_operations ORDER BY started_utc_ticks DESC";
        var result = new List<RestoreOperationRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new RestoreOperationRow(
                OperationId: reader.GetString(0),
                EntryKey: reader.GetString(1),
                HistoryId: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                SourcePath: reader.GetString(3),
                DestinationPath: reader.GetString(4),
                State: reader.GetString(5),
                StartedUtc: RowHydration.SafeUtcFromTicks(reader.GetInt64(6), "restore_operations"),
                Detail: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return result;
    }
}

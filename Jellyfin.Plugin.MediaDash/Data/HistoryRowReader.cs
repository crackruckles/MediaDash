using System;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

// Column-order-locked hydration for the history table. Every caller (GetHistory / GetHistoryEntry)
// MUST project via <see cref="ProjectionColumns"/> so this reader's positional GetXxx(N) calls
// stay aligned. Extracted from MediaDashDb in 05-A. Malformed IssueType is skipped with a
// diagnostic breadcrumb; malformed ticks are clamped rather than skipped so a corrupt timestamp
// never drops an otherwise-usable history row (F-014-adjacent).
internal static class HistoryRowReader
{
    // Positional projection consumed by TryRead. Keep the order and count identical across every
    // callsite; if a column moves, both the caller's SELECT and this reader break together.
    public const string ProjectionColumns =
        "id, issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, restored, success, acknowledged, saved_bytes, move_operation_id";

    // Hydrates one row from a reader positioned on a row projected via <see cref="ProjectionColumns"/>.
    // Returns false when the type column is out-of-range; the entry is left at its default so the
    // caller can `continue` past the malformed row without breaking the enclosing loop.
    public static bool TryRead(SqliteDataReader reader, string table, out HistoryEntry entry)
    {
        var historyId = reader.GetInt64(0);
        if (!RowHydration.TryHydrateIssueType(reader.GetInt32(2), historyId, table, out var historyType))
        {
            entry = default!;
            return false;
        }

        var rawMoveId = reader.IsDBNull(13) ? null : reader.GetValue(13) as string;
        var moveId = Guid.TryParseExact(rawMoveId, "N", out _) ? rawMoveId : null;
        if (!reader.IsDBNull(13) && moveId is null)
        {
            Api.Diagnostics.Record("MediaMove.InvalidHistoryLink", "History entry #" + historyId + " has a damaged link to its move record, so it is shown without an Undo button. The files themselves are not affected.");
        }

        entry = new HistoryEntry
        {
            Id = historyId,
            IssueId = reader.GetInt64(1),
            Type = historyType,
            Path = reader.GetString(3),
            Action = reader.GetString(4),
            BytesFreed = reader.GetInt64(5),
            RecyclePath = reader.IsDBNull(6) ? null : reader.GetString(6),
            FixedAtUtc = RowHydration.SafeUtcFromTicks(reader.GetInt64(7), table, historyId),
            WasDryRun = reader.GetInt32(8) != 0,
            Restored = reader.GetInt32(9) != 0,
            Success = reader.GetInt32(10) != 0,
            Acknowledged = reader.GetInt32(11) != 0,
            SavedBytes = reader.GetInt64(12),
            MoveOperationId = moveId
        };
        return true;
    }
}

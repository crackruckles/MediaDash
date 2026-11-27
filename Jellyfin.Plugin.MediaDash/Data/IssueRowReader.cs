using System;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

// Column-order-locked hydration for the issues table. Every caller (GetIssues / GetIssue) MUST
// project via <see cref="ProjectionColumns"/> so this reader's positional GetXxx(N) calls stay
// aligned. Extracted from MediaDashDb in 05-A. F-014 discipline preserved: skip malformed rows
// (bad GUID / out-of-range enum) with a Diagnostics.Record breadcrumb naming the row id, and
// clamp out-of-range ticks via <see cref="RowHydration.SafeUtcFromTicks"/> so a single bad row
// never takes down the whole read surface.
internal static class IssueRowReader
{
    // Positional projection consumed by TryRead. Keep the order and count identical across every
    // callsite; if a column moves, both the caller's SELECT and this reader break together.
    public const string ProjectionColumns =
        "id, type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc, confidence";

    // Hydrates one row from a reader positioned on a row projected via <see cref="ProjectionColumns"/>.
    // Returns false when the row is malformed (bad GUID / undefined enum) and issue is left at
    // its default. Ticks columns are always clamped through SafeUtcFromTicks rather than skipped —
    // a bad timestamp shouldn't drop an otherwise-usable row.
    public static bool TryRead(SqliteDataReader reader, string table, out Issue issue)
    {
        var idColumn = reader.GetInt64(0);
        var itemIdStr = reader.GetString(2);
        if (!Guid.TryParseExact(itemIdStr, "N", out var itemId))
        {
            Api.Diagnostics.Record("MediaDashDb.MalformedItemId", "MediaDash skipped a damaged entry (#" + idColumn.ToString(CultureInfo.InvariantCulture) + ") in its " + table + " list: its item reference '" + itemIdStr + "' is not valid." + " It is ignored and does no harm. To remove it, run this against mediadash.db while Jellyfin is stopped: DELETE FROM " + table + " WHERE id = " + idColumn.ToString(CultureInfo.InvariantCulture) + ";");
            issue = default!;
            return false;
        }

        if (!RowHydration.TryHydrateIssueType(reader.GetInt32(1), idColumn, table, out var issueType)
            || !RowHydration.TryHydrateIssueStatus(reader.GetInt32(7), idColumn, table, out var issueStatus))
        {
            issue = default!;
            return false;
        }

        issue = new Issue
        {
            Id = idColumn,
            Type = issueType,
            ItemId = itemId,
            Path = reader.GetString(3),
            DetailsJson = reader.GetString(4),
            SuggestedFix = reader.GetString(5),
            SizeSavings = reader.GetInt64(6),
            Status = issueStatus,
            DetectedAtUtc = RowHydration.SafeUtcFromTicks(reader.GetInt64(8), table, idColumn),
            Confidence = reader.IsDBNull(9) ? null : reader.GetDouble(9)
        };
        return true;
    }
}

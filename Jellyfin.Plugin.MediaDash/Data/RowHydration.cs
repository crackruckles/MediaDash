using System;
using System.Globalization;

namespace Jellyfin.Plugin.MediaDash.Data;

// Guarded hydration for enum / timestamp columns coming back out of SQLite. Historically inlined
// as private static helpers on MediaDashDb (MD-004 + audit round 4 P-05). Moved to a shared spot
// in 05-A so IssueRowReader and HistoryRowReader can call them from separate files without
// duplicating the diagnostic wording or the clamp bounds. CLAUDE.md safety invariant 6 keeps
// applying: log the bad value with table + row id, skip / clamp, and never let one bad row take
// down the whole read surface.
internal static class RowHydration
{
    // MD-004: guarded IssueType hydration. Out-of-range integers get logged and skipped, not
    // silently cast to an undefined enum value that would then flow into fixer/scanner decisions
    // with unpredictable behavior. Callers get a bool + out parameter and skip / return null on false.
    public static bool TryHydrateIssueType(int raw, long rowId, string table, out IssueType type)
    {
        if (Enum.IsDefined(typeof(IssueType), raw))
        {
            type = (IssueType)raw;
            return true;
        }

        Api.Diagnostics.Record("MediaDashDb.MalformedEnum", "MediaDash skipped a damaged entry (#" + rowId.ToString(CultureInfo.InvariantCulture) + ") in its " + table + " list: it has an unknown issue type (" + raw.ToString(CultureInfo.InvariantCulture) + ")." + " It is ignored and does no harm. To remove it, run this against mediadash.db while Jellyfin is stopped: DELETE FROM " + table + " WHERE id = " + rowId.ToString(CultureInfo.InvariantCulture) + ";");
        type = default;
        return false;
    }

    // Same shape as TryHydrateIssueType but for the IssueStatus column.
    public static bool TryHydrateIssueStatus(int raw, long rowId, string table, out IssueStatus status)
    {
        if (Enum.IsDefined(typeof(IssueStatus), raw))
        {
            status = (IssueStatus)raw;
            return true;
        }

        Api.Diagnostics.Record("MediaDashDb.MalformedEnum", "MediaDash skipped a damaged entry (#" + rowId.ToString(CultureInfo.InvariantCulture) + ") in its " + table + " list: it has an unknown status (" + raw.ToString(CultureInfo.InvariantCulture) + ")." + " It is ignored and does no harm. To remove it, run this against mediadash.db while Jellyfin is stopped: DELETE FROM " + table + " WHERE id = " + rowId.ToString(CultureInfo.InvariantCulture) + ";");
        status = default;
        return false;
    }

    // Bug-pattern audit round 4 P-05 (2026-09-19): every DateTime construction from raw SQLite
    // ticks used the direct `new DateTime(reader.GetInt64(n), DateTimeKind.Utc)` form, which
    // throws ArgumentOutOfRangeException for negative ticks or values above DateTime.MaxValue
    // (long.MaxValue definitely qualifies — reproduced by seeding detected_at_utc = 9.22e18).
    // Clamp out-of-range values to the domain edges and log a diagnostic pointing at the row so
    // the user can DELETE it — the surrounding reader proceeds with the good rows instead of
    // 500-ing the whole endpoint. Aggregates like MAX() over a bad row similarly get a bounded
    // value instead of an unrecoverable throw.
    public static DateTime SafeUtcFromTicks(long ticks, string table, long rowId = 0)
    {
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            Api.Diagnostics.Record("MediaDashDb.MalformedTicks", "An entry (#" + rowId.ToString(CultureInfo.InvariantCulture) + ") in MediaDash's " + table + " list had an impossible date, so it is shown with the nearest valid date instead." + " It is ignored and does no harm. To remove it, run this against mediadash.db while Jellyfin is stopped: DELETE FROM " + table + " WHERE id = " + rowId.ToString(CultureInfo.InvariantCulture) + ";");
            return ticks < DateTime.MinValue.Ticks ? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)
                                                   : DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        }

        return new DateTime(ticks, DateTimeKind.Utc);
    }
}

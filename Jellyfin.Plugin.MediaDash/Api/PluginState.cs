using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Write-through persistence for the two Plugin static fields that used to be memory-only —
/// <see cref="Plugin.LastFixRun"/> and <see cref="Plugin.RedownloadWarnings"/>. Both are shown in
/// the dashboard (fix-completion popup + redownload-warning banner) but were dropping on every
/// plugin reload / update, so a user restarting mid-day would see the banner vanish or miss the
/// popup from a fix that finished right before the reboot.
///
/// The class hides the JSON serialisation + DB round-trip so <c>Plugin.LastFixRun = summary</c>
/// stays a simple assignment at every call site.
/// </summary>
public static class PluginState
{
    private const string LastFixRunKey = "last_fix_run";
    private const string RedownloadWarningsKey = "redownload_warnings";
    private static readonly JsonSerializerOptions JsonOpts = new();

    private static Data.MediaDashDb? _db;

    /// <summary>
    /// Attach the shared database and rehydrate <see cref="Plugin.LastFixRun"/> +
    /// <see cref="Plugin.RedownloadWarnings"/> from persisted rows. Called once from the DB ctor
    /// after diagnostics rehydration.
    /// </summary>
    /// <param name="db">The plugin database.</param>
    public static void Attach(Data.MediaDashDb db)
    {
        _db = db;
        try
        {
            var lastFixRaw = db.GetState(LastFixRunKey);
            if (!string.IsNullOrEmpty(lastFixRaw))
            {
                Plugin.LastFixRunBacking = JsonSerializer.Deserialize<FixRunSummary>(lastFixRaw, JsonOpts);
            }
        }
        catch (JsonException)
        {
            // Malformed persisted state — start clean, next set overwrites the bad row.
        }

        try
        {
            var warningsRaw = db.GetState(RedownloadWarningsKey);
            if (!string.IsNullOrEmpty(warningsRaw))
            {
                var warnings = JsonSerializer.Deserialize<List<RedownloadWarning>>(warningsRaw, JsonOpts);
                if (warnings is not null)
                {
                    Plugin.RedownloadWarningsBacking = warnings;
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>Persists the last-fix-run summary. Null clears the stored row.</summary>
    /// <param name="value">The summary to persist.</param>
    public static void PersistLastFixRun(FixRunSummary? value)
    {
        var db = _db;
        if (db is null)
        {
            return;
        }

        try
        {
            db.SetState(LastFixRunKey, value is null ? null : JsonSerializer.Serialize(value, JsonOpts));
        }
        catch (Exception)
        {
            // Persistence is best-effort; in-memory copy stays authoritative for this session.
        }
    }

    /// <summary>Persists the redownload-warning list. Empty list stores an empty JSON array.</summary>
    /// <param name="value">The warnings to persist.</param>
    public static void PersistRedownloadWarnings(IReadOnlyList<RedownloadWarning> value)
    {
        var db = _db;
        if (db is null)
        {
            return;
        }

        try
        {
            db.SetState(RedownloadWarningsKey, JsonSerializer.Serialize(value, JsonOpts));
        }
        catch (Exception)
        {
        }
    }
}

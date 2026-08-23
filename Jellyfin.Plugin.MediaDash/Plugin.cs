using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.MediaDash.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaDash;

/// <summary>
/// The MediaDash plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // Paths that a previous scanner in this run has flagged for deletion (Duplicate losers,
    // MalwareRisk executables, OrphanedDebris). Downstream probing scanners consult this via
    // Plugin.IsDoomed to skip the ffprobe/decode entirely.
    private static readonly System.Collections.Generic.HashSet<string> DoomedPaths
        = new(System.StringComparer.OrdinalIgnoreCase);

    private static readonly object DoomedLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "MediaDash";

    /// <inheritdoc />
    public override string Description => "Whole-library housekeeping for Jellyfin covering movies, TV, music, audiobooks, books and comics. Finds duplicates, broken files, oversized encodes, unwanted or missing language tracks, misplaced files, ungrouped media, corrupt artwork, suspicious executables, orphaned metadata, stale content and space-heavy trickplay — then fixes them safely. Dry-run mode and a recycle bin protect your media by default.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("38bdb090-b763-4294-934b-b54ade4d9d6d");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets or sets the path of the file the currently-running scan or fix is working on, or null when idle.
    /// Read by the /Status endpoint so the dashboard can show what's happening under the progress bar.
    /// Never a load-bearing field — best-effort human readout only.
    /// </summary>
    public static string? CurrentActivity { get; set; }

    /// <summary>
    /// Gets or sets the display label of the scanner or fixer currently owning <see cref="CurrentActivity"/>
    /// (e.g. "DuplicateScanner", "TrackFixer"). Rendered next to the path as "{Label} — {Path}" so users
    /// can see *what* is running, not just what file it's chewing on. Null when idle.
    /// </summary>
    public static string? CurrentActivityLabel { get; set; }

    /// <summary>
    /// Gets or sets the summary of the most-recently-completed fix run. The dashboard compares
    /// <see cref="Api.FixRunSummary.FinishedAtUtc"/> to what it last saw and pops an alert whenever a fresh
    /// run finished with failures — otherwise a fast all-failed run just flashes the progress bar and vanishes.
    /// Persisted via <see cref="Api.PluginState"/> so a mid-day restart doesn't lose the most recent run.
    /// </summary>
    public static Api.FixRunSummary? LastFixRun
    {
        get => LastFixRunBacking;
        set
        {
            LastFixRunBacking = value;
            Api.PluginState.PersistLastFixRun(value);
        }
    }

    /// <summary>Gets or sets the backing field for <see cref="LastFixRun"/> so PluginState.Attach can rehydrate without triggering another persist.</summary>
    internal static Api.FixRunSummary? LastFixRunBacking { get; set; }

    /// <summary>
    /// Gets or sets the most recent redownload/restore warnings found by <see cref="Api.RedownloadDetector"/>.
    /// Refreshed at the end of every scan. Persisted so the dashboard banner survives restarts.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<Api.RedownloadWarning> RedownloadWarnings
    {
        get => RedownloadWarningsBacking;
        set
        {
            RedownloadWarningsBacking = value;
            Api.PluginState.PersistRedownloadWarnings(value);
        }
    }

    /// <summary>Gets or sets the backing field for <see cref="RedownloadWarnings"/> so PluginState.Attach can rehydrate without triggering another persist.</summary>
    internal static System.Collections.Generic.IReadOnlyList<Api.RedownloadWarning> RedownloadWarningsBacking { get; set; } = System.Array.Empty<Api.RedownloadWarning>();

    /// <summary>
    /// Returns true when a previous scanner has already flagged this file for deletion, so probing
    /// scanners can skip it. Thread-safe. See <see cref="MarkDoomed"/>, <see cref="ClearDoomed"/>.
    /// </summary>
    /// <param name="path">The file path to check.</param>
    /// <returns>True if the path is already marked for deletion.</returns>
    internal static bool IsDoomed(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        lock (DoomedLock)
        {
            return DoomedPaths.Contains(path);
        }
    }

    /// <summary>Marks a file path as doomed so downstream probing scanners will skip it.</summary>
    /// <param name="path">The file path to add.</param>
    internal static void MarkDoomed(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (DoomedLock)
        {
            DoomedPaths.Add(path);
        }
    }

    /// <summary>Resets the doomed set — called at the start of each scan run.</summary>
    internal static void ClearDoomed()
    {
        lock (DoomedLock)
        {
            DoomedPaths.Clear();
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                DisplayName = "MediaDash",
                // Jellyfin's dashboard sidebar only supports material-icons for plugin entries — there's no field
                // to point at the logo PNG. auto_fix_high (the magic wand) matches the plugin's "find & fix
                // library problems" purpose more distinctly than the generic dashboard icon.
                MenuIcon = "auto_fix_high"
            }
        ];
    }
}

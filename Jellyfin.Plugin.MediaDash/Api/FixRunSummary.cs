using System;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Result totals for the most-recently-completed fix run, exposed on the /Status response so the dashboard
/// can pop an alert when a run finishes with failures.
/// </summary>
public sealed class FixRunSummary
{
    /// <summary>Gets or sets when the fix run finished.</summary>
    public DateTime FinishedAtUtc { get; set; }

    /// <summary>Gets or sets the number of queued fixes the run attempted.</summary>
    public int Attempted { get; set; }

    /// <summary>Gets or sets the number of fixes that succeeded (not counting dry-run stubs).</summary>
    public int Succeeded { get; set; }

    /// <summary>Gets or sets the number of fixes that were genuine failures — a fixer hit an
    /// error path the scheduler must remember (I/O, ffmpeg process failure, verification failure,
    /// unhandled exception). Excludes <see cref="Skipped"/>, which covers safe refusals and
    /// already-fine outcomes that used to inflate this number.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets the number of fixes the run touched but that didn't apply a change
    /// through no bug of MediaDash's own — the fixer refused a hard precondition (destination
    /// collision, DRM-protected, outside library), the condition self-healed before the fix ran
    /// (Playability "plays fine now"), or nothing needed doing (Quality "already smaller",
    /// EmbeddedCoverArt "cover already exists"). Split out from <see cref="Failed"/> so users
    /// don't read "0 succeeded, 49 failed" and assume the plugin is broken when most of that
    /// 49 was actually safe no-ops.</summary>
    public int Skipped { get; set; }

    /// <summary>Gets or sets the most common failure reason across the run, or null when nothing failed.</summary>
    public string? TopFailureReason { get; set; }

    /// <summary>Gets or sets the number of failures that matched <see cref="TopFailureReason"/>.</summary>
    public int TopFailureCount { get; set; }

    /// <summary>Gets or sets the number of queued issues the run skipped because their library
    /// is not currently enabled in Settings → Libraries. Non-blocking (the items stay queued),
    /// but surfaced on the dashboard so a "0 fixed" outcome has a visible explanation.</summary>
    public int SkippedLibraryDisabled { get; set; }

    /// <summary>Gets or sets why no fixes were attempted, including the next step for the user.</summary>
    public string? NoWorkReason { get; set; }
}

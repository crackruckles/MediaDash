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

    /// <summary>Gets or sets the number of fixes that failed.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets the most common failure reason across the run, or null when nothing failed.</summary>
    public string? TopFailureReason { get; set; }

    /// <summary>Gets or sets the number of failures that matched <see cref="TopFailureReason"/>.</summary>
    public int TopFailureCount { get; set; }
}

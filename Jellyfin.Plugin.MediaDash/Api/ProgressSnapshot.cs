namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Lightweight live-progress payload for the Overview poll during an active scan/fix. The full
/// <see cref="StatusResponse"/> aggregates ~10 DB queries plus drive scan, SMART and system-stats
/// sampling on every call; on loaded servers that competes with scan DB writes and leaves the
/// Overview showing stale or missing progress. This endpoint returns only the fields that change
/// per tick and does zero DB work. Full <see cref="StatusResponse"/> still polls on a slower cadence
/// so counts, drives and recycle-bin stay fresh.
/// </summary>
public sealed class ProgressSnapshot
{
    /// <summary>Gets or sets a value indicating whether a scan is currently running.</summary>
    public bool IsScanning { get; set; }

    /// <summary>Gets or sets the running scan's progress percentage.</summary>
    public double? ScanProgress { get; set; }

    /// <summary>Gets or sets a value indicating whether a fix run is currently executing.</summary>
    public bool IsFixing { get; set; }

    /// <summary>Gets or sets the running fix run's progress percentage.</summary>
    public double? FixProgress { get; set; }

    /// <summary>Gets or sets how many of the scanner stages have completed.</summary>
    public int? ScanScannersDone { get; set; }

    /// <summary>Gets or sets the total number of scanner stages in the current run.</summary>
    public int? ScanScannersTotal { get; set; }

    /// <summary>Gets or sets the path currently being processed.</summary>
    public string? CurrentActivity { get; set; }

    /// <summary>Gets or sets the display label of the scanner/fixer owning <see cref="CurrentActivity"/>.</summary>
    public string? CurrentActivityLabel { get; set; }

    /// <summary>Gets or sets the per-file progress fraction (0..1) reported by the current fixer, if any.</summary>
    public double? CurrentOperationProgress { get; set; }

    /// <summary>Gets or sets the human-readable pause/no-work reason, if any.</summary>
    public string? FixPauseReason { get; set; }
}

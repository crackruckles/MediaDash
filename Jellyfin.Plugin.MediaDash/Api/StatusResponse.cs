using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Dashboard status payload.
/// </summary>
public sealed class StatusResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether a scan is currently running.
    /// </summary>
    public bool IsScanning { get; set; }

    /// <summary>
    /// Gets or sets the running scan's progress percentage, when scanning.
    /// </summary>
    public double? ScanProgress { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a fix run is currently executing.
    /// </summary>
    public bool IsFixing { get; set; }

    /// <summary>
    /// Gets or sets the running fix run's progress percentage, when fixing.
    /// </summary>
    public double? FixProgress { get; set; }

    /// <summary>
    /// Gets or sets the total number of open issues.
    /// </summary>
    public int OpenIssueTotal { get; set; }

    /// <summary>
    /// Gets or sets the free bytes across the drives that hold the libraries.
    /// </summary>
    public long FreeDiskBytes { get; set; }

    /// <summary>
    /// Gets or sets the total bytes across the drives that hold the libraries.
    /// </summary>
    public long TotalDiskBytes { get; set; }

    /// <summary>
    /// Gets or sets when issues were last detected (UTC).
    /// </summary>
    public DateTime? LastScanUtc { get; set; }

    /// <summary>
    /// Gets or sets the total bytes reclaimable across all open issues.
    /// </summary>
    public long TotalPotentialSavings { get; set; }

    /// <summary>
    /// Gets or sets the per-type issue counts.
    /// </summary>
    public IReadOnlyList<TypeCount> Counts { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of issues the next fix run would actually touch —
    /// currently-queued items plus detected items whose type is set to Automatic.
    /// Zero means "Run fixes now" would be a no-op.
    /// </summary>
    public int PendingFixCount { get; set; }

    /// <summary>
    /// Gets or sets per-drive free/total bytes for each drive that hosts a library folder.
    /// </summary>
    public IReadOnlyList<DriveUsage> Drives { get; set; } = [];

    /// <summary>
    /// Gets or sets the file currently being scanned or fixed, or null when idle.
    /// Purely informational (surfaced under the progress bar); the frontend must not treat it as authoritative.
    /// </summary>
    public string? CurrentActivity { get; set; }

    /// <summary>
    /// Gets or sets a live resource snapshot for the Jellyfin process (CPU / RAM / GPU).
    /// </summary>
    public SystemStats? System { get; set; }

    /// <summary>Gets or sets the effective recycle bin path (either configured or default).</summary>
    public string? RecycleBinPath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the recycle bin lives on a different volume than any library folder.
    /// When true the plugin has to copy+delete on recycle instead of rename, needing free space on the bin's volume.
    /// </summary>
    public bool RecycleBinCrossVolume { get; set; }
}

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
}

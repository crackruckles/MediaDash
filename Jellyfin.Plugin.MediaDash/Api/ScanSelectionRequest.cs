using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Body for the "run selected scans" endpoint (right-click / long-press on Scan now).
/// </summary>
public sealed class ScanSelectionRequest
{
    /// <summary>
    /// Gets or sets the issue type names whose scanners should run (for example "Ungrouped").
    /// </summary>
    public IReadOnlyList<string> Types { get; set; } = System.Array.Empty<string>();
}

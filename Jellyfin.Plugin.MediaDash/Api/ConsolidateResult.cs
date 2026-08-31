namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Result of a consolidation run — how many batches moved, how many were skipped for safety,
/// and how many bytes crossed. Displayed as an inline confirmation on the Recycle bin tab.
/// </summary>
public sealed class ConsolidateResult
{
    /// <summary>Gets or sets the number of batches successfully moved into the current bin.</summary>
    public int BatchesMoved { get; set; }

    /// <summary>Gets or sets the number of batches skipped (name collision at target, non-batch-shaped folder, etc.).</summary>
    public int BatchesSkipped { get; set; }

    /// <summary>Gets or sets the total bytes moved.</summary>
    public long BytesMoved { get; set; }

    /// <summary>Gets or sets a plain-language explanation when the operation partially failed.</summary>
    public string? Warning { get; set; }
}

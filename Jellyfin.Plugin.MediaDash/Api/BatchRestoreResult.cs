using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Response body for a multi-item restore via <c>POST /RecycleBin/Items/Restore</c> with
/// <see cref="BinRestoreRequest.BinPaths"/>. Every requested bin path becomes one entry in
/// <see cref="Results"/>; entries fail independently so a bad path in the middle doesn't
/// short-circuit the rest. <see cref="Successes"/> + <see cref="Failures"/> = <c>Results.Count</c>.
/// </summary>
public sealed class BatchRestoreResult
{
    /// <summary>Gets or sets the count of entries that restored successfully.</summary>
    public int Successes { get; set; }

    /// <summary>Gets or sets the count of entries that failed (bad path, no manifest, outside library, IO error).</summary>
    public int Failures { get; set; }

    /// <summary>Gets the per-entry outcomes in the order the request supplied them.</summary>
    public Collection<BatchRestoreEntry> Results { get; } = new();
}

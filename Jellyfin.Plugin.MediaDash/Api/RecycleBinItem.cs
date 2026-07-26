using System;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// One file held in the recycle bin.
/// </summary>
public sealed class RecycleBinItem
{
    /// <summary>
    /// Gets or sets the file name.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets when the file was recycled (UTC).
    /// </summary>
    public DateTime RecycledAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the id of the history entry that recycled this file. Null when the bin file has no
    /// matching history row - either it was recycled by a pre-history build, or the row was purged.
    /// The UI uses this to decide whether the per-item Restore button is actionable.
    /// </summary>
    public long? HistoryId { get; set; }

    /// <summary>
    /// Gets or sets the original path this file would be restored to. Null when there's no history
    /// entry to derive it from.
    /// </summary>
    public string? OriginalPath { get; set; }
}

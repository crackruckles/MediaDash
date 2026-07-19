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
}

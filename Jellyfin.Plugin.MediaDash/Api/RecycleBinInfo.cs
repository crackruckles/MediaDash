namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Recycle bin contents summary.
/// </summary>
public sealed class RecycleBinInfo
{
    /// <summary>
    /// Gets or sets the number of files currently held.
    /// </summary>
    public int FileCount { get; set; }

    /// <summary>
    /// Gets or sets the total size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }
}

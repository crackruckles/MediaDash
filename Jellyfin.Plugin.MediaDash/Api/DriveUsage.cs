namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Free/used bytes for one drive that holds a library folder.
/// </summary>
public sealed class DriveUsage
{
    /// <summary>Gets or sets the drive's root path (e.g., "C:\\" or "/mnt/media").</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets the free bytes on the drive.</summary>
    public long FreeBytes { get; set; }

    /// <summary>Gets or sets the total bytes on the drive.</summary>
    public long TotalBytes { get; set; }
}

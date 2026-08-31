namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Free/total space on the volume that owns a candidate recycle bin path. Used by the save-time
/// validation ("does this path have enough room for a bin?") and to seed the pause-cap default.
/// </summary>
public sealed class RecycleBinDiskInfo
{
    /// <summary>Gets or sets the path that was probed (may be a parent when the exact path doesn't exist yet).</summary>
    public string PathProbed { get; set; } = string.Empty;

    /// <summary>Gets or sets the total volume capacity in bytes.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the current free-space on the volume in bytes.</summary>
    public long FreeBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether the volume has at least 5 GB free — the save-time floor. When false, the frontend refuses the save and shows the shortcut back to the Recycle bin settings.</summary>
    public bool MeetsFiveGbMinimum { get; set; }

    /// <summary>Gets or sets the suggested value for RecycleBinPauseFixesAtGb: TotalBytes/GiB - 3, clamped to at least 1 so the setting isn't accidentally saved as 0 (which disables it).</summary>
    public int SuggestedPauseCapGb { get; set; }

    /// <summary>Gets or sets a plain-language explanation when the volume isn't a viable bin location — e.g. path doesn't resolve to a real drive, or 0-byte volume (network share offline).</summary>
    public string? Warning { get; set; }
}

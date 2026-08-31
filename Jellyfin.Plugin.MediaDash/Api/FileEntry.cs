using System;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// One row in a file browser directory listing.
/// </summary>
public sealed class FileEntry
{
    /// <summary>Gets or sets the entry name (no path components).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the entry is a directory.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>Gets or sets the file size in bytes; 0 for directories.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets when the entry was last modified (UTC).</summary>
    public DateTime ModifiedUtc { get; set; }

    /// <summary>
    /// Gets or sets an optional entry kind hint for the UI. Empty for regular library entries;
    /// <c>"recycle-bin"</c> for the recycle-bin shortcut and <c>"logs"</c> for the Jellyfin log
    /// directory shortcut, so the UI can label and style each distinctly (icon, badge, friendly label
    /// instead of the raw path).
    /// </summary>
    public string Kind { get; set; } = string.Empty;
}

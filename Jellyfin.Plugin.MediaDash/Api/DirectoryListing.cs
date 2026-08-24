using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Response for the file browser list endpoint.
/// </summary>
public sealed class DirectoryListing
{
    /// <summary>Gets or sets the absolute path being listed, or empty when listing library roots.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the parent path, or null when at a library root or the pseudo-root.</summary>
    public string? Parent { get; set; }

    /// <summary>Gets or sets a value indicating whether this listing is the pseudo-root that lists library folders.</summary>
    public bool IsRoot { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this listing is inside the recycle-bin root. When true,
    /// the UI switches to read-only mode: no Mkdir / Upload buttons and no per-row rename / move / copy /
    /// delete actions. Restoration and empty are handled from the dedicated Recycle bin tab.
    /// </summary>
    public bool IsRecycleBin { get; set; }

    /// <summary>Gets or sets the entries in this directory.</summary>
    public IReadOnlyList<FileEntry> Entries { get; set; } = [];
}

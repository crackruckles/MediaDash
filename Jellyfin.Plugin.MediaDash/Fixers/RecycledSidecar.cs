namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// One additional file recycled during a fix beyond the primary file. Track fixes remove
/// external subtitle sidecars alongside the video; cover-art strips move each pre-strip
/// audio original aside. Each entry produces its own history row so the Restore button
/// appears next to it in the bin instead of a dead-end "no history" label.
/// </summary>
public sealed class RecycledSidecar
{
    /// <summary>Gets or sets the original path of the recycled sidecar.</summary>
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the resulting path inside the recycle bin.</summary>
    public string RecyclePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the plain-language description used for the sidecar's history row.</summary>
    public string Action { get; set; } = string.Empty;
}

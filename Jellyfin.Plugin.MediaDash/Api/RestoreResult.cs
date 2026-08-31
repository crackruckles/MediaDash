namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Response body for POST History/{id}/Restore. Non-destructive default may land the file at a
/// sibling path (with a -restored suffix) when the original slot is already occupied.
/// </summary>
public sealed class RestoreResult
{
    /// <summary>
    /// Gets or sets the path the file was restored to. Same as the history entry's Path in the
    /// normal case; a -restored-suffixed sibling path when the original was still occupied.
    /// </summary>
    public string RestoredTo { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a -restored suffix was appended because a file
    /// already existed at the original path. UI uses this to render the appropriate confirmation.
    /// </summary>
    public bool Suffixed { get; set; }
}

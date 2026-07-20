namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>Request body for the file-browser rename endpoint.</summary>
public sealed class RenameRequest
{
    /// <summary>Gets or sets the current path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the new leaf name.</summary>
    public string NewName { get; set; } = string.Empty;
}

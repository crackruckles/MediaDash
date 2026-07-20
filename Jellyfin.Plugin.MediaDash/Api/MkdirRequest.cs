namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>Request body for the file-browser mkdir endpoint.</summary>
public sealed class MkdirRequest
{
    /// <summary>Gets or sets the parent directory (inside a library).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder name to create.</summary>
    public string Name { get; set; } = string.Empty;
}

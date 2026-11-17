namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>One explicitly selected source and destination in a file-browser move.</summary>
public sealed class FileMoveSelection
{
    /// <summary>Gets or sets the source path.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Gets or sets the destination path.</summary>
    public string To { get; set; } = string.Empty;
}

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>Request body for the file-browser move and copy endpoints.</summary>
public sealed class MoveOrCopyRequest
{
    /// <summary>Gets or sets the source path.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Gets or sets the destination path.</summary>
    public string To { get; set; } = string.Empty;
}

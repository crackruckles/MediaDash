namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>Request body for the file-browser delete endpoint.</summary>
public sealed class DeleteRequest
{
    /// <summary>Gets or sets the path to send to the recycle bin.</summary>
    public string Path { get; set; } = string.Empty;
}

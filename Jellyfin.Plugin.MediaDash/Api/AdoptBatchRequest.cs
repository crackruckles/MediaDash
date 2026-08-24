namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>Request body for the recycle-bin adopt-batch endpoint.</summary>
public sealed class AdoptBatchRequest
{
    /// <summary>Gets or sets the absolute path of the batch directory to adopt.</summary>
    public string Path { get; set; } = string.Empty;
}

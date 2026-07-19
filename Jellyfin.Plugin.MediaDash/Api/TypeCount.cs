namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Issue count for one issue type.
/// </summary>
public sealed class TypeCount
{
    /// <summary>
    /// Gets or sets the issue type name.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of open issues.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets the bytes reclaimable for this type.
    /// </summary>
    public long PotentialSavings { get; set; }
}

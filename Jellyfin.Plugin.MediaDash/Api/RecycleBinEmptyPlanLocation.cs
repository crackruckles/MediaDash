namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>One location in a <see cref="RecycleBinEmptyPlan"/>.</summary>
public sealed class RecycleBinEmptyPlanLocation
{
    /// <summary>Gets or sets the absolute path of the recycle root.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of batch folders that would be deleted here.</summary>
    public int BatchCount { get; set; }

    /// <summary>Gets or sets the total size of those batch folders.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the currently configured recycle bin.</summary>
    public bool IsCurrent { get; set; }
}

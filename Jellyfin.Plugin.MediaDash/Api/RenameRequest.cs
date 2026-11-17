namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>Request body for the file-browser rename endpoint.</summary>
public sealed class RenameRequest
{
    /// <summary>Gets or sets the current path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the new leaf name.</summary>
    public string NewName { get; set; } = string.Empty;

    /// <summary>Gets or sets the fingerprint of the complete preview the administrator approved.</summary>
    public string? PlanFingerprint { get; set; }

    /// <summary>Gets or sets a value indicating whether to preview without changing any files.</summary>
    public bool PreviewOnly { get; set; }
}

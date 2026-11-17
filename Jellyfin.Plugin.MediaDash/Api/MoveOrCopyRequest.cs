using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>Request body for the file-browser move and copy endpoints.</summary>
public sealed class MoveOrCopyRequest
{
    /// <summary>Gets or sets the source path.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Gets or sets the destination path.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Gets or sets the complete selection for a bulk move preview.</summary>
    public IReadOnlyList<FileMoveSelection> Selections { get; set; } = [];

    /// <summary>Gets or sets the fingerprint of the complete preview the administrator approved.</summary>
    public string? PlanFingerprint { get; set; }

    /// <summary>Gets or sets a value indicating whether to preview without changing any files.</summary>
    public bool PreviewOnly { get; set; }
}

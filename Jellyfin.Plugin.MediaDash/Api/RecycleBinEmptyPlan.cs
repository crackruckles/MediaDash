using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// What "empty every recycle bin" would delete: the current bin plus older bins MediaDash used
/// (a previous bin path, the default bin, batches from older MediaDash versions). The Recycle bin
/// tab shows this before asking whether to empty everything or only the current bin.
/// </summary>
public sealed class RecycleBinEmptyPlan
{
    /// <summary>Gets or sets every location that holds recycled files MediaDash created.</summary>
    public IReadOnlyList<RecycleBinEmptyPlanLocation> Locations { get; set; } = [];

    /// <summary>Gets or sets the number of batch folders across all locations.</summary>
    public int TotalBatches { get; set; }

    /// <summary>Gets or sets the total size across all locations.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the number of batch folders a current-bin-only empty removes (older-format batches in the current folder are not included).</summary>
    public int CurrentBinBatches { get; set; }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Fixers;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>The current complete file-browser move, without filesystem identity or permission internals.</summary>
public sealed class FileMovePreviewDto
{
    /// <summary>Gets or sets the fingerprint required to approve this exact preview.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether execution will leave all files in place.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets readable reasons preventing execution.</summary>
    public IReadOnlyList<string> BlockingReasons { get; set; } = [];

    /// <summary>Gets or sets every selected and related payload location.</summary>
    public IReadOnlyList<FileMoveSelection> Paths { get; set; } = [];

    internal static FileMovePreviewDto FromPlan(MediaMovePlan plan) => new()
    {
        Fingerprint = plan.Fingerprint,
        BlockingReasons = plan.BlockingReasons,
        Paths = plan.Entries.SelectMany(entry => entry.Inventory.Select(payload => new FileMoveSelection
        {
            From = payload.RelativePath.Length == 0 ? entry.Source : Path.Combine(entry.Source, payload.RelativePath),
            To = payload.RelativePath.Length == 0 ? entry.Destination : Path.Combine(entry.Destination, payload.RelativePath)
        })).ToArray()
    };
}

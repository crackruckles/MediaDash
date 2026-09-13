using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Overview payload for the "Broken files repaired" panel — headline totals plus
/// per-rung breakdown of the Playability repair ladder's rescues.
/// </summary>
public sealed class RepairSummary
{
    /// <summary>Gets or sets the count of successful repairs in the last 30 days.</summary>
    public int Count30d { get; set; }

    /// <summary>Gets or sets the pre-repair bytes rescued in the last 30 days (files that would otherwise have been deleted).</summary>
    public long SavedBytes30d { get; set; }

    /// <summary>Gets or sets the lifetime count of successful repairs.</summary>
    public int CountLifetime { get; set; }

    /// <summary>Gets or sets the lifetime pre-repair bytes rescued.</summary>
    public long SavedBytesLifetime { get; set; }

    /// <summary>
    /// Gets or sets the per-rung breakdown over the last 30 days — one row per rung
    /// (quick remux / dropped broken streams / container coerce / video re-encoded)
    /// that has produced any successful repair in the window. Sorted so the biggest
    /// contributors surface first.
    /// </summary>
    public IReadOnlyList<RepairRungTotal> Rungs30d { get; set; } = System.Array.Empty<RepairRungTotal>();
}

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>One rung of the Playability repair ladder's tally over a time window.</summary>
public sealed class RepairRungTotal
{
    /// <summary>Gets or sets the rung key: "remux", "drop", "coerce", "reencode", or "other".</summary>
    public string Rung { get; set; } = string.Empty;

    /// <summary>Gets or sets how many files this rung rescued.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets the pre-repair size sum for those rescues.</summary>
    public long SavedBytes { get; set; }
}

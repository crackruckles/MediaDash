namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>Result of an integrity check against a book file.</summary>
/// <param name="Ok">True when the container parsed cleanly.</param>
/// <param name="Reason">Human-readable reason when the check failed, else null.</param>
public readonly record struct BookProbeResult(bool Ok, string? Reason);

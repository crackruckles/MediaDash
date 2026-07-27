namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>Cached result of a non-ffprobe format integrity check (books, comics).</summary>
/// <param name="Ok">True when the file's container parsed cleanly.</param>
/// <param name="Reason">Human-readable failure reason when <paramref name="Ok"/> is false, else null.</param>
public readonly record struct FormatProbeResult(bool Ok, string? Reason);

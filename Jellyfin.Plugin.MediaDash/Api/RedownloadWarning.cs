using System;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// One case where a file MediaDash successfully re-encoded has been replaced by something roughly the
/// size of the original — the classic Sonarr/Radarr redownload loop, but also fires on user-initiated
/// restores from the recycle bin.
/// </summary>
public sealed class RedownloadWarning
{
    /// <summary>Gets or sets the current file's path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the current file size in bytes.</summary>
    public long CurrentBytes { get; set; }

    /// <summary>Gets or sets the original file size in bytes (from the recycle-bin copy).</summary>
    public long OriginalBytes { get; set; }

    /// <summary>Gets or sets the UTC time the successful re-encode was recorded.</summary>
    public DateTime FixedAtUtc { get; set; }

    /// <summary>Gets or sets the path of the original file still sitting in the recycle bin.</summary>
    public string RecyclePath { get; set; } = string.Empty;
}

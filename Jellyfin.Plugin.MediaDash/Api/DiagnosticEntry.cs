using System;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>One recorded diagnostic event.</summary>
public sealed class DiagnosticEntry
{
    /// <summary>Gets or sets when the event was recorded (UTC).</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>Gets or sets a short source label (e.g., "SystemStats.Linux", "PlayabilityScanner").</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable description of the event.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets how many times this same (source, message) has fired in a row. 1 = single occurrence.</summary>
    public int Count { get; set; } = 1;

    /// <summary>Gets or sets when the most recent occurrence fired, when this entry represents multiple hits.</summary>
    public DateTime LastAtUtc { get; set; }
}

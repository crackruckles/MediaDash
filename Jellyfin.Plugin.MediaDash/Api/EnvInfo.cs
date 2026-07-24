using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Snapshot of environment info used by the Errors tab's "Copy diagnostics" button and by the wizard's
/// missing-subtitle step to warn when no subtitle provider is installed.
/// </summary>
public sealed class EnvInfo
{
    /// <summary>Gets or sets the MediaDash plugin version.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin server version, when detectable.</summary>
    public string JellyfinVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets a human-readable OS description (Windows/Linux/macOS build).</summary>
    public string Os { get; set; } = string.Empty;

    /// <summary>Gets or sets the .NET runtime description.</summary>
    public string Framework { get; set; } = string.Empty;

    /// <summary>Gets or sets the names of subtitle providers currently registered in Jellyfin. Empty when none are installed.</summary>
    public IReadOnlyList<string> SubtitleProviders { get; set; } = [];
}

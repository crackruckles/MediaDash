namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Per-library read/write access probe result. Populated by the pre-flight endpoint that first-run uses
/// so the user finds ownership/ACL problems before approving anything, not after.
/// </summary>
public sealed class LibraryAccessResult
{
    /// <summary>Gets or sets the library's display name in Jellyfin.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the library folder that was probed.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the folder listed successfully (read access).</summary>
    public bool CanRead { get; set; }

    /// <summary>Gets or sets a value indicating whether a probe file could be created and deleted (write access).</summary>
    public bool CanWrite { get; set; }

    /// <summary>Gets or sets a plain-language explanation when either check failed; null on success.</summary>
    public string? Error { get; set; }
}

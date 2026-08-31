namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Body for POST /RecycleBin/Consolidate. Names one legacy bin root to fold into the currently
/// configured one so users don't have to manage multiple locations by hand.
/// </summary>
public sealed class ConsolidateRequest
{
    /// <summary>Gets or sets the absolute path of the source bin root to drain.</summary>
    public string SourceRoot { get; set; } = string.Empty;
}

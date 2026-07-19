namespace Jellyfin.Plugin.MediaDash.Configuration;

/// <summary>
/// What happens to a file (or replaced original) when a fix removes it.
/// </summary>
public enum DisposalMethod
{
    /// <summary>Move to MediaDash's recycle bin, recoverable until retention expires.</summary>
    RecycleBin = 0,

    /// <summary>Delete permanently, immediately.</summary>
    Permanent = 1
}

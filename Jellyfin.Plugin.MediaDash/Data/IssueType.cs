namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// The category of problem a scanner detected.
/// </summary>
public enum IssueType
{
    /// <summary>Another copy of the same movie or episode exists.</summary>
    Duplicate = 0,

    /// <summary>The file is broken or cannot be played.</summary>
    Playability = 1,

    /// <summary>The file exceeds the configured quality ceiling.</summary>
    Quality = 2,

    /// <summary>The file contains subtitle tracks in unwanted languages.</summary>
    SubtitleLanguage = 3,

    /// <summary>The file contains audio tracks in unwanted languages.</summary>
    AudioLanguage = 4
}

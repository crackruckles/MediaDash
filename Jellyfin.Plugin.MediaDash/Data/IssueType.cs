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
    AudioLanguage = 4,

    /// <summary>The file sits in the wrong library kind (a movie in the TV folder, or a TV episode in the Movies folder).</summary>
    Misplaced = 5,

    /// <summary>The file has no subtitle track in any of the wanted languages (embedded or external).</summary>
    MissingSubtitles = 6,

    /// <summary>The file has existed on the server longer than the stale threshold and no user has played it within that window.</summary>
    Stale = 7,

    /// <summary>Local artwork (poster / backdrop / thumb) is zero-byte, truncated, or fails to decode.</summary>
    CorruptArtwork = 8,

    /// <summary>Executable or script file sitting inside a media library folder — nothing legitimate should ship there, so treat as potential malware.</summary>
    MalwareRisk = 9,

    /// <summary>The file or containing folder is not filed under a per-title (or per-franchise) parent folder inside its library root.</summary>
    Ungrouped = 10
}

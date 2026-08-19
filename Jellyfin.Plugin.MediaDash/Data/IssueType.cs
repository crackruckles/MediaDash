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
    Ungrouped = 10,

    /// <summary>Trickplay sprite thumbnails are still raw JPG and can be re-encoded as WebP (renamed .jpg so the client still fetches them) for a large disk-space reduction with no client-side change.</summary>
    LargeTrickplay = 11,

    /// <summary>An .ass/.ssa sidecar subtitle carries embedded fonts that no style or override references (fansubs often bundle many), reclaimable by rewriting the file. Also produced when a forced-font override is set and any embedded fonts exist.</summary>
    SubtitleFonts = 12,

    /// <summary>A folder, subtitle sidecar, trickplay folder, or Jellyfin metadata folder whose parent (video or library item) no longer exists. Debris left by moves / deletions / re-imports; safe to remove because the reference point is gone.</summary>
    OrphanedDebris = 13,

    /// <summary>An <c>.nfo</c> metadata sidecar that's zero-byte, not valid XML, or missing a recognised root element (movie/tvshow/episode/musicvideo). Broken NFO files stop Jellyfin from re-fetching metadata cleanly; deleting them lets the next scan try again.</summary>
    CorruptNfo = 14,

    /// <summary>A source file that keeps having to be transcoded on the fly (client can't direct-play it). A one-off re-encode to a compatible codec / bitrate turns future plays into direct-play, saving CPU on every play.</summary>
    HeavyTranscode = 15,

    /// <summary>A source file whose most recent transcode attempt failed (non-zero ffmpeg exit or a "Conversion failed" tail marker). A targeted re-encode with the plugin's own settings usually succeeds where the on-the-fly attempt didn't.</summary>
    FailedTranscode = 16,

    /// <summary>A music or audiobook folder whose audio files carry an embedded cover image but no folder-level <c>cover.jpg</c> / <c>folder.jpg</c> exists. Extracting a shared folder cover once (and optionally stripping the redundant per-file copies) is a big disk-space win and makes Jellyfin use the folder image directly.</summary>
    EmbeddedCoverArt = 17
}

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Canonical set of external subtitle file extensions the plugin treats as sidecars. Matched against
/// Jellyfin's own <c>Emby.Naming.Common.NamingOptions.SubtitleFileExtensions</c> plus <c>.sup</c>
/// (Blu-ray PGS) which Jellyfin recognises via its media probe. Keep in sync with Jellyfin upstream —
/// if Jellyfin indexes a file as a subtitle, MediaDash must too, otherwise the orphan-cleanup pass
/// wrongly flags it (or the sorter leaves it behind on a move).
/// </summary>
public static class SubtitleFormats
{
    /// <summary>Case-insensitive set of recognised sidecar extensions, dot included.</summary>
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", // SubRip — ubiquitous default.
        ".ass", // Advanced SubStation Alpha — fansub / styled subs.
        ".ssa", // SubStation Alpha — .ass predecessor.
        ".vtt", // WebVTT — streaming / HTML5 video default.
        ".sub", // MicroDVD / SubViewer / VobSub raw payload.
        ".idx", // VobSub index (paired with .sub).
        ".sup", // PGS / HDMV — Blu-ray bitmap subs.
        ".smi", // SAMI — Windows Media era, still common in Korean releases.
        ".sami", // SAMI long-form extension variant.
        ".mks" // Matroska subtitles container (video-less .mkv with subtitle tracks only).
    };
}

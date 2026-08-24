using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Canonical set of media file extensions the plugin recognises. Video and Audio mirror Jellyfin's
/// own <c>Emby.Naming.Common.NamingOptions</c> lists so any file Jellyfin indexes is also visible
/// to MediaDash's filesystem-walking passes (orphan cleanup, empty-folder detection, duplicate
/// signals). Books / Comics / Pictures aren't part of Jellyfin's naming options — those live on
/// item resolvers — so their sets are curated here.
/// Keep in sync with upstream: if Jellyfin adds an extension, add it here and every consumer picks
/// it up. Subtitles live in <see cref="SubtitleFormats"/>.
/// </summary>
public static class MediaFormats
{
    /// <summary>Every video extension in Jellyfin's <c>NamingOptions.VideoFileExtensions</c>.</summary>
    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".001", ".3g2", ".3gp", ".amv", ".asf", ".asx", ".avi", ".bin", ".bivx", ".divx",
        ".dv", ".dvr-ms", ".f4v", ".fli", ".flv", ".ifo", ".img", ".iso", ".m2t", ".m2ts",
        ".m2v", ".m4v", ".mkv", ".mk3d", ".mov", ".mp4", ".mpe", ".mpeg", ".mpg", ".mts",
        ".mxf", ".nrg", ".nsv", ".nuv", ".ogm", ".ogv", ".pva", ".qt", ".rec", ".rm",
        ".rmvb", ".strm", ".svq3", ".tp", ".ts", ".ty", ".viv", ".vob", ".vp3", ".webm",
        ".wmv", ".wtv", ".xvid"
    };

    /// <summary>Every audio extension in Jellyfin's <c>NamingOptions.AudioFileExtensions</c>.</summary>
    public static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".669", ".3gp", ".aa", ".aac", ".aax", ".ac3", ".act", ".adp", ".adplug", ".adx",
        ".afc", ".amf", ".aif", ".aifc", ".aiff", ".alac", ".amr", ".ape", ".ast", ".au",
        ".awb", ".cda", ".cue", ".dmf", ".dsf", ".dsm", ".dsp", ".dts", ".dvf", ".eac3",
        ".ec3", ".far", ".flac", ".gdm", ".gsm", ".gym", ".hps", ".imf", ".it", ".m15",
        ".m4a", ".m4b", ".mac", ".med", ".mka", ".mmf", ".mod", ".mogg", ".mp2", ".mp3",
        ".mpa", ".mpc", ".mpp", ".mp+", ".msv", ".nmf", ".nsf", ".nsv", ".oga", ".ogg",
        ".okt", ".opus", ".pls", ".ra", ".rf64", ".rm", ".s3m", ".sfx", ".shn", ".sid",
        ".stm", ".strm", ".ult", ".uni", ".vox", ".wav", ".wma", ".wv", ".xm", ".xsp",
        ".ymf"
    };

    /// <summary>Ebook extensions. Jellyfin's book resolver accepts these plus PDFs.</summary>
    public static readonly HashSet<string> Books = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".mobi", ".azw", ".azw3", ".pdf", ".djvu"
    };

    /// <summary>Comic archive extensions. Currently indexed as Book items by Jellyfin.</summary>
    public static readonly HashSet<string> Comics = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cbz", ".cbr", ".cb7"
    };

    /// <summary>Photo extensions Jellyfin's photo resolver recognises.</summary>
    public static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".gif", ".bmp", ".tif", ".tiff",
        ".raw", ".nef", ".cr2", ".arw", ".dng"
    };

    /// <summary>
    /// Every extension the plugin treats as user media (empty-folder pass uses this to distinguish
    /// "genuinely empty" folders from folders that just don't hold video). Precomputed union of the
    /// per-kind sets so callers don't rebuild it per file check.
    /// </summary>
    public static readonly HashSet<string> All = new(
        Video.Concat(Audio).Concat(Books).Concat(Comics).Concat(Pictures),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Video-classified extensions that ffmpeg / ffprobe can't decode as an ordinary stream: disc
    /// images that need a mounted VOB path (.iso, .img, .nrg), Jellyfin stub files pointing elsewhere
    /// (.strm text URLs, .disc folder pointers), DVD descriptors (.ifo), split-archive markers (.001)
    /// and ambiguous raw payloads (.bin — could be VCD raw, could be anything). Jellyfin resolves
    /// these via specialised demuxers or by pointing playback at a companion path. Probing/encoding
    /// them directly produces noisy diagnostic errors on every scan without ever yielding a useful
    /// issue, so <see cref="ProbingScannerBase"/> skips them up front.
    /// </summary>
    public static readonly HashSet<string> NonProbable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".img", ".nrg", ".ifo", ".strm", ".disc", ".001", ".bin"
    };
}

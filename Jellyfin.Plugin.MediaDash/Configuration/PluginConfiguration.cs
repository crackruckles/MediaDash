using MediaBrowser.Model.Plugins;

// Arrays are required for XML-serialized plugin configuration.
#pragma warning disable CA1819

namespace Jellyfin.Plugin.MediaDash.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        MaxResolutionHeight = 1080;
        MaxBitrateMbpsAt1080p = 8;
        PreferredCodec = "hevc";
        QualityTolerancePercent = 15;
        AllowedAudioLanguages = [];
        AllowedSubtitleLanguages = [];
        CodecPreferenceOrder = ["av1", "hevc", "h264", "vp9", "mpeg4", "mpeg2video"];
        ReencodeFileTypes = [];
        TargetContainer = "mkv";
        UseHardwareEncoder = false;
        PreferredGpuIndex = null;
        SoftwareEncodePreset = EncodePreset.Balanced;
        MinScanFileSizeMb = 100;
        SkipHdrContent = true;
        KeeperPolicyOrder = ["Resolution", "Codec", "Bitrate", "Size"];
        ThoroughPlayabilityCheck = true;
        TreatEditionsAsDuplicates = false;
        DryRun = true;
        DuplicateFixMode = FixMode.DetectOnly;
        TranscodeFixMode = FixMode.DetectOnly;
        SubtitleFixMode = FixMode.DetectOnly;
        AudioFixMode = FixMode.DetectOnly;
        PlayabilityFixMode = FixMode.DetectOnly;
        DuplicateDisposal = DisposalMethod.RecycleBin;
        TranscodeDisposal = DisposalMethod.RecycleBin;
        SubtitleDisposal = DisposalMethod.RecycleBin;
        AudioDisposal = DisposalMethod.RecycleBin;
        PlayabilityDisposal = DisposalMethod.RecycleBin;
        RecycleBinPath = string.Empty;
        RecycleBinRetentionDays = 30;
        MaxConcurrentTranscodes = 1;
        PauseDuringPlayback = true;
        ScheduledFixTime = "03:00";
        FirstRunDone = false;
        AnalyticsEnabled = true;
        AnalyticsInstallId = string.Empty;
        EnabledLibraries = [];
        MisplacedFixMode = FixMode.DetectOnly;
        MoviesTargetPath = string.Empty;
        TvTargetPath = string.Empty;
        MusicTargetPath = string.Empty;
        BooksTargetPath = string.Empty;
        ComicsTargetPath = string.Empty;
        PicturesTargetPath = string.Empty;
        MediaSortSource = MediaSortSource.JellyfinMetadata;
        RenameAfterTranscode = false;
        MissingSubtitlesFixMode = FixMode.DetectOnly;
        AnimeTargetPath = string.Empty;
        StaleFixMode = FixMode.Off;
        StaleThresholdDays = 365;
        StaleExcludedLibraryIds = [];
        StaleExcludedGenres = [];
        DuplicateMinAgeDays = 7;
        PostV12CleanupCompleted = false;
        SuspiciousFileFixMode = FixMode.DetectOnly;
        SuspiciousFileDisposal = DisposalMethod.RecycleBin;
        HistoryHiddenBeforeUtcTicks = 0;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the first-run setup has been completed.
    /// </summary>
    public bool FirstRunDone { get; set; }

    /// <summary>
    /// Gets or sets the item ids of libraries MediaDash scans. Empty means all movie and TV libraries.
    /// </summary>
    public string[] EnabledLibraries { get; set; }

    /// <summary>
    /// Gets or sets how duplicate fixes run.
    /// </summary>
    public FixMode DuplicateFixMode { get; set; }

    /// <summary>
    /// Gets or sets how re-encode fixes run.
    /// </summary>
    public FixMode TranscodeFixMode { get; set; }

    /// <summary>
    /// Gets or sets how subtitle track removal runs.
    /// </summary>
    public FixMode SubtitleFixMode { get; set; }

    /// <summary>
    /// Gets or sets how audio track removal runs.
    /// </summary>
    public FixMode AudioFixMode { get; set; }

    /// <summary>
    /// Gets or sets how removal of unplayable files runs.
    /// </summary>
    public FixMode PlayabilityFixMode { get; set; }

    /// <summary>
    /// Gets or sets where removed unplayable files go.
    /// </summary>
    public DisposalMethod PlayabilityDisposal { get; set; }

    /// <summary>
    /// Gets or sets where files removed by duplicate fixes go.
    /// </summary>
    public DisposalMethod DuplicateDisposal { get; set; }

    /// <summary>
    /// Gets or sets where replaced originals of re-encodes go.
    /// </summary>
    public DisposalMethod TranscodeDisposal { get; set; }

    /// <summary>
    /// Gets or sets where replaced originals of subtitle strips go.
    /// </summary>
    public DisposalMethod SubtitleDisposal { get; set; }

    /// <summary>
    /// Gets or sets where replaced originals of audio strips go.
    /// </summary>
    public DisposalMethod AudioDisposal { get; set; }

    /// <summary>
    /// Gets or sets the recycle bin folder. Empty uses a folder inside the plugin's data directory.
    /// </summary>
    public string RecycleBinPath { get; set; }

    /// <summary>
    /// Gets or sets how many days recycled files are kept before automatic purge.
    /// </summary>
    public int RecycleBinRetentionDays { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of simultaneous re-encodes.
    /// </summary>
    public int MaxConcurrentTranscodes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether scheduled scans and fixes only run while the server is idle:
    /// nobody playing media and no session active in the last 15 minutes. Manual runs from the dashboard ignore this.
    /// When on, this acts as a hard veto over <see cref="ScheduledFixTime"/> — the run is skipped if the server
    /// is busy at the scheduled time and stays queued for the next idle window.
    /// </summary>
    public bool PauseDuringPlayback { get; set; }

    /// <summary>
    /// Gets or sets the daily time-of-day the fix task fires, formatted as "HH:mm" (24h, server local time).
    /// Defaults to "03:00". Invalid values fall back to 03:00.
    /// </summary>
    public string ScheduledFixTime { get; set; } = "03:00";

    /// <summary>
    /// Gets or sets a value indicating whether MediaDash reports aggregated, anonymous per-run statistics
    /// (per-type success counts + bytes freed, plus plugin and Jellyfin versions) to the community stats board.
    /// Off by default. Enabled via a first-run wizard step or Settings toggle. No paths, no filenames, no
    /// usernames — only counts, byte totals, and version strings are sent.
    /// </summary>
    public bool AnalyticsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the anonymous ID used to deduplicate this install's monthly rows on the analytics
    /// backend. Populated on first opt-in with a fresh <see cref="System.Guid"/>. Never leaves this
    /// config file; the only thing derived from it is the row key on the analytics DB.
    /// </summary>
    public string AnalyticsInstallId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum wanted video height in pixels (e.g. 1080). Files taller than this are flagged as oversized.
    /// </summary>
    public int MaxResolutionHeight { get; set; }

    /// <summary>
    /// Gets or sets the maximum wanted video bitrate in Mbps for 1080p content; scaled proportionally for other resolutions.
    /// </summary>
    public double MaxBitrateMbpsAt1080p { get; set; }

    /// <summary>
    /// Gets or sets the codec files should be transcoded to when they exceed the quality ceiling.
    /// </summary>
    public string PreferredCodec { get; set; }

    /// <summary>
    /// Gets or sets the tolerance percentage above the ceilings before a file is flagged, to avoid churn on borderline files.
    /// </summary>
    public int QualityTolerancePercent { get; set; }

    /// <summary>
    /// Gets or sets the ISO 639-2 codes of audio languages to keep. Empty means the audio language scanner is not configured and stays off.
    /// </summary>
    public string[] AllowedAudioLanguages { get; set; }

    /// <summary>
    /// Gets or sets the ISO 639-2 codes of subtitle languages to keep. Empty means the subtitle language scanner is not configured and stays off.
    /// </summary>
    public string[] AllowedSubtitleLanguages { get; set; }

    /// <summary>
    /// Gets or sets the codec ranking used when choosing which duplicate to keep; earlier entries win.
    /// </summary>
    public string[] CodecPreferenceOrder { get; set; }

    /// <summary>
    /// Gets or sets the file extensions (without dot) eligible for re-encoding, e.g. "mkv", "avi". Empty means all video files.
    /// </summary>
    public string[] ReencodeFileTypes { get; set; }

    /// <summary>
    /// Gets or sets the container format re-encoded files are written to (e.g. "mkv" or "mp4").
    /// </summary>
    public string TargetContainer { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether re-encodes use the server's configured hardware encoder
    /// (much faster, slightly larger files). Falls back to software per file when the hardware encoder fails.
    /// </summary>
    public bool UseHardwareEncoder { get; set; }

    /// <summary>
    /// Gets or sets the GPU index re-encodes should target when the host has more than one card
    /// (e.g., dedicated dGPU alongside an iGPU). Null = let ffmpeg pick (Jellyfin's default). Matches the
    /// index reported by the /Status endpoint under System.Gpus.
    /// </summary>
    public int? PreferredGpuIndex { get; set; }

    /// <summary>
    /// Gets or sets the speed-vs-quality preset for software re-encodes (ignored by hardware encoders,
    /// which don't support CRF and use the plugin's bitrate ceiling instead).
    /// </summary>
    public EncodePreset SoftwareEncodePreset { get; set; }

    /// <summary>
    /// Gets or sets the minimum file size in megabytes for a file to be considered by the quality scanner.
    /// Filters out sample files, trailers, and other small media that shouldn't be re-encoded.
    /// </summary>
    public int MinScanFileSizeMb { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether files detected as HDR (color_primaries=bt2020, transfer=smpte2084/arib-std-b67)
    /// are skipped by the quality scanner. Default: on. Naively re-encoding HDR content without proper color-space
    /// plumbing destroys HDR metadata, so opting in should be a deliberate choice.
    /// </summary>
    public bool SkipHdrContent { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Quality scanner should also inspect audiobook items.
    /// Off by default because audiobooks are typically already low-bitrate spoken word and the audio ceiling produces false positives.
    /// </summary>
    public bool QualityScanAudiobooks { get; set; }

    /// <summary>
    /// Gets or sets the order of criteria used to pick the copy to keep among duplicates.
    /// </summary>
    public string[] KeeperPolicyOrder { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the playability scan test-plays the start, middle and end of every file.
    /// On by default; the first scan is slow but results are cached for unchanged files.
    /// </summary>
    public bool ThoroughPlayabilityCheck { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether different editions of the same movie are treated as duplicates of each other.
    /// </summary>
    public bool TreatEditionsAsDuplicates { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether fixes only log what they would do instead of changing files. Defaults to on for safety.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets how misplaced-media fixes run (a movie in the TV folder, or a TV episode in the Movies folder).
    /// </summary>
    public FixMode MisplacedFixMode { get; set; }

    /// <summary>
    /// Gets or sets the destination folder for movies that need to be moved. Empty disables the sorter.
    /// Must sit inside a Jellyfin library folder or moves are refused by <see cref="Fixers.LibraryGuard"/>.
    /// </summary>
    public string MoviesTargetPath { get; set; }

    /// <summary>
    /// Gets or sets the destination folder for TV episodes that need to be moved. Empty disables the sorter.
    /// Must sit inside a Jellyfin library folder or moves are refused by <see cref="Fixers.LibraryGuard"/>.
    /// </summary>
    public string TvTargetPath { get; set; }

    /// <summary>
    /// Gets or sets the destination folder music (Audio / MusicAlbum items) is moved into when the sorter
    /// finds it in a Movies / TV / Books library. Empty disables music sorting. Must sit inside a Jellyfin
    /// library folder or moves are refused by <see cref="Fixers.LibraryGuard"/>.
    /// </summary>
    public string MusicTargetPath { get; set; }

    /// <summary>
    /// Gets or sets the destination folder books (Book items — EPUB/PDF/MOBI/AZW3) are moved into when the
    /// sorter finds them in a non-book library. Empty disables book sorting. Must sit inside a Jellyfin
    /// library folder or moves are refused by <see cref="Fixers.LibraryGuard"/>.
    /// </summary>
    public string BooksTargetPath { get; set; }

    /// <summary>
    /// Gets or sets the destination folder comics (CBZ / CBR / CB7 files, currently classified as Book by
    /// Jellyfin until a native Comic entity ships) are moved into when the sorter finds them in a non-comic
    /// library. Empty disables comic sorting. Must sit inside a Jellyfin library folder or moves are refused
    /// by <see cref="Fixers.LibraryGuard"/>.
    /// </summary>
    public string ComicsTargetPath { get; set; }

    /// <summary>
    /// Gets or sets the destination folder pictures / photos are moved into when the sorter finds them
    /// in a non-photo library. Empty disables picture sorting. Must sit inside a Jellyfin library folder
    /// or moves are refused by <see cref="Fixers.LibraryGuard"/>.
    /// </summary>
    public string PicturesTargetPath { get; set; }

    /// <summary>
    /// Gets or sets where the sorter reads a file's movie/TV classification from.
    /// </summary>
    public MediaSortSource MediaSortSource { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether successful re-encodes rename the output to a canonical filename
    /// (Movie: <c>Name (Year) - {height}p.{ext}</c>; TV: <c>SeriesName - S{ss:00}E{ee:00} - {height}p.{ext}</c>).
    /// </summary>
    public bool RenameAfterTranscode { get; set; }

    /// <summary>
    /// Gets or sets how the missing-subtitle scanner acts. Fixes call Jellyfin's ISubtitleManager to download
    /// a matching subtitle from the admin-configured providers; requires at least one provider set up in Jellyfin.
    /// </summary>
    public FixMode MissingSubtitlesFixMode { get; set; }

    /// <summary>
    /// Gets or sets the destination folder anime lands in when the media sorter runs. Empty disables anime
    /// routing — anime is treated as its underlying kind (Movie or TV episode). Must sit inside a Jellyfin
    /// library or moves are refused by <see cref="Fixers.LibraryGuard"/>. Detection uses Jellyfin's "Anime"
    /// genre tag (case-insensitive); falls back to normal movie/TV classification when absent.
    /// </summary>
    public string AnimeTargetPath { get; set; }

    /// <summary>
    /// Gets or sets how the stale-content scanner runs. Off by default because "stale" is a subjective call
    /// and no fixer exists yet — DetectOnly surfaces the list on the Issues tab so the owner can decide.
    /// </summary>
    public FixMode StaleFixMode { get; set; }

    /// <summary>
    /// Gets or sets the age in days above which an unwatched file is considered stale. Both the "no user has
    /// played it in this window" AND "the item has been on the server this long" conditions must be true —
    /// so freshly-imported items are never flagged as stale on their first scan.
    /// </summary>
    public int StaleThresholdDays { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin library item ids the stale scanner ignores entirely. Useful for libraries
    /// where "unwatched for a year" is the desired long-term state (Christmas movies, reference archives).
    /// </summary>
    public string[] StaleExcludedLibraryIds { get; set; }

    /// <summary>
    /// Gets or sets the genre names whose items the stale scanner skips (case-insensitive match against any
    /// tag on <c>BaseItem.Genres</c>). Complements <see cref="StaleExcludedLibraryIds"/> for finer control —
    /// e.g. keep everything tagged "Documentary" regardless of last-played date.
    /// </summary>
    public string[] StaleExcludedGenres { get; set; }

    /// <summary>
    /// Gets or sets the minimum age in days a duplicate copy must have before the scanner will flag it.
    /// Filters out fresh imports whose Jellyfin metadata hasn't stabilised yet — a movie imported 5 minutes
    /// ago will often match itself under the "no metadata" bucket for a short window before the provider
    /// scrape lands. Default 7. Set to 0 to disable the gate.
    /// </summary>
    public int DuplicateMinAgeDays { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the one-shot post-Jellyfin-12 upgrade cleanup has been run
    /// (or dismissed). Once true, the Overview banner offering the sweep stops appearing forever - by
    /// design, this is a "run once after your 10.x -> 12.x jump" action, not something to keep offering.
    /// </summary>
    public bool PostV12CleanupCompleted { get; set; }

    /// <summary>
    /// Gets or sets the UTC-ticks watermark below which history rows are hidden from the History tab.
    /// The rows still exist and still count toward aggregate savings (per-library chart, "Reclaimed
    /// since install", monthly analytics) - only the visible list is truncated. Advanced past by the
    /// Clear-history button. 0 means show everything.
    /// </summary>
    public long HistoryHiddenBeforeUtcTicks { get; set; }

    /// <summary>
    /// Gets or sets how the suspicious-file scanner acts. Defaults to <see cref="FixMode.DetectOnly"/>
    /// so nothing gets deleted without the user clicking Approve — malware detection is only as good
    /// as the extension list, and we'd rather over-flag than silently nuke a file the user meant to keep.
    /// </summary>
    public FixMode SuspiciousFileFixMode { get; set; }

    /// <summary>
    /// Gets or sets where files removed by the suspicious-file fixer go. Defaults to
    /// <see cref="DisposalMethod.RecycleBin"/> because "MediaDash quarantined a random binary I meant
    /// to keep" is the recoverable-failure mode we care about.
    /// </summary>
    public DisposalMethod SuspiciousFileDisposal { get; set; }

    /// <summary>
    /// Gets the fix mode for an issue type.
    /// </summary>
    /// <param name="type">The issue type.</param>
    /// <returns>The configured mode.</returns>
    public FixMode GetFixMode(Data.IssueType type)
    {
        return type switch
        {
            Data.IssueType.Duplicate => DuplicateFixMode,
            Data.IssueType.Quality => TranscodeFixMode,
            Data.IssueType.SubtitleLanguage => SubtitleFixMode,
            Data.IssueType.AudioLanguage => AudioFixMode,
            Data.IssueType.Playability => PlayabilityFixMode,
            Data.IssueType.Misplaced => MisplacedFixMode,
            Data.IssueType.MissingSubtitles => MissingSubtitlesFixMode,
            Data.IssueType.Stale => StaleFixMode,
            Data.IssueType.MalwareRisk => SuspiciousFileFixMode,
            _ => FixMode.DetectOnly
        };
    }

    /// <summary>
    /// Gets the disposal method for an issue type.
    /// </summary>
    /// <param name="type">The issue type.</param>
    /// <returns>The configured disposal method.</returns>
    public DisposalMethod GetDisposal(Data.IssueType type)
    {
        return type switch
        {
            Data.IssueType.Duplicate => DuplicateDisposal,
            Data.IssueType.Quality => TranscodeDisposal,
            Data.IssueType.SubtitleLanguage => SubtitleDisposal,
            Data.IssueType.AudioLanguage => AudioDisposal,
            Data.IssueType.Playability => PlayabilityDisposal,
            Data.IssueType.MalwareRisk => SuspiciousFileDisposal,
            _ => DisposalMethod.RecycleBin
        };
    }
}

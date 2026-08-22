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
        TreatEditionsAsDuplicates = true;
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
        RecycleBinWarnThresholdGb = 10;
        PauseDuringPlayback = true;
        FirstRunDone = false;
        // Community reporting requires affirmative consent in the wizard or Settings → Safety.
        // The install UUID is minted only when the user enables the toggle.
        AnalyticsEnabled = false;
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
        TrickplayFixMode = FixMode.DetectOnly;
        TrickplayWebPQuality = 80;
        TrickplayMinSizeMb = 10;
        SubtitleFontFixMode = FixMode.DetectOnly;
        SubtitleForceFont = string.Empty;
        OrphanCleanupFixMode = FixMode.DetectOnly;
        OrphanCleanupDisposal = DisposalMethod.RecycleBin;
        OrphanScanEmptyFolders = true;
        OrphanScanSubtitles = true;
        OrphanScanTrickplay = true;
        OrphanScanMetadata = true;
        NfoFixMode = FixMode.DetectOnly;
        NfoDisposal = DisposalMethod.RecycleBin;
        HeavyTranscodeFixMode = FixMode.DetectOnly;
        HeavyTranscodeDisposal = DisposalMethod.RecycleBin;
        HeavyTranscodeLookbackDays = 30;
        FailedTranscodeFixMode = FixMode.DetectOnly;
        FailedTranscodeDisposal = DisposalMethod.RecycleBin;
        EmbeddedCoverFixMode = FixMode.DetectOnly;
        EmbeddedCoverDisposal = DisposalMethod.RecycleBin;
        EmbeddedCoverStripFromAudio = true;
        EmbeddedCoverFilename = "cover.jpg";
        UngroupedFixMode = FixMode.DetectOnly;
        CorruptArtworkFixMode = FixMode.DetectOnly;
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
    /// Gets or sets the recycle-bin size (in GB) that triggers the "bin is getting big" banner on the
    /// dashboard. 0 disables the banner entirely.
    /// </summary>
    public int RecycleBinWarnThresholdGb { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether scheduled scans and fixes only run while the server is idle:
    /// nobody playing media and no session active in the last 15 minutes. Manual runs from the dashboard ignore this.
    /// The fix task fires on an interval and returns immediately when this check fails, so it stays out of the
    /// way whenever someone is using the server.
    /// </summary>
    public bool PauseDuringPlayback { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether MediaDash reports aggregated, anonymous per-run statistics
    /// (per-scanner success counts + bytes freed, plus plugin and Jellyfin versions) to the community stats board.
    /// Off by default; the first-run wizard and Settings → Safety let users opt in explicitly.
    /// No paths, no filenames, no usernames — only counts, byte totals, and version strings are sent.
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
    /// Gets or sets how the trickplay-optimise scanner acts. Detect-only by default so the very first
    /// scan surfaces the projected reclaim size (typically 40-55% of the trickplay data folder) before
    /// the user opts into rewriting sidecar images.
    /// </summary>
    public FixMode TrickplayFixMode { get; set; }

    /// <summary>
    /// Gets or sets the libwebp quality (0-100) used when re-encoding trickplay JPG sprites to WebP.
    /// Default 80 sits at the "indistinguishable at scrub-bar zoom" edge of the curve; below 75 starts
    /// showing soft edges on high-contrast text overlays, above 85 gives up size savings for pixel-peeping.
    /// </summary>
    public int TrickplayWebPQuality { get; set; }

    /// <summary>
    /// Gets or sets the minimum size (in MB) a trickplay folder must have before it's flagged as a
    /// LargeTrickplay issue. Prevents 1000s of low-savings items from swamping the Issues tab on
    /// libraries with many small clips. 0 disables the threshold (flag every convertible folder).
    /// </summary>
    public int TrickplayMinSizeMb { get; set; }

    /// <summary>
    /// Gets or sets how the subtitle-font optimiser acts. DetectOnly by default so users see the
    /// projected reclaim size before opting into rewriting .ass sidecars.
    /// </summary>
    public FixMode SubtitleFontFixMode { get; set; }

    /// <summary>
    /// Gets or sets the family name every subtitle should be forced to use. Empty (default) means keep
    /// each sidecar's original per-style fonts and only strip unused embedded ones. Setting a value
    /// rewrites every Style.Fontname / <c>{\fn}</c> override to this name AND removes the entire
    /// <c>[Fonts]</c> section — the client is expected to have this font available.
    /// </summary>
    public string SubtitleForceFont { get; set; }

    /// <summary>
    /// Gets or sets how the orphan-cleanup task acts. Defaults to Off because deletion is destructive
    /// and users need to opt in after seeing what would be removed.
    /// </summary>
    public FixMode OrphanCleanupFixMode { get; set; }

    /// <summary>Gets or sets where orphan-cleanup deletions go — recycle bin by default so any misdetection stays recoverable.</summary>
    public DisposalMethod OrphanCleanupDisposal { get; set; }

    /// <summary>Gets or sets a value indicating whether the orphan-cleanup pass looks for empty media folders.</summary>
    public bool OrphanScanEmptyFolders { get; set; }

    /// <summary>Gets or sets a value indicating whether the orphan-cleanup pass looks for subtitle sidecars whose companion video is gone.</summary>
    public bool OrphanScanSubtitles { get; set; }

    /// <summary>Gets or sets a value indicating whether the orphan-cleanup pass looks for media-folder trickplay folders whose companion video is gone.</summary>
    public bool OrphanScanTrickplay { get; set; }

    /// <summary>Gets or sets a value indicating whether the orphan-cleanup pass looks for Jellyfin metadata folders whose item GUID no longer resolves.</summary>
    public bool OrphanScanMetadata { get; set; }

    /// <summary>
    /// Gets or sets how the NFO-integrity task acts. Detect-only by default so the user sees the list
    /// before opting into deletion — a hand-curated NFO that went corrupt is user work that's easy to
    /// mistake for regenerable metadata.
    /// </summary>
    public FixMode NfoFixMode { get; set; }

    /// <summary>Gets or sets where deleted corrupt NFOs go. RecycleBin default keeps hand-curated files recoverable.</summary>
    public DisposalMethod NfoDisposal { get; set; }

    /// <summary>Gets or sets how the heavy-transcode task acts. Fix reuses the transcode pipeline, so on Automatic
    /// the plugin will re-encode any file that has needed a live transcode in the lookback window.</summary>
    public FixMode HeavyTranscodeFixMode { get; set; }

    /// <summary>Gets or sets where the original goes after a heavy-transcode re-encode. RecycleBin default.</summary>
    public DisposalMethod HeavyTranscodeDisposal { get; set; }

    /// <summary>Gets or sets how many days back the transcode-log scanner reads. 30 by default; higher = more
    /// history but more log parsing per scan.</summary>
    public int HeavyTranscodeLookbackDays { get; set; }

    /// <summary>Gets or sets how the failed-transcode task acts. Same fix path as the heavy-transcode task.</summary>
    public FixMode FailedTranscodeFixMode { get; set; }

    /// <summary>Gets or sets where the original goes after a failed-transcode re-encode. RecycleBin default.</summary>
    public DisposalMethod FailedTranscodeDisposal { get; set; }

    /// <summary>Gets or sets how the embedded-cover-art task acts. Extract-only when just the mode is set;
    /// stripping requires <see cref="EmbeddedCoverStripFromAudio"/> to also be on.</summary>
    public FixMode EmbeddedCoverFixMode { get; set; }

    /// <summary>Gets or sets how loose files that should sit under a per-title folder are handled.</summary>
    public FixMode UngroupedFixMode { get; set; }

    /// <summary>Gets or sets how corrupt Jellyfin-managed artwork (poster/backdrop/thumb inside InternalMetadataPath) is handled.</summary>
    public FixMode CorruptArtworkFixMode { get; set; }

    /// <summary>Gets or sets where original audio files go after their embedded covers are stripped.
    /// Only meaningful when <see cref="EmbeddedCoverStripFromAudio"/> is on.</summary>
    public DisposalMethod EmbeddedCoverDisposal { get; set; }

    /// <summary>Gets or sets a value indicating whether the fixer also strips the redundant embedded cover
    /// from each audio file after writing the shared folder cover. Big savings (500 KB × track count per folder)
    /// but the audio files get rewritten — hence the recycle-bin disposal safety net.</summary>
    public bool EmbeddedCoverStripFromAudio { get; set; }

    /// <summary>Gets or sets the filename the fixer writes into each folder. Jellyfin recognises
    /// <c>cover.jpg</c> / <c>folder.jpg</c> equally; <c>cover.jpg</c> is the modern default.</summary>
    public string EmbeddedCoverFilename { get; set; } = "cover.jpg";

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
            Data.IssueType.LargeTrickplay => TrickplayFixMode,
            Data.IssueType.SubtitleFonts => SubtitleFontFixMode,
            Data.IssueType.OrphanedDebris => OrphanCleanupFixMode,
            Data.IssueType.CorruptNfo => NfoFixMode,
            Data.IssueType.HeavyTranscode => HeavyTranscodeFixMode,
            Data.IssueType.FailedTranscode => FailedTranscodeFixMode,
            Data.IssueType.EmbeddedCoverArt => EmbeddedCoverFixMode,
            Data.IssueType.Ungrouped => UngroupedFixMode,
            Data.IssueType.CorruptArtwork => CorruptArtworkFixMode,
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
            Data.IssueType.OrphanedDebris => OrphanCleanupDisposal,
            Data.IssueType.CorruptNfo => NfoDisposal,
            Data.IssueType.HeavyTranscode => HeavyTranscodeDisposal,
            Data.IssueType.FailedTranscode => FailedTranscodeDisposal,
            Data.IssueType.EmbeddedCoverArt => EmbeddedCoverDisposal,
            _ => DisposalMethod.RecycleBin
        };
    }
}

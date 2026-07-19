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
        KeeperPolicyOrder = ["Resolution", "Codec", "Bitrate", "Size"];
        ThoroughPlayabilityCheck = false;
        TreatEditionsAsDuplicates = false;
        DryRun = true;
        DuplicateFixMode = FixMode.DetectOnly;
        TranscodeFixMode = FixMode.DetectOnly;
        SubtitleFixMode = FixMode.DetectOnly;
        AudioFixMode = FixMode.DetectOnly;
        DuplicateDisposal = DisposalMethod.RecycleBin;
        TranscodeDisposal = DisposalMethod.RecycleBin;
        SubtitleDisposal = DisposalMethod.RecycleBin;
        AudioDisposal = DisposalMethod.RecycleBin;
        RecycleBinPath = string.Empty;
        RecycleBinRetentionDays = 30;
        MaxConcurrentTranscodes = 1;
        PauseDuringPlayback = true;
        FirstRunDone = false;
        EnabledLibraries = [];
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
    /// </summary>
    public bool PauseDuringPlayback { get; set; }

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
    /// Gets or sets the order of criteria used to pick the copy to keep among duplicates.
    /// </summary>
    public string[] KeeperPolicyOrder { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the playability scan additionally decodes the start and end of every file (slow but thorough).
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
    /// Gets the fix mode for an issue type.
    /// </summary>
    /// <param name="type">The issue type.</param>
    /// <returns>The configured mode; playability issues are never auto-fixed.</returns>
    public FixMode GetFixMode(Data.IssueType type)
    {
        return type switch
        {
            Data.IssueType.Duplicate => DuplicateFixMode,
            Data.IssueType.Quality => TranscodeFixMode,
            Data.IssueType.SubtitleLanguage => SubtitleFixMode,
            Data.IssueType.AudioLanguage => AudioFixMode,
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
            _ => DisposalMethod.RecycleBin
        };
    }
}

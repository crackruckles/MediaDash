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
        KeeperPolicyOrder = ["Resolution", "Codec", "Bitrate", "Size"];
        ThoroughPlayabilityCheck = false;
        TreatEditionsAsDuplicates = false;
        DryRun = true;
    }

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
}

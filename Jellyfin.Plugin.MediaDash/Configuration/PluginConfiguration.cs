using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaDash.Configuration;

/// <summary>
/// All user-configurable settings for MediaDash.
/// Shown in Dashboard → Plugins → MediaDash → Settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── Media directories ──────────────────────────────────────────────────
    /// <summary>
    /// Newline-separated list of directories to scan for video files.
    /// If empty, the plugin will use Jellyfin's own configured library paths.
    /// </summary>
    public string MediaDirectories { get; set; } = string.Empty;

    // ── Encoder: process control ───────────────────────────────────────────
    /// <summary>
    /// systemd service name for the re-encoder.
    /// The plugin will call: systemctl start {ReencodeServiceName}
    /// Leave empty to hide the "Run re-encoder" button.
    /// </summary>
    public string ReencodeServiceName { get; set; } = string.Empty;

    /// <summary>
    /// systemd service name for the track stripper.
    /// Leave empty to hide the "Run strip tracks" button.
    /// </summary>
    public string StripServiceName { get; set; } = string.Empty;

    /// <summary>Path to touch to pause encoding. Delete to resume.</summary>
    public string PauseFlagPath { get; set; } = "/tmp/mediadash_pause";

    /// <summary>Path to touch to force-resume past quiet hours.</summary>
    public string ForceFlagPath { get; set; } = "/tmp/mediadash_force";

    /// <summary>Substring in /proc/*/cmdline to detect the re-encoder process.</summary>
    public string ReencodeProcessName { get; set; } = string.Empty;

    /// <summary>Substring in /proc/*/cmdline to detect the strip-tracks process.</summary>
    public string StripProcessName { get; set; } = string.Empty;

    // ── Encoder: encode settings ───────────────────────────────────────────
    /// <summary>Hardware acceleration method: vaapi, nvenc, or software.</summary>
    public string EncodeMethod { get; set; } = "vaapi";

    /// <summary>Target video codec: hevc, h264, or av1.</summary>
    public string TargetCodec { get; set; } = "hevc";

    /// <summary>CQP quality value. Lower = better quality, larger file. 22 is a good default.</summary>
    public int EncodeQuality { get; set; } = 22;

    /// <summary>Number of concurrent encode workers.</summary>
    public int EncodeWorkers { get; set; } = 1;

    /// <summary>
    /// Path to the VAAPI/DRM render device.
    /// AMD/Intel: /dev/dri/renderD128  NVIDIA (via nvenc): leave blank.
    /// </summary>
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";

    /// <summary>
    /// Comma-separated video codecs to skip (already efficient).
    /// Example: hevc,av1
    /// </summary>
    public string SkipCodecs { get; set; } = "hevc,av1";

    /// <summary>
    /// Comma-separated video file extensions to process.
    /// </summary>
    public string VideoExtensions { get; set; } =
        ".mkv,.mp4,.avi,.m2ts,.webm,.mov,.ts,.flv,.wmv,.mpg,.mpeg,.divx,.vob";

    // ── Track stripping: language preferences ─────────────────────────────
    /// <summary>
    /// Whether track stripping is enabled at all.
    /// </summary>
    public bool EnableTrackStripping { get; set; } = false;

    /// <summary>
    /// Comma-separated ISO 639-2 language codes to KEEP in audio tracks.
    /// All other audio tracks will be removed.
    /// Example: eng,jpn  — leave empty to keep all audio tracks.
    /// </summary>
    public string KeepAudioLanguages { get; set; } = "eng";

    /// <summary>
    /// Comma-separated ISO 639-2 language codes to KEEP in subtitle tracks.
    /// Leave empty to keep all subtitle tracks.
    /// </summary>
    public string KeepSubtitleLanguages { get; set; } = "eng";

    /// <summary>
    /// Always keep the first audio track regardless of language,
    /// so the file always has at least one audio track.
    /// </summary>
    public bool AlwaysKeepFirstAudio { get; set; } = true;

    /// <summary>Whether to keep commentary/description audio tracks.</summary>
    public bool KeepCommentaryTracks { get; set; } = false;

    // ── Status / log file paths ────────────────────────────────────────────
    /// <summary>
    /// Directory containing per-worker encode status JSON files
    /// (slot_0.json, slot_1.json …) written by your encode script.
    /// Leave empty if you don't use slot-based status files.
    /// </summary>
    public string EncodeStatusDir { get; set; } = string.Empty;

    /// <summary>Full path to the re-encoder log file.</summary>
    public string ReencodeLogPath { get; set; } = string.Empty;

    /// <summary>Full path to the track-stripping log file.</summary>
    public string StripLogPath { get; set; } = string.Empty;

    /// <summary>Full path to the duplicate-detection JSON report.</summary>
    public string DupesReportPath { get; set; } = string.Empty;

    /// <summary>Full path to the JSON file tracking processed files.</summary>
    public string ReencodeStateFile { get; set; } = string.Empty;

    /// <summary>Path to the find_dupes script. Leave empty to disable dupe scanning.</summary>
    public string DupesScanScript { get; set; } = string.Empty;

    // ── Quiet hours ────────────────────────────────────────────────────────
    /// <summary>Hour (0–23) when encoding should pause.</summary>
    public int QuietHoursStart { get; set; } = 22;

    /// <summary>Hour (0–23) when encoding should resume.</summary>
    public int QuietHoursEnd { get; set; } = 8;

    /// <summary>Whether to also pause when Jellyfin streams are active.</summary>
    public bool PauseDuringStreams { get; set; } = true;
}

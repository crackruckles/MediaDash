using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaDash.Configuration;

/// <summary>
/// All user-configurable settings for MediaDash.
/// Shown in Dashboard → Plugins → MediaDash → Settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── Media root ─────────────────────────────────────────────────────────
    /// <summary>Path to the root of your media storage (for disk usage stats).</summary>
    public string MediaRootPath { get; set; } = "/mnt/media";

    // ── Log files written by the processing scripts ─────────────────────
    /// <summary>Full path to the re-encode log file.</summary>
    public string ReencodeLogPath { get; set; } = "/var/log/reencode_bdremux.log";

    /// <summary>Full path to the strip-tracks log file.</summary>
    public string StripLogPath { get; set; } = "/var/log/strip_tracks.log";

    /// <summary>Full path to the duplicate-detection JSON report.</summary>
    public string DupesReportPath { get; set; } = "/var/log/find_dupes_report.json";

    // ── Re-encode state / live status ──────────────────────────────────
    /// <summary>
    /// Directory containing per-worker slot JSON files written by
    /// reencode_bdremux.py (slot_0.json, slot_1.json …).
    /// </summary>
    public string EncodeStatusDir { get; set; } = "/tmp/encode_status";

    /// <summary>State file tracking which files have already been processed.</summary>
    public string ReencodeStateFile { get; set; } = "/var/lib/reencode/processed.json";

    // ── Flag files used to pause / resume the encoder ──────────────────
    /// <summary>
    /// Touch this file to pause encoding; delete it to allow encoding.
    /// The Python script polls for its presence.
    /// </summary>
    public string PauseFlagPath { get; set; } = "/tmp/reencode_pause";

    /// <summary>Touch this file to force-resume past quiet hours.</summary>
    public string ForceFlagPath { get; set; } = "/tmp/reencode_force";

    // ── Script / service names ─────────────────────────────────────────
    /// <summary>
    /// systemd service name to start when the user clicks "Run re-encoder".
    /// Leave empty to disable the button.
    /// </summary>
    public string ReencodeServiceName { get; set; } = "reencode-bdremux";

    /// <summary>
    /// systemd service name to start when the user clicks "Run strip tracks".
    /// Leave empty to disable the button.
    /// </summary>
    public string StripServiceName { get; set; } = "strip-tracks";

    /// <summary>
    /// Path to the find_dupes.py script, used by "Run scan now".
    /// Leave empty to disable the button.
    /// </summary>
    public string DupesScanScript { get; set; } = "/root/scripts/find_dupes.py";

    // ── Process detection ──────────────────────────────────────────────
    /// <summary>Substring to look for in /proc to detect active re-encoding.</summary>
    public string ReencodeProcessName { get; set; } = "reencode_bdremux";

    /// <summary>Substring to look for in /proc to detect active strip-tracks.</summary>
    public string StripProcessName { get; set; } = "strip_tracks";

    // ── Quiet hours (mirrored from the Python script, editable here) ───
    /// <summary>Hour (0–23) when encoding should pause.</summary>
    public int QuietHoursStart { get; set; } = 8;

    /// <summary>Hour (0–23) when encoding should resume.</summary>
    public int QuietHoursEnd { get; set; } = 16;

    // ── Video file extensions the queue scanner looks for ──────────────
    /// <summary>Comma-separated list of video extensions to include in queue count.</summary>
    public string VideoExtensions { get; set; } =
        ".mkv,.mp4,.avi,.m2ts,.webm,.mov,.ts,.flv,.wmv,.mpg,.mpeg,.divx,.vob";

    /// <summary>Comma-separated video codecs that are already done (skip re-encode).</summary>
    public string SkipCodecs { get; set; } = "hevc,av1";

    /// <summary>Encode method: vaapi, nvenc, or software.</summary>
    public string EncodeMethod { get; set; } = "vaapi";
    /// <summary>Target codec: hevc, h264, or av1.</summary>
    public string EncodeCodec { get; set; } = "hevc";
    /// <summary>CQP quality value (lower = better).</summary>
    public int EncodeQuality { get; set; } = 22;
    /// <summary>Number of concurrent encode workers.</summary>
    public int EncodeWorkers { get; set; } = 2;
}

using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Container-level fields from ffprobe's <c>format</c> object.
/// </summary>
public sealed class FfprobeFormat
{
    /// <summary>
    /// Gets or sets the container format name(s).
    /// </summary>
    [JsonPropertyName("format_name")]
    public string? FormatName { get; set; }

    /// <summary>
    /// Gets or sets the duration in seconds, as reported by the container.
    /// </summary>
    [JsonPropertyName("duration")]
    public string? Duration { get; set; }

    /// <summary>
    /// Gets or sets the overall bit rate in bits per second.
    /// </summary>
    [JsonPropertyName("bit_rate")]
    public string? BitRate { get; set; }

    /// <summary>
    /// Gets or sets the file size in bytes as reported by ffprobe.
    /// </summary>
    [JsonPropertyName("size")]
    public string? Size { get; set; }
}

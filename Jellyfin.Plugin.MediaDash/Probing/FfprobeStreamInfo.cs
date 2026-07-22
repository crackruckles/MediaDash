using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// A single stream entry from ffprobe's <c>streams</c> array.
/// </summary>
public sealed class FfprobeStreamInfo
{
    /// <summary>
    /// Gets or sets the stream index within the file.
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the stream kind: video, audio, subtitle, data or attachment.
    /// </summary>
    [JsonPropertyName("codec_type")]
    public string? CodecType { get; set; }

    /// <summary>
    /// Gets or sets the codec name (e.g. h264, hevc, aac, subrip).
    /// </summary>
    [JsonPropertyName("codec_name")]
    public string? CodecName { get; set; }

    /// <summary>
    /// Gets or sets the video width in pixels.
    /// </summary>
    [JsonPropertyName("width")]
    public int? Width { get; set; }

    /// <summary>
    /// Gets or sets the video height in pixels.
    /// </summary>
    [JsonPropertyName("height")]
    public int? Height { get; set; }

    /// <summary>
    /// Gets or sets the stream bit rate in bits per second.
    /// </summary>
    [JsonPropertyName("bit_rate")]
    public string? BitRate { get; set; }

    /// <summary>
    /// Gets or sets the stream duration in seconds.
    /// </summary>
    [JsonPropertyName("duration")]
    public string? Duration { get; set; }

    /// <summary>
    /// Gets or sets the stream tags; the <c>language</c> tag carries the ISO 639-2 language code.
    /// </summary>
    [JsonPropertyName("tags")]
    public IReadOnlyDictionary<string, string>? Tags { get; set; }

    /// <summary>Gets or sets the color primaries (e.g., "bt709" for SDR, "bt2020" for HDR).</summary>
    [JsonPropertyName("color_primaries")]
    public string? ColorPrimaries { get; set; }

    /// <summary>Gets or sets the color transfer characteristic (e.g., "bt709" for SDR, "smpte2084" for HDR10, "arib-std-b67" for HLG).</summary>
    [JsonPropertyName("color_transfer")]
    public string? ColorTransfer { get; set; }

    /// <summary>
    /// Gets the stream's ISO 639-2 language code, or null when untagged.
    /// </summary>
    [JsonIgnore]
    public string? Language => Tags is not null && Tags.TryGetValue("language", out var lang) ? lang : null;
}

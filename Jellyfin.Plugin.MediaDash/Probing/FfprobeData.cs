using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Root of ffprobe's JSON output (<c>-show_format -show_streams -show_error</c>).
/// </summary>
public sealed class FfprobeData
{
    /// <summary>
    /// Gets or sets the container-level information.
    /// </summary>
    [JsonPropertyName("format")]
    public FfprobeFormat? Format { get; set; }

    /// <summary>
    /// Gets or sets the streams in the file.
    /// </summary>
    [JsonPropertyName("streams")]
    public IReadOnlyList<FfprobeStreamInfo>? Streams { get; set; }

    /// <summary>
    /// Gets or sets the probe error, when ffprobe could not read the file.
    /// </summary>
    [JsonPropertyName("error")]
    public FfprobeError? Error { get; set; }
}

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    // Container duration can be N/A while individual audio/video streams have valid durations.
    internal bool TryGetDuration(out double duration)
    {
        if (double.TryParse(Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out duration)
            && double.IsFinite(duration) && duration > 0)
        {
            return true;
        }

        duration = Streams?.Where(s => (s.CodecType is "audio" or "video") && !s.IsAttachedPicture)
            .Select(s => double.TryParse(s.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                && double.IsFinite(d) && d > 0 ? d : 0).DefaultIfEmpty().Max() ?? 0;
        return duration > 0;
    }
}

using System;
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

    // Where the audio and video actually end. The container duration can run past them when a
    // subtitle track ends later (common in remuxes), so a tail check anchored on the container
    // end can land on a stretch with no audio or video at all. Uses each stream's duration, or
    // Matroska's per-stream DURATION tag (hh:mm:ss.fffffffff) when the stream reports none.
    internal bool TryGetAudioVideoEnd(out double seconds)
    {
        seconds = 0;
        foreach (var stream in Streams ?? [])
        {
            if (stream.CodecType is not ("audio" or "video") || stream.IsAttachedPicture)
            {
                continue;
            }

            if (double.TryParse(stream.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) && d > 0)
            {
                seconds = Math.Max(seconds, d);
            }
            else if (stream.Tags is not null && stream.Tags.TryGetValue("DURATION", out var tag) && TryParseTagDuration(tag, out var tagged))
            {
                seconds = Math.Max(seconds, tagged);
            }
        }

        return seconds > 0;
    }

    internal static bool TryParseTagDuration(string? value, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // TimeSpan accepts at most 7 fractional digits; Matroska writes 9.
        var text = value.Trim();
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0 && text.Length - dot - 1 > 7)
        {
            text = text[..(dot + 8)];
        }

        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span) && span > TimeSpan.Zero)
        {
            seconds = span.TotalSeconds;
            return true;
        }

        return false;
    }
}

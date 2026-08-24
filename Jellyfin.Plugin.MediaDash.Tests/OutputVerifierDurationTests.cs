using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Field report A2: TrackFixer's -c copy -map -0:N remux drops the container's longest stream (a
// trailing subtitle cue or a padded secondary audio) so Format.Duration legitimately shrinks even
// though the retained video is unchanged. Duration comparison must be against the video stream.
public sealed class OutputVerifierDurationTests
{
    [Fact]
    public void GetVideoDurationSeconds_PrefersVideoStreamDuration_OverContainerFormat()
    {
        var probe = new FfprobeData
        {
            Format = new FfprobeFormat { Duration = "3600" },
            Streams = new List<FfprobeStreamInfo>
            {
                new() { CodecType = "video", Duration = "3540" },
                // Trailing subtitle (or padded audio) that would otherwise dominate Format.Duration.
                new() { CodecType = "subtitle", Duration = "3600" }
            }
        };

        Assert.Equal(3540, OutputVerifier.GetVideoDurationSeconds(probe));
    }

    [Fact]
    public void GetVideoDurationSeconds_FallsBackToMkvDurationTag_WhenStreamDurationEmpty()
    {
        var probe = new FfprobeData
        {
            Format = new FfprobeFormat { Duration = "3600" },
            Streams = new List<FfprobeStreamInfo>
            {
                new()
                {
                    CodecType = "video",
                    Duration = null,
                    Tags = new Dictionary<string, string> { ["DURATION"] = "00:59:00.000000000" }
                },
                new() { CodecType = "subtitle", Duration = "3600" }
            }
        };

        Assert.Equal(3540, OutputVerifier.GetVideoDurationSeconds(probe));
    }

    [Fact]
    public void GetVideoDurationSeconds_FallsBackToFormatDuration_WhenNoVideoDurationAvailable()
    {
        var probe = new FfprobeData
        {
            Format = new FfprobeFormat { Duration = "3540" },
            Streams = new List<FfprobeStreamInfo>
            {
                new() { CodecType = "video", Duration = null, Tags = null }
            }
        };

        Assert.Equal(3540, OutputVerifier.GetVideoDurationSeconds(probe));
    }
}

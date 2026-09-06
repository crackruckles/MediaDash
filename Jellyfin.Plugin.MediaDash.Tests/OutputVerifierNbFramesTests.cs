using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Layer 2 of the OutputVerifier ladder (issue #39): when container duration disagrees between
/// source and remux — because the source's Format.Duration was derived from a stream we then
/// removed, or because the container simply lies — a matching video-stream <c>nb_frames</c> is
/// sufficient proof the video content is intact. These tests pin the extraction and the tolerance.
/// </summary>
public sealed class OutputVerifierNbFramesTests
{
    [Fact]
    public void GetVideoNbFrames_ReturnsParsedFrameCount()
    {
        var probe = new FfprobeData
        {
            Streams = new List<FfprobeStreamInfo>
            {
                new() { CodecType = "video", NbFrames = "129600" },
                new() { CodecType = "audio", NbFrames = "97200" }
            }
        };

        Assert.Equal(129600L, OutputVerifier.GetVideoNbFrames(probe));
    }

    [Fact]
    public void GetVideoNbFrames_NullWhenAbsent()
    {
        var probe = new FfprobeData
        {
            Streams = new List<FfprobeStreamInfo>
            {
                new() { CodecType = "video", NbFrames = null }
            }
        };

        Assert.Null(OutputVerifier.GetVideoNbFrames(probe));
    }

    [Fact]
    public void GetVideoNbFrames_NullWhenEmpty()
    {
        // ffprobe emits "N/A" as an empty string in JSON output for streams that don't carry the
        // count — parsing an empty string returns null so the caller falls through to Layer 3.
        var probe = new FfprobeData
        {
            Streams = new List<FfprobeStreamInfo>
            {
                new() { CodecType = "video", NbFrames = string.Empty }
            }
        };

        Assert.Null(OutputVerifier.GetVideoNbFrames(probe));
    }

    [Fact]
    public void GetVideoNbFrames_NullWhenZero()
    {
        // Zero is treated as "not populated" — a video stream with genuinely zero frames is
        // pathological (would fail the "has video" check earlier). Better to fall through.
        var probe = new FfprobeData
        {
            Streams = new List<FfprobeStreamInfo>
            {
                new() { CodecType = "video", NbFrames = "0" }
            }
        };

        Assert.Null(OutputVerifier.GetVideoNbFrames(probe));
    }

    [Fact]
    public void GetVideoNbFrames_IgnoresGarbage()
    {
        // Some containers embed non-integer strings ("N/A", "unknown"). Must not throw.
        var probe = new FfprobeData
        {
            Streams = new List<FfprobeStreamInfo>
            {
                new() { CodecType = "video", NbFrames = "N/A" }
            }
        };

        Assert.Null(OutputVerifier.GetVideoNbFrames(probe));
    }

    [Fact]
    public void GetVideoNbFrames_PicksVideoStream_NotAudio()
    {
        // Order of streams in the container is arbitrary — the extractor must find the video stream
        // by codec_type, not by index. This locks the invariant across container quirks.
        var probe = new FfprobeData
        {
            Streams = new List<FfprobeStreamInfo>
            {
                new() { CodecType = "audio", NbFrames = "97200" },
                new() { CodecType = "video", NbFrames = "129600" }
            }
        };

        Assert.Equal(129600L, OutputVerifier.GetVideoNbFrames(probe));
    }

    [Fact]
    public void RegressionScenario_Issue39_SpiderVerse_FrameCountsMatch()
    {
        // Exact numbers from GBernard314's bug report (2026-09-01): original duration reported as
        // 8954.9s (from a bogus Format.Duration), new file 8406.4s. Slack was 179.1s so duration
        // check fails. With nb_frames on both files matching (the video was untouched — this was
        // a -c copy remux dropping an unwanted audio track), Layer 2 rescues the verification.
        //
        // We can't drive VerifyAsync directly without a live ffprobe, but the frame-count
        // extraction is what the rescue relies on — pin it explicitly for a Blu-ray-shaped file.
        // 24 fps × 8406.4 seconds ≈ 201,754 frames. Both sides carry the same count when the
        // rip is honest.
        var original = new FfprobeData
        {
            Format = new FfprobeFormat { Duration = "8954.9" },
            Streams = new List<FfprobeStreamInfo>
            {
                new() { CodecType = "video", Duration = null, NbFrames = "201754" },
                new() { CodecType = "audio", NbFrames = "393792" },
                new() { CodecType = "audio", NbFrames = "393792" },
                new() { CodecType = "audio", NbFrames = "393792" },
                new() { CodecType = "audio", NbFrames = "393792" }
            }
        };
        var remuxed = new FfprobeData
        {
            Format = new FfprobeFormat { Duration = "8406.4" },
            Streams = new List<FfprobeStreamInfo>
            {
                new() { CodecType = "video", Duration = "8406.4", NbFrames = "201754" },
                new() { CodecType = "audio", NbFrames = "393792" }
            }
        };

        Assert.Equal(201754L, OutputVerifier.GetVideoNbFrames(original));
        Assert.Equal(201754L, OutputVerifier.GetVideoNbFrames(remuxed));
        Assert.Equal(OutputVerifier.GetVideoNbFrames(original), OutputVerifier.GetVideoNbFrames(remuxed));
    }
}

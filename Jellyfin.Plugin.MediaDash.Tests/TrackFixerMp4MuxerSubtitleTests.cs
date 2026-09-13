using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Issue #56: HandBrake-authored .m4v with a VobSub subtitle track was failing every TrackFixer
// remux with "Tag text incompatible with output codec id '98314'" — the ipod muxer refuses
// bitmap subs under -c copy. ComputeMuxerIncompatibleSubtitleIndexes decides which subtitle
// stream indexes to fold into the remux's negative-map list so the fix succeeds instead of
// leaving a raw ffmpeg error on the Errors tab.
public class TrackFixerMp4MuxerSubtitleTests
{
    [Fact]
    public void VobSubInM4vIsFlaggedForDrop()
    {
        var probe = Probe(
            Sub(0, "dvd_subtitle"),
            Aud(1, "aac"));

        var dropped = TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "m4v");

        Assert.Equal(new[] { 0 }, dropped);
    }

    [Fact]
    public void PgsAndDvbSubsAreFlaggedForDropInMp4()
    {
        var probe = Probe(
            Sub(0, "hdmv_pgs_subtitle"),
            Sub(1, "dvb_subtitle"),
            Aud(2, "aac"));

        var dropped = TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "mp4");

        Assert.Equal(new[] { 0, 1 }, dropped);
    }

    [Fact]
    public void MovTextInMp4IsKept()
    {
        var probe = Probe(
            Sub(0, "mov_text"),
            Aud(1, "aac"));

        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "mp4"));
    }

    [Fact]
    public void MkvOutputNeverDropsSubs()
    {
        var probe = Probe(
            Sub(0, "dvd_subtitle"),
            Sub(1, "hdmv_pgs_subtitle"),
            Aud(2, "flac"));

        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, "mkv"));
    }

    [Fact]
    public void LeadingDotOnExtensionIsAccepted()
    {
        var probe = Probe(Sub(0, "dvd_subtitle"), Aud(1, "aac"));
        Assert.Equal(new[] { 0 }, TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, ".m4v"));
    }

    [Fact]
    public void EmptyOrMissingStreamsReturnsEmpty()
    {
        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(new FfprobeData(), "m4v"));
        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(Probe(), "m4v"));
        Assert.Empty(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(Probe(Aud(0, "aac")), string.Empty));
    }

    private static FfprobeData Probe(params FfprobeStreamInfo[] streams)
        => new() { Streams = new List<FfprobeStreamInfo>(streams) };

    private static FfprobeStreamInfo Sub(int index, string codec)
        => new() { Index = index, CodecType = "subtitle", CodecName = codec };

    private static FfprobeStreamInfo Aud(int index, string codec)
        => new() { Index = index, CodecType = "audio", CodecName = codec };
}

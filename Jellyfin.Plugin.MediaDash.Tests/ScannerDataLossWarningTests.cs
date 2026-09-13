using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Guardrail on the data-loss consent path: the scanners must emit a warning of the exact shape
// FixTask.RollbackAutoQueuedBlockingWarnings + Issue.HasBlockingWarnings recognise, or the
// safeguard silently no-ops and users lose bitmap subs without consent. These tests wire the
// public helper directly (no BaseItem plumbing needed) and assert the DetailsJson-compatible
// shape that ends up on the wire.
public class ScannerDataLossWarningTests
{
    [Fact]
    public void M4vWithVobSub_EmitsBlockingWarning()
    {
        var probe = Probe(Sub(0, "dvd_subtitle"), Aud(1, "aac"));
        var warnings = AudioLanguageScanner.BuildDataLossWarnings(probe, @"C:\media\show.m4v");

        Assert.Single(warnings);
        var w = warnings[0];
        Assert.Equal("bitmap-subs-dropped", w.Code);
        Assert.True(w.Blocking);
        Assert.Contains("bitmap subtitle track", w.Message);
        Assert.Contains(".m4v", w.Message);
        Assert.Contains("dvd_subtitle", w.Message);
    }

    [Fact]
    public void Mp4WithMultipleBitmapSubs_ReportsAllCodecs()
    {
        var probe = Probe(
            Sub(0, "hdmv_pgs_subtitle"),
            Sub(1, "dvb_subtitle"),
            Aud(2, "aac"));
        var warnings = AudioLanguageScanner.BuildDataLossWarnings(probe, @"C:\media\show.mp4");

        Assert.Single(warnings);
        Assert.Contains("2 bitmap subtitle tracks", warnings[0].Message);
        Assert.Contains("hdmv_pgs_subtitle", warnings[0].Message);
        Assert.Contains("dvb_subtitle", warnings[0].Message);
    }

    [Fact]
    public void MkvWithBitmapSubs_NoWarning()
    {
        // MKV can hold bitmap subs under -c copy; no data loss when the same-container remux
        // runs. Warning must NOT fire — otherwise users get a spurious approval gate on MKV files.
        var probe = Probe(Sub(0, "dvd_subtitle"), Aud(1, "aac"));
        Assert.Empty(AudioLanguageScanner.BuildDataLossWarnings(probe, @"C:\media\show.mkv"));
    }

    [Fact]
    public void Mp4WithTextSubsOnly_NoWarning()
    {
        // mov_text is the sub codec MP4 natively supports — remux copies it fine, no loss.
        var probe = Probe(Sub(0, "mov_text"), Aud(1, "aac"));
        Assert.Empty(AudioLanguageScanner.BuildDataLossWarnings(probe, @"C:\media\show.mp4"));
    }

    [Fact]
    public void EmitsJsonShapeThatHasBlockingWarningsRecognises()
    {
        // End-to-end serialisation round-trip: DTO on the wire is what FixTask + Issue read.
        // A silent field-name mismatch (e.g. Blocking vs blocking casing) would break the guard.
        var probe = Probe(Sub(0, "dvd_subtitle"), Aud(1, "aac"));
        var warnings = AudioLanguageScanner.BuildDataLossWarnings(probe, @"C:\media\show.m4v");
        var json = JsonSerializer.Serialize(new { warnings });
        var issue = new Jellyfin.Plugin.MediaDash.Data.Issue { DetailsJson = json };
        Assert.True(issue.HasBlockingWarnings);
    }

    private static FfprobeData Probe(params FfprobeStreamInfo[] streams)
        => new() { Streams = new List<FfprobeStreamInfo>(streams) };

    private static FfprobeStreamInfo Sub(int index, string codec)
        => new() { Index = index, CodecType = "subtitle", CodecName = codec };

    private static FfprobeStreamInfo Aud(int index, string codec)
        => new() { Index = index, CodecType = "audio", CodecName = codec };
}

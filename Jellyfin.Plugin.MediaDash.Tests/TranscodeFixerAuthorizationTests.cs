using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Bug-pattern audit round 4 P-01 (2026-09-19): TranscodeFixer.BuildArgs applied the language
/// allow-lists to audio/subtitle map filters unconditionally. If the user had AudioLanguage
/// and/or SubtitleLanguage set to Off (or on the FixerDisabled list) but had non-empty
/// AllowedAudioLanguages / AllowedSubtitleLanguages, approving a Quality re-encode of one file
/// silently removed unwanted-language tracks — an unrelated cleanup executed without consent.
/// These tests pin the new per-type authorization gate.
/// </summary>
public class TranscodeFixerAuthorizationTests
{
    [Fact]
    public void IsTypeAuthorized_TrueWhenModeIsNotOffAndNotOnDisableList()
    {
        var cfg = NewConfig();
        cfg.AudioFixMode = FixMode.ManualApprove;
        cfg.FixerDisabled = System.Array.Empty<string>();
        Assert.True(TranscodeFixer.IsTypeAuthorized(cfg, IssueType.AudioLanguage));
    }

    [Fact]
    public void IsTypeAuthorized_FalseWhenModeIsOff()
    {
        var cfg = NewConfig();
        cfg.AudioFixMode = FixMode.Off;
        Assert.False(TranscodeFixer.IsTypeAuthorized(cfg, IssueType.AudioLanguage));
    }

    [Fact]
    public void IsTypeAuthorized_FalseWhenOnDisableList()
    {
        var cfg = NewConfig();
        cfg.AudioFixMode = FixMode.Automatic;
        cfg.FixerDisabled = new[] { "AudioLanguage" };
        Assert.False(TranscodeFixer.IsTypeAuthorized(cfg, IssueType.AudioLanguage));
    }

    [Fact]
    public void IsTypeAuthorized_NullDisableListDoesNotThrow()
    {
        var cfg = NewConfig();
        cfg.AudioFixMode = FixMode.ManualApprove;
        cfg.FixerDisabled = null!;
        Assert.True(TranscodeFixer.IsTypeAuthorized(cfg, IssueType.AudioLanguage));
    }

    [Fact]
    public void BuildArgs_UnauthorizedAudioCleanup_KeepsAllAudioTracks()
    {
        // Exact P-01 scenario: user has AllowedAudioLanguages=["eng"] but AudioFixMode=Off
        // AND FixerDisabled includes "AudioLanguage". Pre-fix: French audio silently dropped
        // during the Quality re-encode. Post-fix: both English AND French audio are mapped.
        var cfg = NewConfig();
        cfg.AllowedAudioLanguages = new[] { "eng" };
        cfg.AudioFixMode = FixMode.Off;
        cfg.FixerDisabled = new[] { "AudioLanguage" };

        var probe = MkvProbe(
            Vid(0, "h264"),
            Aud(1, "aac", "eng"),
            Aud(2, "aac", "fre"));

        var args = TranscodeFixer.BuildArgs(
            inputPath: "/tmp/movie.mkv",
            tempPath: "/tmp/out.mkv",
            probe: probe,
            video: probe.Streams![0],
            config: cfg,
            needsDownscale: false,
            targetContainer: "mkv",
            hardwareEncoder: null,
            vaapiDevice: null);

        // Both audio streams must appear in the -map list — indexes 1 (eng) AND 2 (fre).
        var audioMaps = ExtractMappedStreams(args);
        Assert.Contains("0:1", audioMaps);
        Assert.Contains("0:2", audioMaps);
    }

    [Fact]
    public void BuildArgs_AuthorizedAudioCleanup_DropsUnwantedLanguage()
    {
        // Same probe as above but this time the user HAS authorized the cleanup:
        // AudioFixMode=ManualApprove + AudioLanguage not on disable list. Then the language
        // filter runs and the French track is correctly excluded from the -map list. This
        // is the pre-existing behavior — the fix only affected the unauthorized branch.
        var cfg = NewConfig();
        cfg.AllowedAudioLanguages = new[] { "eng" };
        cfg.AudioFixMode = FixMode.ManualApprove;
        cfg.FixerDisabled = System.Array.Empty<string>();

        var probe = MkvProbe(
            Vid(0, "h264"),
            Aud(1, "aac", "eng"),
            Aud(2, "aac", "fre"));

        var args = TranscodeFixer.BuildArgs(
            inputPath: "/tmp/movie.mkv",
            tempPath: "/tmp/out.mkv",
            probe: probe,
            video: probe.Streams![0],
            config: cfg,
            needsDownscale: false,
            targetContainer: "mkv",
            hardwareEncoder: null,
            vaapiDevice: null);

        var audioMaps = ExtractMappedStreams(args);
        Assert.Contains("0:1", audioMaps);
        Assert.DoesNotContain("0:2", audioMaps);
    }

    [Fact]
    public void BuildArgs_UnauthorizedSubtitleCleanup_KeepsAllSubtitleTracksOnMkv()
    {
        // Subtitle branch of the same P-01 fix. Only relevant for MKV target (subs are stripped
        // entirely on MP4 per the "MP4 subtitle support is too patchy" comment).
        var cfg = NewConfig();
        cfg.AllowedSubtitleLanguages = new[] { "eng" };
        cfg.SubtitleFixMode = FixMode.Off;
        cfg.FixerDisabled = new[] { "SubtitleLanguage" };

        var probe = MkvProbe(
            Vid(0, "h264"),
            Aud(1, "aac", "eng"),
            Sub(2, "subrip", "eng"),
            Sub(3, "subrip", "fre"));

        var args = TranscodeFixer.BuildArgs(
            inputPath: "/tmp/movie.mkv",
            tempPath: "/tmp/out.mkv",
            probe: probe,
            video: probe.Streams![0],
            config: cfg,
            needsDownscale: false,
            targetContainer: "mkv",
            hardwareEncoder: null,
            vaapiDevice: null);

        var maps = ExtractMappedStreams(args);
        Assert.Contains("0:2", maps);
        Assert.Contains("0:3", maps);
    }

    [Fact]
    public void BuildArgs_ManualApproveWithoutCompanion_KeepsAllAudioTracks()
    {
        // 02-B consent-defect fix: `ManualApprove` mode with a nonempty language allow-list is
        // NOT authorisation for incidental cleanup during a Transcode fix. Only an actually-
        // queued AudioLanguage issue on the same path (signalled via
        // `hasApprovedAudioCompanion=true`) permits the drop. The default parameter value is
        // `true` so pre-02-B tests keep working, but production callers now compute the flag
        // from the live queue (see `TranscodeFixer.FixAsync` line ~205).
        var cfg = NewConfig();
        cfg.AllowedAudioLanguages = new[] { "eng" };
        cfg.AudioFixMode = FixMode.ManualApprove;
        cfg.FixerDisabled = System.Array.Empty<string>();

        var probe = MkvProbe(
            Vid(0, "h264"),
            Aud(1, "aac", "eng"),
            Aud(2, "aac", "fre"));

        var args = TranscodeFixer.BuildArgs(
            inputPath: "/tmp/movie.mkv",
            tempPath: "/tmp/out.mkv",
            probe: probe,
            video: probe.Streams![0],
            config: cfg,
            needsDownscale: false,
            targetContainer: "mkv",
            hardwareEncoder: null,
            vaapiDevice: null,
            hasApprovedAudioCompanion: false,
            hasApprovedSubtitleCompanion: false);

        // Both audio streams stay — the French track cannot be dropped without an actual
        // queued AudioLanguage issue on this path.
        var audioMaps = ExtractMappedStreams(args);
        Assert.Contains("0:1", audioMaps);
        Assert.Contains("0:2", audioMaps);
    }

    [Fact]
    public void BuildArgs_ManualApproveWithCompanion_DropsUnwantedLanguage()
    {
        // Same scenario as above but with `hasApprovedAudioCompanion=true` — the user actually
        // approved an AudioLanguage issue on the same path, so the incidental cleanup is
        // now authorised. French is dropped.
        var cfg = NewConfig();
        cfg.AllowedAudioLanguages = new[] { "eng" };
        cfg.AudioFixMode = FixMode.ManualApprove;
        cfg.FixerDisabled = System.Array.Empty<string>();

        var probe = MkvProbe(
            Vid(0, "h264"),
            Aud(1, "aac", "eng"),
            Aud(2, "aac", "fre"));

        var args = TranscodeFixer.BuildArgs(
            inputPath: "/tmp/movie.mkv",
            tempPath: "/tmp/out.mkv",
            probe: probe,
            video: probe.Streams![0],
            config: cfg,
            needsDownscale: false,
            targetContainer: "mkv",
            hardwareEncoder: null,
            vaapiDevice: null,
            hasApprovedAudioCompanion: true,
            hasApprovedSubtitleCompanion: false);

        var audioMaps = ExtractMappedStreams(args);
        Assert.Contains("0:1", audioMaps);
        Assert.DoesNotContain("0:2", audioMaps);
    }

    [Fact]
    public void BuildArgs_AutomaticIgnoresCompanionFlag_DropsUnwantedLanguage()
    {
        // `Automatic` mode is bulk-authorisation and the companion flag is not consulted for it.
        // A user who set `AudioFixMode = Automatic` gets incidental cleanup during any transcode
        // regardless of whether an AudioLanguage row is queued for this specific file.
        var cfg = NewConfig();
        cfg.AllowedAudioLanguages = new[] { "eng" };
        cfg.AudioFixMode = FixMode.Automatic;
        cfg.FixerDisabled = System.Array.Empty<string>();

        var probe = MkvProbe(
            Vid(0, "h264"),
            Aud(1, "aac", "eng"),
            Aud(2, "aac", "fre"));

        var args = TranscodeFixer.BuildArgs(
            inputPath: "/tmp/movie.mkv",
            tempPath: "/tmp/out.mkv",
            probe: probe,
            video: probe.Streams![0],
            config: cfg,
            needsDownscale: false,
            targetContainer: "mkv",
            hardwareEncoder: null,
            vaapiDevice: null,
            hasApprovedAudioCompanion: false,
            hasApprovedSubtitleCompanion: false);

        var audioMaps = ExtractMappedStreams(args);
        Assert.Contains("0:1", audioMaps);
        Assert.DoesNotContain("0:2", audioMaps);
    }

    [Fact]
    public void BuildArgs_EmptyAllowList_KeepsAllTracks_Regardless()
    {
        // Regression guard: an empty allow-list must always mean "keep all", both pre- and
        // post-fix. Authorization gate must not accidentally break this case.
        var cfg = NewConfig();
        cfg.AllowedAudioLanguages = System.Array.Empty<string>();
        cfg.AudioFixMode = FixMode.Automatic;
        cfg.FixerDisabled = System.Array.Empty<string>();

        var probe = MkvProbe(
            Vid(0, "h264"),
            Aud(1, "aac", "eng"),
            Aud(2, "aac", "fre"));

        var args = TranscodeFixer.BuildArgs(
            inputPath: "/tmp/movie.mkv",
            tempPath: "/tmp/out.mkv",
            probe: probe,
            video: probe.Streams![0],
            config: cfg,
            needsDownscale: false,
            targetContainer: "mkv",
            hardwareEncoder: null,
            vaapiDevice: null);

        var audioMaps = ExtractMappedStreams(args);
        Assert.Contains("0:1", audioMaps);
        Assert.Contains("0:2", audioMaps);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildArgs_OriginalSelectionKeepsOriginalTracksOrAllWhenUnknown(bool originalKnown)
    {
        var cfg = NewConfig();
        cfg.AudioFixMode = FixMode.Automatic;
        cfg.SubtitleFixMode = FixMode.Automatic;
        cfg.AllowedAudioLanguages = ["eng", "orig"];
        cfg.AllowedSubtitleLanguages = ["orig"];
        var original = Aud(2, "aac", "jpn");
        if (originalKnown) original.Disposition = new Dictionary<string, int> { ["original"] = 1 };
        var probe = MkvProbe(Vid(0, "h264"), Aud(1, "aac", "eng"), original, Sub(3, "subrip", "jpn"), Aud(4, "aac", "fra"));
        var maps = ExtractMappedStreams(TranscodeFixer.BuildArgs("in.mkv", "out.mkv", probe, probe.Streams![0], cfg, false, "mkv", null, null));
        Assert.Contains("0:1", maps);
        Assert.Contains("0:2", maps);
        Assert.Contains("0:3", maps);
        Assert.Equal(!originalKnown, maps.Contains("0:4"));
    }

    [Fact]
    public void BuildArgs_ResolvesOriginalFromItemMetadata()
    {
        var cfg = NewConfig();
        cfg.AudioFixMode = FixMode.Automatic;
        cfg.AllowedAudioLanguages = ["eng", "orig"];
        var probe = MkvProbe(Vid(0, "h264"), Aud(1, "aac", "eng"), Aud(2, "aac", "kor"), Aud(3, "aac", "fra"));
        var maps = ExtractMappedStreams(TranscodeFixer.BuildArgs("in.mkv", "out.mkv", probe, probe.Streams![0], cfg, false, "mkv", null, null, item: new OriginalLanguageTests.MetadataVideo()));
        Assert.Contains("0:2", maps);
        Assert.DoesNotContain("0:3", maps);
    }

    [Theory]
    [InlineData("language")]
    [InlineData("unknown-original")]
    [InlineData("mode")]
    [InlineData("approval")]
    public void RetainedTracksRecheckRestoresTracksWhenConsentChanges(string change)
    {
        var cfg = NewConfig();
        cfg.AudioFixMode = FixMode.ManualApprove;
        cfg.AllowedAudioLanguages = ["eng"];
        var probe = MkvProbe(Vid(0, "h264"), Aud(1, "aac", "eng"), Aud(2, "aac", "jpn"));
        Assert.DoesNotContain(TranscodeFixer.GetRetainedTracks(probe, cfg, "mkv", null, true, false), s => s.Index == 2);
        if (change == "language") cfg.AllowedAudioLanguages = ["eng", "jpn"];
        if (change == "unknown-original") cfg.AllowedAudioLanguages = ["eng", "orig"];
        if (change == "mode") cfg.AudioFixMode = FixMode.Off;
        Assert.Contains(TranscodeFixer.GetRetainedTracks(probe, cfg, "mkv", null, change != "approval", false), s => s.Index == 2);
    }

    [Fact]
    public void ReencodeVerificationAllowsCodecChangeButRejectsMissingRetainedTrack()
    {
        var source = MkvProbe(Aud(1, "flac", "eng"), Aud(2, "flac", "jpn"));
        var output = MkvProbe(Aud(1, "aac", "eng"), Aud(2, "aac", "jpn"));
        Assert.True(TrackFixer.RetainedTracksMatch(source, output, [], allowAudioCodecChange: true));
        Assert.False(TrackFixer.RetainedTracksMatch(source, MkvProbe(Aud(1, "aac", "eng")), [], allowAudioCodecChange: true));
        Assert.False(TrackFixer.RetainedTracksMatch(source, output, []));
        Assert.False(TrackFixer.RetainedTracksMatch(MkvProbe(Sub(1, "subrip", "eng")), MkvProbe(Sub(1, "ass", "eng")), [], allowAudioCodecChange: true));
    }

    [Theory]
    [InlineData(FixMode.Off)]
    [InlineData(FixMode.Automatic)]
    public void BuildArgs_KeepsHearingImpairedSubtitlesAndUnauthorizedAudio(FixMode audioMode)
    {
        var cfg = NewConfig();
        cfg.AudioFixMode = audioMode;
        cfg.SubtitleFixMode = FixMode.Automatic;
        cfg.AllowedAudioLanguages = ["eng"];
        cfg.AllowedSubtitleLanguages = ["eng"];
        cfg.SubtitleHearingImpairedMode = true;
        var subtitle = Sub(3, "subrip", "fra");
        subtitle.Disposition = new Dictionary<string, int> { ["hearing_impaired"] = 1 };
        var probe = MkvProbe(Vid(0, "h264"), Aud(1, "aac", "eng"), Aud(2, "aac", "fra"), subtitle);
        var maps = ExtractMappedStreams(TranscodeFixer.BuildArgs("in.mkv", "out.mkv", probe, probe.Streams![0], cfg, false, "mkv", null, null));
        Assert.Contains("0:3", maps);
        Assert.Equal(audioMode == FixMode.Off, maps.Contains("0:2"));
    }

    private static PluginConfiguration NewConfig() => new()
    {
        // Config constructor defaults handle almost everything; only override what the tests care about.
        TargetContainer = "mkv",
        PreferredCodec = "h265",
        MaxResolutionHeight = 1080,
        MaxBitrateMbpsAt1080p = 8
    };

    private static FfprobeData MkvProbe(params FfprobeStreamInfo[] streams)
        => new() { Format = new FfprobeFormat { FormatName = "matroska,webm" }, Streams = new List<FfprobeStreamInfo>(streams) };

    private static FfprobeStreamInfo Vid(int index, string codec)
        => new() { Index = index, CodecType = "video", CodecName = codec, Width = 1920, Height = 1080 };

    // Language is a computed getter over Tags["language"], not a direct setter, so seed it there.
    private static FfprobeStreamInfo Aud(int index, string codec, string lang)
        => new() { Index = index, CodecType = "audio", CodecName = codec, Tags = new Dictionary<string, string> { ["language"] = lang } };

    private static FfprobeStreamInfo Sub(int index, string codec, string lang)
        => new() { Index = index, CodecType = "subtitle", CodecName = codec, Tags = new Dictionary<string, string> { ["language"] = lang } };

    // Pulls the argument values that follow every `-map` flag in the ffmpeg args list. The map
    // list is the surface P-01 mutates; nothing else in the args changes with the fix.
    private static List<string> ExtractMappedStreams(List<string> args)
    {
        var maps = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-map")
            {
                maps.Add(args[i + 1]);
            }
        }

        return maps;
    }
}

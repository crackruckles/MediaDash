using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class TrackSafetyTests
{
    private static FfprobeStreamInfo Stream(int index, string type, string? lang)
    {
        return new FfprobeStreamInfo
        {
            Index = index,
            CodecType = type,
            Tags = lang is null ? null : new Dictionary<string, string> { ["language"] = lang }
        };
    }

    private static FfprobeData Probe(params FfprobeStreamInfo[] streams) => new() { Streams = streams };

    private static PluginConfiguration Config(string[] audio, string[]? subs = null)
    {
        return new PluginConfiguration { AllowedAudioLanguages = audio, AllowedSubtitleLanguages = subs ?? audio };
    }

    [Fact]
    public void NeverRemovesTheOnlyAudioTrack()
    {
        // Single audio track in a disallowed language must be kept (safety invariant #2).
        var probe = Probe(Stream(0, "video", null), Stream(1, "audio", "fra"));
        var result = TrackFixer.ComputeRemovableIndexes(probe, IssueType.AudioLanguage, Config(["eng"]));
        Assert.Empty(result);
    }

    [Fact]
    public void NeverRemovesAllAudioWhenNoneAreAllowed()
    {
        var probe = Probe(Stream(0, "video", null), Stream(1, "audio", "fra"), Stream(2, "audio", "deu"));
        var result = TrackFixer.ComputeRemovableIndexes(probe, IssueType.AudioLanguage, Config(["eng"]));
        Assert.Empty(result);
    }

    [Fact]
    public void RemovesDisallowedAudioWhenAnAllowedTrackRemains()
    {
        var probe = Probe(Stream(0, "video", null), Stream(1, "audio", "eng"), Stream(2, "audio", "fra"), Stream(3, "audio", "deu"));
        var result = TrackFixer.ComputeRemovableIndexes(probe, IssueType.AudioLanguage, Config(["eng"]));
        Assert.Equal([2, 3], result);
    }

    [Fact]
    public void UntaggedAudioIsNeverRemoved()
    {
        var probe = Probe(Stream(0, "video", null), Stream(1, "audio", "eng"), Stream(2, "audio", null));
        var result = TrackFixer.ComputeRemovableIndexes(probe, IssueType.AudioLanguage, Config(["eng"]));
        Assert.Empty(result);
    }

    [Fact]
    public void RemovesDisallowedSubtitles()
    {
        var probe = Probe(
            Stream(0, "video", null),
            Stream(1, "audio", "eng"),
            Stream(2, "subtitle", "eng"),
            Stream(3, "subtitle", "fra"),
            Stream(4, "subtitle", null));
        var result = TrackFixer.ComputeRemovableIndexes(probe, IssueType.SubtitleLanguage, Config(["eng"]));
        Assert.Equal([3], result);
    }
}

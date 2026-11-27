using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class OriginalLanguageTests
{
    private static FfprobeStreamInfo Stream(int index, string kind, string language, bool original = false)
        => new() { Index = index, CodecType = kind, CodecName = kind == "subtitle" ? "subrip" : "aac",
            Tags = new Dictionary<string, string> { ["language"] = language },
            Disposition = new Dictionary<string, int> { ["original"] = original ? 1 : 0 } };

    private static PluginConfiguration Config() => new()
    {
        AllowedAudioLanguages = ["eng", "orig"], AllowedSubtitleLanguages = ["eng", "orig"]
    };

    [Theory]
    [InlineData(IssueType.AudioLanguage, 3)]
    [InlineData(IssueType.SubtitleLanguage, 6)]
    public void FixKeepsEnglishAndFlaggedOriginal(IssueType type, int removed)
    {
        var probe = new FfprobeData { Streams = [Stream(1, "audio", "eng"), Stream(2, "audio", "kor", true),
            Stream(3, "audio", "fra"), Stream(4, "subtitle", "eng"), Stream(5, "subtitle", "kor"), Stream(6, "subtitle", "fra")] };
        Assert.Equal([removed], TrackFixer.ComputeRemovableIndexes(probe, type, Config()));
    }

    [Fact]
    public void MetadataProtectsUnflaggedOriginalAndUsesInheritedLanguage()
    {
        var probe = new FfprobeData { Streams = [Stream(1, "audio", "eng"), Stream(2, "audio", "kor"), Stream(3, "audio", "fra")] };
        Assert.Equal([3], TrackFixer.ComputeRemovableIndexes(probe, IssueType.AudioLanguage, Config(), new MetadataVideo()));
    }

    [Theory]
    [InlineData(IssueType.AudioLanguage)]
    [InlineData(IssueType.SubtitleLanguage)]
    public void UnknownOriginalNeverAuthorizesRemoval(IssueType type)
    {
        var probe = new FfprobeData { Streams = [Stream(1, "audio", "eng"), Stream(2, "audio", "kor"), Stream(3, "subtitle", "kor")] };
        Assert.Empty(TrackFixer.ComputeRemovableIndexes(probe, type, Config()));
        Assert.Null(LanguageHelper.ResolveLanguages(["orig"], new Video(), probe));
    }

    [Theory]
    [InlineData(IssueType.AudioLanguage)]
    [InlineData(IssueType.SubtitleLanguage)]
    public void FailedOriginalMetadataLookupCannotAuthorizeRemovalUsingEnglishFlag(IssueType type)
    {
        var probe = new FfprobeData { Streams = [Stream(1, "audio", "eng", true), Stream(2, "audio", "spa"),
            Stream(3, "subtitle", "eng"), Stream(4, "subtitle", "spa")] };
        Assert.Throws<InvalidOperationException>(() => TrackFixer.ComputeRemovableIndexes(probe, type, Config(), new UnavailableMetadataVideo()));
    }

    [Fact]
    public void ExplicitLanguageSelectionDoesNotRequireOriginalMetadata()
    {
        var probe = new FfprobeData { Streams = [Stream(1, "audio", "eng"), Stream(2, "audio", "spa")] };
        var config = Config();
        config.AllowedAudioLanguages = ["spa"];
        Assert.Equal([1], TrackFixer.ComputeRemovableIndexes(probe, IssueType.AudioLanguage, config, new UnavailableMetadataVideo()));
    }

    [Fact]
    public void AllOriginalLanguagesAndEquivalentSubtitlesAreKept()
    {
        var probe = new FfprobeData { Streams = [Stream(1, "audio", "nor", true), Stream(2, "audio", "jpn", true),
            Stream(3, "subtitle", "nob"), Stream(4, "subtitle", "jpn"), Stream(5, "subtitle", "fra")] };
        Assert.Equal([5], TrackFixer.ComputeRemovableIndexes(probe, IssueType.SubtitleLanguage, Config()));
        Assert.Equal(new[] { "nor", "jpn" }, LanguageHelper.ResolveLanguages(["orig"], null, probe));
    }

    [Fact]
    public void OriginalOnlyResolvesToRealProviderLanguage()
    {
        Assert.Equal(new[] { "kor" }, LanguageHelper.ResolveLanguages(["ORIG"], new MetadataVideo(), null));
    }

    [Fact]
    public void HearingImpairedSubtitlesRemainProtectedAtFixTime()
    {
        var subtitle = Stream(2, "subtitle", "fra");
        subtitle.Disposition = new Dictionary<string, int> { ["hearing_impaired"] = 1 };
        var config = Config(); config.AllowedSubtitleLanguages = ["eng"]; config.SubtitleHearingImpairedMode = true;
        Assert.Empty(TrackFixer.ComputeRemovableIndexes(new FfprobeData { Streams = [subtitle] }, IssueType.SubtitleLanguage, config));
    }

    [Fact]
    public void VerificationRejectsMissingOriginalEvenWhenEnglishRemains()
    {
        var english = Stream(1, "audio", "eng");
        var korean = Stream(2, "audio", "kor", true);
        var french = Stream(3, "audio", "fra");
        var source = new FfprobeData { Streams = [english, korean, french] };
        Assert.False(TrackFixer.RetainedTracksMatch(source, new FfprobeData { Streams = [english] }, [3]));
        Assert.True(TrackFixer.RetainedTracksMatch(source, new FfprobeData { Streams = [english, korean] }, [3]));
    }

    public class MetadataVideo : Video
    {
        public string OriginalLanguage => "fr";
        public string GetInheritedOriginalLanguage() => "ko";
    }

    public class UnavailableMetadataVideo : Video
    {
        public string GetInheritedOriginalLanguage() => throw new IOException("Original-language parent lookup failed");
    }
}

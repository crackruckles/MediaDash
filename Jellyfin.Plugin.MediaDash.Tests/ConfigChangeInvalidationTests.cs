using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Guards issue #64: a config edit must invalidate exactly the Detected rows that a scanner
// re-reading the changed setting would now emit differently.
public class ConfigChangeInvalidationTests
{
    [Fact]
    public void ChangedFields_IdenticalConfigsReturnEmpty()
    {
        var a = new PluginConfiguration();
        var b = new PluginConfiguration();
        Assert.Empty(ConfigChangeInvalidation.ChangedFields(a, b));
    }

    [Fact]
    public void ChangedFields_ArrayContentDifference()
    {
        // The issue #64 case exactly: user adds "jpn" to the allowed list, arrays are new
        // references so ReferenceEquals is false — content compare must catch this.
        var a = new PluginConfiguration { AllowedAudioLanguages = new[] { "eng" } };
        var b = new PluginConfiguration { AllowedAudioLanguages = new[] { "eng", "jpn" } };
        Assert.Contains(nameof(PluginConfiguration.AllowedAudioLanguages), ConfigChangeInvalidation.ChangedFields(a, b));
    }

    [Fact]
    public void ChangedFields_SameArrayContentDifferentInstance()
    {
        // Reload/save produces a fresh array with identical contents — must NOT be flagged.
        var a = new PluginConfiguration { AllowedAudioLanguages = new[] { "eng", "jpn" } };
        var b = new PluginConfiguration { AllowedAudioLanguages = new[] { "eng", "jpn" } };
        Assert.DoesNotContain(nameof(PluginConfiguration.AllowedAudioLanguages), ConfigChangeInvalidation.ChangedFields(a, b));
    }

    [Fact]
    public void ChangedFields_ScalarDifference()
    {
        var a = new PluginConfiguration { MaxResolutionHeight = 1080 };
        var b = new PluginConfiguration { MaxResolutionHeight = 2160 };
        Assert.Contains(nameof(PluginConfiguration.MaxResolutionHeight), ConfigChangeInvalidation.ChangedFields(a, b));
    }

    [Fact]
    public void ChangedFields_EnumDifference()
    {
        var a = new PluginConfiguration { AudioFixMode = FixMode.Off };
        var b = new PluginConfiguration { AudioFixMode = FixMode.ManualApprove };
        Assert.Contains(nameof(PluginConfiguration.AudioFixMode), ConfigChangeInvalidation.ChangedFields(a, b));
    }

    [Fact]
    public void AffectedTypes_AllowedAudioLanguagesMapsToAudioLanguage()
    {
        var affected = ConfigChangeInvalidation.AffectedTypes(new[] { nameof(PluginConfiguration.AllowedAudioLanguages) });
        Assert.NotNull(affected);
        Assert.Equal(new[] { IssueType.AudioLanguage }, affected!.OrderBy(t => t));
    }

    [Fact]
    public void AffectedTypes_AllowedSubtitleLanguagesMapsToBothSubtitleTypes()
    {
        // Missing-subtitle scanner also reads AllowedSubtitleLanguages, so both surfaces are stale.
        var affected = ConfigChangeInvalidation.AffectedTypes(new[] { nameof(PluginConfiguration.AllowedSubtitleLanguages) });
        Assert.NotNull(affected);
        Assert.Contains(IssueType.SubtitleLanguage, affected!);
        Assert.Contains(IssueType.MissingSubtitles, affected);
    }

    [Fact]
    public void AffectedTypes_UnrelatedFieldReturnsEmpty()
    {
        // Retention days doesn't change what any scanner emits (only how the recycle bin behaves),
        // so a scan burn on retention edits would be wasted work.
        var affected = ConfigChangeInvalidation.AffectedTypes(new[] { nameof(PluginConfiguration.RecycleBinRetentionDays) });
        Assert.NotNull(affected);
        Assert.Empty(affected!);
    }

    [Fact]
    public void AffectedTypes_EnabledLibrariesReturnsNullForFullRescan()
    {
        // Changes the item universe for every scanner — targeted rescan can't cover it.
        var affected = ConfigChangeInvalidation.AffectedTypes(new[] { nameof(PluginConfiguration.EnabledLibraries) });
        Assert.Null(affected);
    }

    [Fact]
    public void AffectedTypes_TargetPathsMapToMisplacedAndUngrouped()
    {
        var affected = ConfigChangeInvalidation.AffectedTypes(new[] { nameof(PluginConfiguration.MoviesTargetPath) });
        Assert.NotNull(affected);
        Assert.Contains(IssueType.Misplaced, affected!);
        Assert.Contains(IssueType.Ungrouped, affected);
    }

    [Fact]
    public void Clone_ProducesIndependentArrays()
    {
        var source = new PluginConfiguration { AllowedAudioLanguages = new[] { "eng" } };
        var clone = ConfigChangeInvalidation.Clone(source);
        source.AllowedAudioLanguages[0] = "spa";
        // The clone must not observe the source's later mutation, otherwise the "before" snapshot
        // held by ConfigChangeRescanner would silently drift and the diff would report zero fields.
        Assert.Equal("eng", clone.AllowedAudioLanguages[0]);
    }

    [Fact]
    public void Clone_ProducesIndependentScalarValues()
    {
        var source = new PluginConfiguration { MaxResolutionHeight = 1080 };
        var clone = ConfigChangeInvalidation.Clone(source);
        source.MaxResolutionHeight = 2160;
        Assert.Equal(1080, clone.MaxResolutionHeight);
    }

    [Fact]
    public void EndToEnd_Issue64Regression()
    {
        // Simulates the exact reporter path:
        //  1) AllowedAudioLanguages = ["eng"], scan runs, jpn flagged for removal.
        //  2) User adds jpn to the allowed list.
        //  3) Config change must invalidate ONLY AudioLanguage rows — not the whole issues table.
        var before = new PluginConfiguration { AllowedAudioLanguages = new[] { "eng" } };
        var after = new PluginConfiguration { AllowedAudioLanguages = new[] { "eng", "jpn" } };

        var changed = ConfigChangeInvalidation.ChangedFields(before, after);
        var affected = ConfigChangeInvalidation.AffectedTypes(changed);

        Assert.NotNull(affected);
        Assert.Single(affected!);
        Assert.Contains(IssueType.AudioLanguage, affected);
    }
}

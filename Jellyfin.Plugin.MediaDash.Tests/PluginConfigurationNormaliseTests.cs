using Jellyfin.Plugin.MediaDash.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

#pragma warning disable CS0618 // DetectOnly tombstone references are the point of this test

public class PluginConfigurationNormaliseTests
{
    [Fact]
    public void NormaliseFixModes_PromotesDetectOnlyToManualApprove()
    {
        var cfg = new PluginConfiguration
        {
            AudioFixMode = FixMode.DetectOnly,
            SubtitleFontFixMode = FixMode.DetectOnly,
            PlayabilityFixMode = FixMode.Automatic
        };

        Assert.True(cfg.NormaliseFixModes());
        Assert.Equal(FixMode.ManualApprove, cfg.AudioFixMode);
        Assert.Equal(FixMode.ManualApprove, cfg.SubtitleFontFixMode);
        Assert.Equal(FixMode.Automatic, cfg.PlayabilityFixMode);
    }

    [Fact]
    public void NormaliseFixModes_IsIdempotent()
    {
        var cfg = new PluginConfiguration { AudioFixMode = FixMode.Off, PlayabilityFixMode = FixMode.ManualApprove };
        Assert.False(cfg.NormaliseFixModes());
        Assert.False(cfg.NormaliseFixModes());
    }

    [Fact]
    public void NormaliseFixModes_ClampsStaleAutomaticToManualApprove()
    {
        var cfg = new PluginConfiguration { StaleFixMode = FixMode.Automatic };
        Assert.True(cfg.NormaliseFixModes());
        Assert.Equal(FixMode.ManualApprove, cfg.StaleFixMode);
    }
}

using Jellyfin.Plugin.MediaDash.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class PluginConfigurationPrivacyTests
{
    [Fact]
    public void NewInstallationDoesNotEnableAnalyticsWithoutConsent()
    {
        var configuration = new PluginConfiguration();

        Assert.False(configuration.AnalyticsEnabled);
        Assert.Equal(string.Empty, configuration.AnalyticsInstallId);
    }

    [Fact]
    public void LegacyEnabledAnalyticsWithoutInstallIdIsDisabled()
    {
        var configuration = new PluginConfiguration
        {
            AnalyticsEnabled = true,
            AnalyticsInstallId = string.Empty
        };

        Assert.True(configuration.NormalizeAnalyticsConsent());
        Assert.False(configuration.AnalyticsEnabled);
        Assert.Equal(string.Empty, configuration.AnalyticsInstallId);
        Assert.Equal(1, configuration.AnalyticsConsentVersion);
    }

    [Fact]
    public void LegacyAutomaticallyMintedInstallIdIsNotTreatedAsConsent()
    {
        var configuration = new PluginConfiguration
        {
            AnalyticsEnabled = true,
            AnalyticsInstallId = "54834b32-926e-4f79-a39a-741d6fcad224"
        };

        Assert.True(configuration.NormalizeAnalyticsConsent());
        Assert.False(configuration.AnalyticsEnabled);
        Assert.Equal(string.Empty, configuration.AnalyticsInstallId);
        Assert.Equal(1, configuration.AnalyticsConsentVersion);
    }

    [Fact]
    public void VersionedExplicitAnalyticsConsentIsPreserved()
    {
        var configuration = new PluginConfiguration
        {
            AnalyticsEnabled = true,
            AnalyticsInstallId = "54834b32-926e-4f79-a39a-741d6fcad224",
            AnalyticsConsentVersion = 1
        };

        Assert.False(configuration.NormalizeAnalyticsConsent());
        Assert.True(configuration.AnalyticsEnabled);
    }
}

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
    }

    [Fact]
    public void ExplicitAnalyticsConsentWithInstallIdIsPreserved()
    {
        var configuration = new PluginConfiguration
        {
            AnalyticsEnabled = true,
            AnalyticsInstallId = "54834b32-926e-4f79-a39a-741d6fcad224"
        };

        Assert.False(configuration.NormalizeAnalyticsConsent());
        Assert.True(configuration.AnalyticsEnabled);
    }
}

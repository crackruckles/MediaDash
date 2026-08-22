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
}

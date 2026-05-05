using Jellyfin.Plugin.MediaDash.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaDash;

/// <summary>
/// Registers MediaDash services into Jellyfin's dependency-injection container.
/// Jellyfin discovers this class automatically at startup.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection,
                                 IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MediaDashService>();
    }
}

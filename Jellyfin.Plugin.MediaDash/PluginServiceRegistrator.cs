using Jellyfin.Plugin.MediaDash.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaDash;

/// <summary>Registers MediaDash services into Jellyfin's DI container.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection,
                                 IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MediaDashService>();
    }
}

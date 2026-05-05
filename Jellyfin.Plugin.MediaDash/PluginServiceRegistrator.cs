using Jellyfin.Plugin.MediaDash.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaDash;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection,
                                 IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MediaDashService>();
        serviceCollection.AddSingleton<FileExplorerService>();
    }
}

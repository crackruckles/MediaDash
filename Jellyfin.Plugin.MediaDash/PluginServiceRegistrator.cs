using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaDash;

/// <summary>
/// Registers the plugin's services with the server's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MediaDashDb>();
        serviceCollection.AddSingleton<FfprobeService>();
        serviceCollection.AddSingleton<IScanner, DuplicateScanner>();
        serviceCollection.AddSingleton<IScanner, PlayabilityScanner>();
        serviceCollection.AddSingleton<IScanner, QualityScanner>();
        serviceCollection.AddSingleton<IScanner, SubtitleLanguageScanner>();
        serviceCollection.AddSingleton<IScanner, AudioLanguageScanner>();
        serviceCollection.AddSingleton<LibraryGuard>();
        serviceCollection.AddSingleton<RecycleBin>();
        serviceCollection.AddSingleton<FfmpegExecutor>();
        serviceCollection.AddSingleton<OutputVerifier>();
        serviceCollection.AddSingleton<IFixer, DuplicateFixer>();
        serviceCollection.AddSingleton<IFixer, TrackFixer>();
        serviceCollection.AddSingleton<IFixer, TranscodeFixer>();
        serviceCollection.AddSingleton<IFixer, PlayabilityFixer>();
    }
}

using Jellyfin.Plugin.MediaDash.Analytics;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        serviceCollection.AddSingleton<BookProbeService>();
        serviceCollection.AddSingleton<ComicProbeService>();
        serviceCollection.AddSingleton<IScanner, DuplicateScanner>();
        serviceCollection.AddSingleton<IScanner, PlayabilityScanner>();
        serviceCollection.AddSingleton<IScanner, QualityScanner>();
        serviceCollection.AddSingleton<IScanner, SubtitleLanguageScanner>();
        serviceCollection.AddSingleton<IScanner, AudioLanguageScanner>();
        serviceCollection.AddSingleton<IScanner, MediaSorterScanner>();
        serviceCollection.AddSingleton<IScanner, MediaGrouperScanner>();
        serviceCollection.AddSingleton<IScanner, ArtworkScanner>();
        serviceCollection.AddSingleton<IScanner, MissingSubtitleScanner>();
        serviceCollection.AddSingleton<IScanner, StaleContentScanner>();
        serviceCollection.AddSingleton<IScanner, SuspiciousFileScanner>();
        serviceCollection.AddSingleton<IScanner, TrickplayOptimizeScanner>();
        serviceCollection.AddSingleton<IScanner, SubtitleFontScanner>();
        serviceCollection.AddSingleton<IScanner, OrphanCleanupScanner>();
        serviceCollection.AddSingleton<IScanner, NfoScanner>();
        serviceCollection.AddSingleton<IScanner, TranscodeLogScanner>();
        serviceCollection.AddSingleton<IScanner, EmbeddedCoverArtScanner>();
        serviceCollection.AddSingleton<LibraryGuard>();
        serviceCollection.AddSingleton<RecycleBin>();
        serviceCollection.AddSingleton<FfmpegExecutor>();
        serviceCollection.AddSingleton<OutputVerifier>();
        serviceCollection.AddSingleton<IFixer, ArtworkFixer>();
        serviceCollection.AddSingleton<IFixer, DuplicateFixer>();
        serviceCollection.AddSingleton<IFixer, TrackFixer>();
        serviceCollection.AddSingleton<IFixer, TranscodeFixer>();
        serviceCollection.AddSingleton<IFixer, PlayabilityFixer>();
        serviceCollection.AddSingleton<IFixer, MediaSorterFixer>();
        serviceCollection.AddSingleton<IFixer, MediaGrouperFixer>();
        serviceCollection.AddSingleton<IFixer, MissingSubtitleFixer>();
        serviceCollection.AddSingleton<IFixer, SuspiciousFileFixer>();
        serviceCollection.AddSingleton<IFixer, TrickplayOptimizeFixer>();
        serviceCollection.AddSingleton<IFixer, SubtitleFontFixer>();
        serviceCollection.AddSingleton<IFixer, OrphanCleanupFixer>();
        serviceCollection.AddSingleton<IFixer, NfoFixer>();
        serviceCollection.AddSingleton<IFixer, EmbeddedCoverArtFixer>();
        serviceCollection.AddSingleton<AnalyticsReporter>();
        serviceCollection.AddSingleton<PostUpgradeCleanup>();
        serviceCollection.AddHostedService<ScheduleMigrator>();
    }
}

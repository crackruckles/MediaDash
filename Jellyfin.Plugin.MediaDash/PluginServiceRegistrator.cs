using System;
using System.IO;
using System.Reflection;
using Jellyfin.Plugin.MediaDash.Analytics;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Jellyfin.Plugin.MediaDash;

/// <summary>
/// Registers the plugin's services with the server's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Hook the plugin's XML doc file into Jellyfin's Swagger generator so /api-docs/swagger
        // renders every MediaDash controller endpoint with its <summary> / <param> / <returns>
        // XML comments. Without this, AI/CI harnesses have to grep the source to learn the API —
        // Jellyfin's own IncludeXmlComments call only covers its own assemblies. Silent no-op if
        // the XML file is missing (Release-mode strip or dev-time incremental build hiccup).
        var pluginAssembly = typeof(PluginServiceRegistrator).Assembly;
        var xmlPath = Path.ChangeExtension(pluginAssembly.Location, ".xml");
        if (File.Exists(xmlPath))
        {
            serviceCollection.Configure<SwaggerGenOptions>(options =>
                options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true));
        }

        // 02-C: DiagnosticService is registered BEFORE MediaDashDb so the service is
        // available to attach when the DB constructs (the DB's ctor calls
        // Api.Diagnostics.Attach(this) which forwards to the service when present).
        serviceCollection.AddSingleton<DiagnosticService>();
        serviceCollection.AddSingleton<MediaDashDb>();
        // 05-C.2: IssueStore owns the `issues` table. Registered as a factory that reuses the
        // single MediaDashDb-owned instance so consumers who narrow their DI graph to just issues
        // (fixers, ScanTask, FixTask, FixExecutor, ConfigChangeRescanner, controller endpoints)
        // share the same connection factory as everything else.
        serviceCollection.AddSingleton<IssueStore>(sp => sp.GetRequiredService<MediaDashDb>().Issues);
        // 05-C.3: HistoryStore owns the `history` table (excluding RecordFixOutcome — the
        // cross-store transaction stays on MediaDashDb). Same DI factory pattern.
        serviceCollection.AddSingleton<HistoryStore>(sp => sp.GetRequiredService<MediaDashDb>().History);
        // 05-C.4: ProbeCacheStore owns probe_cache + file_hashes + format_probe_cache +
        // decode_cache. Consumed by FfprobeService / FileHasher / BookProbeService /
        // ComicProbeService — pure convenience caches, no cross-store atomicity.
        serviceCollection.AddSingleton<ProbeCacheStore>(sp => sp.GetRequiredService<MediaDashDb>().Probes);
        // 05-C.5: DiagnosticStore owns the `diagnostics` table. Attached to DiagnosticService via
        // MediaDashDb's ctor (Api.Diagnostics.Attach) rather than a ctor-inject, but registered
        // here so future consumers can DI it directly.
        serviceCollection.AddSingleton<DiagnosticStore>(sp => sp.GetRequiredService<MediaDashDb>().Diagnostics);
        // 05-C.6: RestoreOperationStore owns the `restore_operations` table (schema v7). Consumed
        // by RestoreService for the owner-tokened claim workflow.
        serviceCollection.AddSingleton<RestoreOperationStore>(sp => sp.GetRequiredService<MediaDashDb>().RestoreOps);
        // Roadmap i-110-protected-media: ProtectionStore owns `protection_rules` (schema v8).
        // ProtectionService caches the ruleset and runs the runtime path/library matching.
        serviceCollection.AddSingleton<ProtectionStore>(sp => sp.GetRequiredService<MediaDashDb>().Protection);
        serviceCollection.AddSingleton<ProtectionService>();
        // 05-C.7: StatusQueryService composes GET /MediaDash/Status. Centralises the drive scan +
        // SMART probe + aggregate history reads + recycle-bin contents + config reads out of the
        // controller so /Status has one owner instead of a 170-line controller inline block.
        serviceCollection.AddSingleton<StatusQueryService>();
        // 02-A: RunState owns every transient run flag (bypass-idle, ignore-activity, current-
        // path, scan filter, low-impact hint). One instance per plugin lifetime; Scan and Fix
        // slots inside are independent so nothing here forces them to serialise.
        serviceCollection.AddSingleton<RunState>();
        // 02-A: IPluginConfigurationProvider hands out per-call snapshots so a save mid-run
        // can't tear the view a scan or fix ran under. Reads Plugin.Instance.Configuration and
        // deep-clones arrays via ConfigChangeInvalidation.Clone.
        serviceCollection.AddSingleton<IPluginConfigurationProvider, PluginConfigurationProvider>();
        serviceCollection.AddSingleton<FfprobeService>();
        serviceCollection.AddSingleton<FileHasher>();
        serviceCollection.AddSingleton<BookProbeService>();
        serviceCollection.AddSingleton<ComicProbeService>();
        // Order per field report D1: cheap classifiers first (results populate Overview immediately),
        // heaviest last (Playability's whole-file decode). Duplicate must come first so its
        // "flagged for deletion" paths let downstream scanners skip doomed files without probing them.
        serviceCollection.AddSingleton<IScanner, DuplicateScanner>();
        serviceCollection.AddSingleton<IScanner, SuspiciousFileScanner>();
        serviceCollection.AddSingleton<IScanner, MediaSorterScanner>();
        serviceCollection.AddSingleton<IScanner, MediaGrouperScanner>();
        serviceCollection.AddSingleton<IScanner, StaleContentScanner>();
        serviceCollection.AddSingleton<IScanner, TranscodeLogScanner>();
        serviceCollection.AddSingleton<IScanner, NfoScanner>();
        serviceCollection.AddSingleton<IScanner, ArtworkScanner>();
        serviceCollection.AddSingleton<IScanner, SubtitleFontScanner>();
        serviceCollection.AddSingleton<IScanner, OrphanCleanupScanner>();
        serviceCollection.AddSingleton<IScanner, TrickplayOptimizeScanner>();
        serviceCollection.AddSingleton<IScanner, AudioLanguageScanner>();
        serviceCollection.AddSingleton<IScanner, SubtitleLanguageScanner>();
        serviceCollection.AddSingleton<IScanner, MissingSubtitleScanner>();
        serviceCollection.AddSingleton<IScanner, QualityScanner>();
        serviceCollection.AddSingleton<IScanner, EmbeddedCoverArtScanner>();
        serviceCollection.AddSingleton<IScanner, PlayabilityScanner>();
        serviceCollection.AddSingleton<IScanner, FormatCompatibilityScanner>();
        // Converter registry: DI-fed IEnumerable<IFormatConverter> → dictionary. Phase 1 registers
        // no IFormatConverter instances; the registry lands empty and every FormatCompatibility
        // scan silently produces zero issues until Phase 2 wires the first converter.
        serviceCollection.AddSingleton<IFormatConverterRegistry, FormatConverterRegistry>();
        serviceCollection.AddSingleton<ILibraryCollectionResolver, LibraryCollectionResolver>();
        serviceCollection.AddSingleton<IToolchainDownloader, CalibreDownloader>();
        serviceCollection.AddSingleton<IFormatConverter, ComicConverter>();
        serviceCollection.AddSingleton<IFormatConverter, EbookConverter>();
        serviceCollection.AddSingleton<IFormatConverter, AudiobookConverter>();
        serviceCollection.AddSingleton<IFormatConverter, ImageConverter>();
        serviceCollection.AddSingleton<IFormatConverter, MusicConverter>();
        serviceCollection.AddSingleton<IFormatConverter, VideoConverter>();
        serviceCollection.AddSingleton(sp => new CalibreToolchain(
            sp.GetRequiredService<IToolchainDownloader>(),
            sp.GetRequiredService<ILogger<CalibreToolchain>>(),
            Path.Combine(MediaDashDb.DataDirectory ?? string.Empty)));
        serviceCollection.AddSingleton<LibraryGuard>();
        serviceCollection.AddSingleton<MediaIdentityStore>(sp => sp.GetRequiredService<MediaDashDb>().Identities);
        serviceCollection.AddSingleton<Metadata.TvdbClient>();
        serviceCollection.AddSingleton<Metadata.MediaIdentityService>();
        serviceCollection.AddSingleton<MediaMovePlanner>();
        serviceCollection.AddSingleton<MediaMoveService>();
        serviceCollection.AddSingleton<MediaMoveUserData>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<MediaMoveUserData>());
        serviceCollection.AddSingleton<MediaMoveStore>(sp => sp.GetRequiredService<MediaDashDb>().Moves);
        serviceCollection.AddSingleton<RecycleBin>();
        // 05-B: single owner of the "restore a recycled file back to a library" workflow.
        // Every route (History/{id}/Restore, RedownloadWarnings/{historyId}/RestoreOptimized,
        // RecycleBin/Items/Restore) funnels through here so the owner-tokened claim table
        // (restore_operations, v7) is the sole serialization point for physical bin-entry access.
        serviceCollection.AddSingleton<RestoreService>();
        serviceCollection.AddSingleton<FfmpegExecutor>();
        serviceCollection.AddSingleton<OutputVerifier>();
        serviceCollection.AddSingleton<FixLibraryRefresher>();
        serviceCollection.AddSingleton<NativeLibraryRefresher>();
        // 03-A: one auditable protocol for verify → stage → promote → dispose → commit.
        // Consumed by TranscodeFixer / TrackFixer / EmbeddedCoverArtFixer / PlayabilityFixer
        // through the migrations in 03-B and 03-C.
        serviceCollection.AddSingleton<VerifiedReplacementService>();
        // 04-B: transactional history + status recorder. FixTask feeds it the plan + execution
        // result; the recorder writes the fan-out (primary + companions + sidecars) and the
        // conditional Queued → Fixed transitions in one SQLite transaction.
        serviceCollection.AddSingleton<FixResultRecorder>();
        // 04-C: per-operation executor. Owns the last-mile consent recheck, fixer dispatch,
        // sharing-violation retry and exception translation. TimeProvider is registered so
        // tests can substitute a fake clock — DI uses TimeProvider.System in production.
        serviceCollection.AddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<FixExecutor>();
        serviceCollection.AddSingleton<IFixer, ArtworkFixer>();
        serviceCollection.AddSingleton<IFixer, DuplicateFixer>();
        serviceCollection.AddSingleton<IFixer, TrackFixer>();
        serviceCollection.AddSingleton<IFixer, TranscodeFixer>();
        serviceCollection.AddSingleton<IFixer, PlayabilityFixer>();
        serviceCollection.AddSingleton<IFixer, MediaSorterFixer>();
        serviceCollection.AddSingleton<IFixer, MediaGrouperFixer>();
        serviceCollection.AddSingleton<IFixer, MissingSubtitleFixer>();
        serviceCollection.AddSingleton<IFixer, SuspiciousFileFixer>();
        serviceCollection.AddSingleton<IFixer, StaleFixer>();
        serviceCollection.AddSingleton<IFixer, TrickplayOptimizeFixer>();
        serviceCollection.AddSingleton<IFixer, SubtitleFontFixer>();
        serviceCollection.AddSingleton<IFixer, OrphanCleanupFixer>();
        serviceCollection.AddSingleton<IFixer, NfoFixer>();
        serviceCollection.AddSingleton<IFixer, EmbeddedCoverArtFixer>();
        serviceCollection.AddSingleton<IFixer, FormatCompatibilityFixer>();
        serviceCollection.AddSingleton<AnalyticsReporter>();
        serviceCollection.AddSingleton<PostUpgradeCleanup>();
        serviceCollection.AddHostedService<ScheduleMigrator>();
        serviceCollection.AddHostedService<ConfigChangeRescanner>();
    }
}

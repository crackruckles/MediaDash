using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class ReportedScannerIntegrationRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-reported-scanners-" + Guid.NewGuid().ToString("N"));
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly RunState? _previousState = Plugin.AttachedRunState;
    private readonly PluginConfiguration _config = new() { DryRun = false, PauseDuringPlayback = false, AllowedSubtitleLanguages = ["eng"] };
    private readonly MediaDashDb _db;
    private readonly ILibraryManager _library;
    private readonly List<VirtualFolderInfo> _folders;

    public ReportedScannerIntegrationRegressionTests()
    {
        Directory.CreateDirectory(_root);
        _db = new MediaDashDb(Path.Combine(_root, "state.db"));
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        _folders = [new VirtualFolderInfo { ItemId = "enabled", Locations = [_root] }];
        _library = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)_library).Call = (method, _) => method.Name switch
        {
            "GetVirtualFolders" => _folders,
            "GetItemList" => new List<BaseItem>(),
            _ => null
        };
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Plugin.AttachedRunState = _previousState;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    private ScanTask TaskFor(IEnumerable<IScanner> scanners, RunState? state = null)
    {
        var manager = DispatchProxy.Create<ITaskManager, LibraryScanCancellationTests.ManagerProxy>();
        return new ScanTask(_library, scanners, _db, null!, manager, state ?? new RunState(), new PluginConfigurationProvider(() => _config), new ProtectionService(_db.Protection, _library), NullLogger<ScanTask>.Instance);
    }

    [Fact]
    public async Task EmptyLibrary_DoesNotMarkContentDefaultsChecked()
    {
        await TaskFor([]).ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Null(_db.GetState("ContentAwareDefaultsChecked"));
        Assert.Equal(FixMode.ManualApprove, _config.AudioFixMode);
        Assert.Equal(FixMode.ManualApprove, _config.SubtitleFixMode);
    }

    [Fact]
    public async Task FilterIsConsumedWhenRunCannotStart()
    {
        var state = new RunState();
        state.ArmNextScanTypeFilter(new HashSet<IssueType> { IssueType.Quality });
        Assert.True(state.TryBegin(RunRole.Scan));
        await TaskFor([], state).ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Null(state.ConsumeNextScanTypeFilter());
    }

    [Fact]
    public async Task ScopedScan_RemovesDeletedFilesPriorFindings()
    {
        _config.EnabledLibraries = ["enabled"];
        var path = Path.Combine(_root, "deleted.mkv");
        _db.ReplaceDetectedIssues(IssueType.AudioLanguage, [new Issue { Type = IssueType.AudioLanguage, Path = path }]);
        await TaskFor([new EmptyScanner()]).ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Empty(_db.GetIssues(IssueType.AudioLanguage));
    }

    [Fact]
    public async Task NullProbe_ProducesNoMissingSubtitleIssue()
    {
        var scanner = new MissingSubtitleScanner(null!, NullLogger<MissingSubtitleScanner>.Instance);
        var evaluate = typeof(MissingSubtitleScanner).GetMethod("EvaluateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<Issue?>)evaluate.Invoke(scanner, [new Movie(), "film.mkv", null, CancellationToken.None])!;
        Assert.Null(result);
    }

    [Fact]
    public void MissingStreamBitrate_UsesContainerThenSizeDuration()
    {
        var probe = new FfprobeData { Format = new FfprobeFormat { BitRate = "1000000", Duration = "10" } };
        var stream = new FfprobeStreamInfo { BitRate = "N/A" };
        Assert.Equal(1000000, DuplicateScanner.ResolveBitrate(probe, stream, 1000));
        probe.Format.BitRate = "N/A";
        Assert.Equal(800, DuplicateScanner.ResolveBitrate(probe, stream, 1000));
    }

    [Fact]
    public void ProtectedPath_IsNotMarkedDoomed()
    {
        var path = Path.Combine(_root, "keep.mkv");
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = _root, ScannersMask = "*", FixersMask = "*" });
        var filtered = new ProtectionService(_db.Protection, _library).FilterEmittable([new Issue { Type = IssueType.Duplicate, Path = path }], IssueType.Duplicate);
        Assert.Empty(ScanTask.ComputeDoomedPaths(filtered, []));
    }

    [Fact]
    public async Task EmptyFolderFix_RefusesLibraryRootAndParentOfNestedLibrary()
    {
        var guard = new LibraryGuard(_library);
        var paths = DispatchProxy.Create<IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (_, _) => _root;
        var bin = new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History);
        var fixer = new OrphanCleanupFixer(guard, bin, NullLogger<OrphanCleanupFixer>.Instance);
        var issue = new Issue { Type = IssueType.OrphanedDebris, Path = _root, DetailsJson = "{\"kind\":\"EmptyFolder\"}" };
        var result = await fixer.FixAsync(issue, null, CancellationToken.None);
        Assert.Equal(FixReason.OutsideLibrary, result.Reason);
        Assert.True(Directory.Exists(_root));
        var parent = Path.Combine(_root, "parent");
        var nested = Path.Combine(parent, "nested");
        Directory.CreateDirectory(nested);
        _folders.Add(new VirtualFolderInfo { Locations = [nested] });
        issue.Path = parent;
        result = await fixer.FixAsync(issue, null, CancellationToken.None);
        Assert.Equal(FixReason.OutsideLibrary, result.Reason);
        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public async Task HashChangedDuringRead_IsNotReturnedOrCached()
    {
        var path = Path.Combine(_root, "mutable.bin");
        File.WriteAllBytes(path, new byte[8 * 1024 * 1024]);
        var before = new FileInfo(path);
        var size = before.Length;
        var mtime = before.LastWriteTimeUtc.Ticks;
        _config.LowSystemImpactMode = true;
        var hasher = new FileHasher(_db.Probes, NullLogger<FileHasher>.Instance, new PluginConfigurationProvider(() => _config));
        var hashing = hasher.HashAsync(path, CancellationToken.None);
        File.SetLastWriteTimeUtc(path, before.LastWriteTimeUtc.AddMinutes(1));
        Assert.Null(await hashing);
        Assert.Null(_db.Probes.GetCachedHash(path, size, mtime));
    }

    [Fact]
    public void EnabledPathCaseFollowsPlatform()
    {
        Assert.Equal(OperatingSystem.IsWindows(), LibraryGuard.IsInEnabledLocations(Path.Combine(_root, "Movies", "film.mkv"), [Path.Combine(_root, "movies") + Path.DirectorySeparatorChar]));
    }

    [Fact]
    public async Task UnindexedSweep_HonoursResolvedFolderIdentity()
    {
        var path = Path.Combine(_root, "poem.txt");
        File.WriteAllText(path, "owned text fixture");
        _config.EnabledLibraries = ["resolved"];
        _folders[0].ItemId = null;
        _folders[0].CollectionType = CollectionTypeOptions.books;
        var converter = DispatchProxy.Create<IFormatConverter, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)converter).Call = (method, _) => method.Name switch
        {
            "get_Key" => "ebook",
            "get_Kind" => MediaKind.Ebook,
            "get_AllowedLibraryTypes" => new HashSet<CollectionType?> { CollectionType.books },
            "get_Availability" => ConverterAvailability.Ready,
            "CanConvert" => true,
            _ => null
        };
        var resolver = DispatchProxy.Create<ILibraryCollectionResolver, OriginalLanguageFlowTests.CallProxy>();
        var scanner = new FormatCompatibilityScanner(new FormatConverterRegistry([converter]), resolver, () => _folders, new PluginConfigurationProvider(() => _config), NullLogger<FormatCompatibilityScanner>.Instance, getFolderId: _ => "resolved");
        var issues = await scanner.ScanAsync([], new Progress<double>(), CancellationToken.None);
        Assert.Equal(path, Assert.Single(issues).Path);
    }

    [Fact]
    public async Task EmptyFolderFix_RefusesProtectedDescendant()
    {
        var folder = Path.Combine(_root, "empty");
        Directory.CreateDirectory(folder);
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = Path.Combine(folder, "protected"), ScannersMask = "*", FixersMask = "*" });
        var paths = DispatchProxy.Create<IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (_, _) => _root;
        var fixer = new OrphanCleanupFixer(new LibraryGuard(_library), new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History), NullLogger<OrphanCleanupFixer>.Instance, new ProtectionService(_db.Protection, _library));
        var result = await fixer.FixAsync(new Issue { Type = IssueType.OrphanedDebris, Path = folder, DetailsJson = "{\"kind\":\"EmptyFolder\"}" }, null, CancellationToken.None);
        Assert.Equal(FixReason.Protected, result.Reason);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void SymlinkLibraryRoot_IsTrustedButDescendantLinkIsRefused()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Path.Combine(_root, "target");
        var rootLink = Path.Combine(_root, "library-link");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(rootLink, target);
        _folders.Clear();
        _folders.Add(new VirtualFolderInfo { Locations = [rootLink] });
        File.WriteAllText(Path.Combine(target, "film.mkv"), "fixture");
        var guard = new LibraryGuard(_library);
        Assert.True(guard.IsInsideLibrary(Path.Combine(rootLink, "film.mkv")));
        Directory.CreateSymbolicLink(Path.Combine(rootLink, "nested-link"), target);
        Assert.False(guard.IsInsideLibrary(Path.Combine(rootLink, "nested-link", "film.mkv")));
        Directory.Delete(Path.Combine(rootLink, "nested-link"));
        Directory.Delete(rootLink);
    }

    [Fact]
    public void FormatCompatibilityCustomOrder_DoesNotOverflow()
    {
        _config.FixerOrder = ["Quality"];
        var rank = typeof(FixTask).GetMethod("FixerRank", BindingFlags.Static | BindingFlags.NonPublic)!;
        var plans = FixPlanBuilder.Build(
            [new Issue { Id = 1, Type = IssueType.FormatCompatibility, Path = "/library/format.wmv" },
             new Issue { Id = 2, Type = IssueType.Quality, Path = "/library/quality.mkv" }],
            type => (int)rank.Invoke(null, [type])!, _ => 0, StringComparer.Ordinal);
        Assert.Equal(new[] { IssueType.Quality, IssueType.FormatCompatibility }, plans.Select(plan => plan.Primary.Type));
    }

    [Fact]
    public async Task TranscodeCompanionDismissedDuringEncode_IsNotResolved()
    {
        var path = Path.Combine(_root, "film.mkv");
        _config.TranscodeFixMode = FixMode.Automatic;
        _config.AudioFixMode = FixMode.Automatic;
        foreach (var type in new[] { IssueType.Quality, IssueType.AudioLanguage })
        {
            _db.ReplaceDetectedIssues(type, [new Issue { Type = type, Path = path }]);
            _db.Issues.QueueDetectedIssues(type);
        }
        var primary = Assert.Single(_db.GetIssues(IssueType.Quality));
        var companion = Assert.Single(_db.GetIssues(IssueType.AudioLanguage));
        var fixer = new CallbackFixer(() => _db.TryTransitionIssueStatus(companion.Id, [IssueStatus.Queued], IssueStatus.Dismissed));
        var executor = new FixExecutor(_db.Issues, [fixer], new PluginConfigurationProvider(() => _config), TimeProvider.System, new ProtectionService(_db.Protection, _library), NullLogger<FixExecutor>.Instance);
        var result = await executor.ExecuteAsync(new PlannedFix(PlannedFixKind.TranscodeWithTracks, primary, [companion]), true, new Progress<double>(), CancellationToken.None);
        Assert.Equal(new[] { primary.Id }, result!.ResolvedIssueIds);
    }

    [Fact]
    public async Task PhotoDecodeRunsWithThoroughCheckingDisabled()
    {
        _config.ThoroughPlayabilityCheck = false;
        var path = Path.Combine(_root, "damaged.jpg");
        File.WriteAllText(path, "owned photo fixture");
        var info = new FileInfo(path);
        _db.Probes.StoreDecode(path, info.Length, info.LastWriteTimeUtc.Ticks, "corrupt JPEG image data");
        var ffprobe = new FfprobeService(null!, _db.Probes, NullLogger<FfprobeService>.Instance);
        var scanner = new PlayabilityScanner(ffprobe, null!, null!, NullLogger<PlayabilityScanner>.Instance);
        var probe = new FfprobeData { Streams = [new FfprobeStreamInfo { CodecType = "video", CodecName = "mjpeg" }] };
        var photo = new Photo { Path = path };
        var evaluate = typeof(PlayabilityScanner).GetMethod("EvaluateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var issue = await (Task<Issue?>)evaluate.Invoke(scanner, [photo, path, probe, CancellationToken.None])!;
        Assert.NotNull(issue);
        Assert.Contains("decode-error", issue.DetailsJson);
    }

    private sealed class CallbackFixer(Action action) : IFixer
    {
        public bool CanFix(IssueType type) => type == IssueType.Quality;
        public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            action();
            return Task.FromResult(new FixResult { Success = true, Message = "fixture encode" });
        }
    }

    private sealed class EmptyScanner : IScanner
    {
        public IssueType Type => IssueType.AudioLanguage;
        public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Issue>>([]);
    }
}

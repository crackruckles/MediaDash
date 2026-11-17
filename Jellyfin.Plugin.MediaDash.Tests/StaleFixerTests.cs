using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using System.Runtime.CompilerServices;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class StaleFixerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mediadash-stale-" + Guid.NewGuid().ToString("N"));
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly PluginConfiguration _config = new() { DryRun = false, StaleFixMode = FixMode.ManualApprove, StaleDisposal = DisposalMethod.RecycleBin };
    private readonly MediaDashDb _db;
    private readonly ILibraryManager _manager;
    private readonly StaleFixer _fixer;
    private readonly CurrentStaleScanner _scanner = new();

    public StaleFixerTests()
    {
        Directory.CreateDirectory(_root);
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        _manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)_manager).Call = (method, _) => method.Name switch
        {
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new() { Locations = [_root] } },
            "GetItemById" => new Movie { Path = _scanner.Path },
            _ => null
        };
        var paths = DispatchProxy.Create<IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (_, _) => _root;
        var bin = new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History);
        var monitor = DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
        _fixer = new StaleFixer(new LibraryGuard(_manager), bin, monitor, NullLogger<StaleFixer>.Instance, _manager, [_scanner]);
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    [Fact]
    public async System.Threading.Tasks.Task CanFix_OnlyMatchesStale()
    {
        Assert.True(_fixer.CanFix(IssueType.Stale));
        Assert.False(_fixer.CanFix(IssueType.Playability));
        await System.Threading.Tasks.Task.CompletedTask;
    }

    [Fact]
    public async System.Threading.Tasks.Task Fix_MissingFile_ReportsSourceMissing()
    {
        var result = await _fixer.FixAsync(new Issue { Type = IssueType.Stale, Path = Path.Combine(_root, "gone.mkv") }, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(FixReason.SourceMissing, result.Reason);
    }

    [Fact]
    public async System.Threading.Tasks.Task Fix_OutsideLibrary_IsRefused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "stale-outside-" + Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllText(outside, "x");
        try
        {
            _scanner.Path = outside;
            var result = await _fixer.FixAsync(new Issue { Type = IssueType.Stale, Path = outside }, null, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(FixReason.OutsideLibrary, result.Reason);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Fix_RecycleBin_MovesFileAndReportsBytes()
    {
        var path = Path.Combine(_root, "unwatched.mkv");
        File.WriteAllBytes(path, new byte[256]);
        _scanner.Path = path;
        var result = await _fixer.FixAsync(new Issue { Type = IssueType.Stale, Path = path }, null, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Equal(256, result.BytesFreed);
        Assert.False(File.Exists(path));
        Assert.NotNull(result.RecyclePath);
    }

    [Fact]
    public async System.Threading.Tasks.Task Fix_PermanentDisposal_DeletesInPlace()
    {
        _config.StaleDisposal = DisposalMethod.Permanent;
        var path = Path.Combine(_root, "nuke.mkv");
        File.WriteAllBytes(path, new byte[128]);
        _scanner.Path = path;
        var result = await _fixer.FixAsync(new Issue { Type = IssueType.Stale, Path = path }, null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.False(File.Exists(path));
        Assert.Null(result.RecyclePath);
    }

    [Fact]
    public async System.Threading.Tasks.Task Fix_DryRun_LeavesFileAlone()
    {
        _config.DryRun = true;
        var path = Path.Combine(_root, "dryrun.mkv");
        File.WriteAllBytes(path, new byte[64]);
        _scanner.Path = path;
        var result = await _fixer.FixAsync(new Issue { Type = IssueType.Stale, Path = path }, null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(result.WasDryRun);
        Assert.True(File.Exists(path));
    }
    [Fact]
    public async Task WatchedAfterApproval_IsLeftInPlace()
    {
        var path = Path.Combine(_root, "recently-watched.mkv");
        File.WriteAllText(path, "owned fixture");
        _scanner.Path = path;
        _scanner.StillStale = false;
        var result = await _fixer.FixAsync(new Issue { Type = IssueType.Stale, Path = path }, null, CancellationToken.None);
        Assert.Equal(FixReason.AlreadyHealthy, result.Reason);
        Assert.True(File.Exists(path));
    }

    private sealed class CurrentStaleScanner : IScanner
    {
        internal string Path { get; set; } = string.Empty;
        internal bool StillStale { get; set; } = true;
        public IssueType Type => IssueType.Stale;
        public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Issue>>(StillStale ? new[] { new Issue { Type = Type, Path = items.Single().Path } } : Array.Empty<Issue>());
    }
}

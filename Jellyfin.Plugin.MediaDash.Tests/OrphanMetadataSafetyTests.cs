using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Analytics;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("MediaDashDb")]
public sealed class OrphanMetadataSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-orphan-metadata-" + Guid.NewGuid().ToString("N"));
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly RunState? _previousState = Plugin.AttachedRunState;
    private readonly FixRunSummary? _previousSummary = Plugin.LastFixRun;
    private readonly Guid _id = Guid.Parse("aabbccdd-1111-2222-3333-444455556666");
    private readonly PluginConfiguration _config = new()
    {
        DryRun = true, OrphanCleanupFixMode = FixMode.ManualApprove, OrphanScanMetadata = true,
        OrphanScanEmptyFolders = false, OrphanScanSubtitles = false, OrphanScanTrickplay = false,
        OrphanCleanupDisposal = DisposalMethod.RecycleBin, AnalyticsEnabled = false, PauseDuringPlayback = false
    };
    private readonly MediaDashDb _db;
    private readonly ILibraryManager _library;
    private readonly IServerApplicationPaths _paths;
    private readonly ILibraryMonitor _monitor;
    private readonly LibraryGuard _guard;
    private readonly ProtectionService _protection;
    private readonly RecycleBin _bin;
    private readonly ServiceProvider _services;
    private readonly ITaskManager _taskManager;
    private readonly TestClock _clock = new();
    private TaskResult? _lastLibraryScan;
    private TaskState _libraryScanState = TaskState.Idle;
    private Action? _duringTaskResultRead;
    private readonly RunState _state = new();
    private bool _ownerReturned;
    private bool _lookupThrows;
    private bool _repositoryOwnerReturned;
    private bool _repositoryThrows;
    private readonly List<MediaBrowser.Controller.Entities.TV.Series> _series = [];
    private Action? _duringLookup;

    public OrphanMetadataSafetyTests()
    {
        Directory.CreateDirectory(Media);
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "poster.png"), "owned recoverable metadata");
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        _paths = Proxy<IServerApplicationPaths>((method, _) => method.Name switch
        {
            "get_DataPath" => Path.Combine(_root, "server", "data"),
            "get_InternalMetadataPath" => Metadata,
            _ => null
        });
        _library = Proxy<ILibraryManager>((method, args) =>
        {
            if (method.Name == "GetVirtualFolders") return new List<VirtualFolderInfo> { new() { ItemId = "owned", Locations = [Media] } };
            if (method.Name == "GetItemList")
            {
                if (args![0] is InternalItemsQuery seriesQuery && seriesQuery.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Series)) return _series.ToArray();
                if (_repositoryThrows) throw new IOException("owned repository query failure");
                return _repositoryOwnerReturned && args![0] is InternalItemsQuery query && query.ItemIds.Contains(_id)
                    ? new BaseItem[] { new Video { Id = _id, Path = Path.Combine(Media, "Movie.mkv") } } : Array.Empty<BaseItem>();
            }
            if (method.Name == "GetItemById")
            {
                _duringLookup?.Invoke();
                if (_lookupThrows) throw new IOException("owned repository lookup failure");
                return _ownerReturned ? new Video { Id = _id, Path = Path.Combine(_root, "disabled", "Movie.mkv") } : null;
            }
            return null;
        });
        _monitor = Proxy<ILibraryMonitor>((_, _) => null);
        var task = Proxy<IScheduledTask>((method, _) => method.Name == "get_Key" ? "RefreshLibrary" : null);
        var worker = Proxy<IScheduledTaskWorker>((method, _) =>
        {
            if (method.Name == "get_LastExecutionResult") { _duringTaskResultRead?.Invoke(); return _lastLibraryScan; }
            return method.Name switch { "get_ScheduledTask" => task, "get_State" => _libraryScanState, _ => null };
        });
        _taskManager = Proxy<ITaskManager>((method, _) => method.Name == "get_ScheduledTasks" ? new[] { worker } : null);
        _services = new ServiceCollection().AddSingleton(_library).AddSingleton(_paths).AddSingleton(_taskManager)
            .AddSingleton<TimeProvider>(_clock).AddSingleton<LibraryGuard>().BuildServiceProvider();
        _guard = _services.GetRequiredService<LibraryGuard>();
        _protection = new ProtectionService(_db.Protection, _library);
        _bin = new RecycleBin(_paths, NullLogger<RecycleBin>.Instance, _db.History);
    }

    private string Media => Path.Combine(_root, "media");
    private string Metadata => Path.Combine(_root, "custom-metadata");
    private string Target => Path.Combine(Metadata, "library", "aa", _id.ToString("N"));
    private Issue Issue(string? path = null) => new() { Type = IssueType.OrphanedDebris, Path = path ?? Target, ItemId = _id,
        DetailsJson = "{\"kind\":\"OrphanMetadata\"}", DetectedAtUtc = DateTime.UtcNow };
    private OrphanCleanupScanner Scanner() => new(_paths, _library, NullLogger<OrphanCleanupScanner>.Instance, _protection, _taskManager);
    private OrphanCleanupFixer Fixer() => new(_guard, _bin, NullLogger<OrphanCleanupFixer>.Instance, _protection);
    private RestoreService Restore() => new(_db, _db.History, _db.RestoreOps, _bin, _guard, _monitor, NullLogger<RestoreService>.Instance, _protection);
    private MediaDashController Controller() => new(_db, _db.Issues, _db.History, null!, _bin, _monitor, _library, null!,
        [], [], null!, _guard, _state, Restore(), null!, _protection, [Fixer()]);

    private async Task RunFix()
    {
        var provider = new PluginConfigurationProvider(() => _config);
        var task = new FixTask(_db.Issues, _bin, null!, null!,
            new AnalyticsReporter(_db.History, _db.Issues, null!, NullLogger<AnalyticsReporter>.Instance), _guard, _state, provider,
            new FixExecutor(_db.Issues, [Fixer()], provider, TimeProvider.System, _protection, NullLogger<FixExecutor>.Instance),
            new FixResultRecorder(_db, NullLogger<FixResultRecorder>.Instance), NullLogger<FixTask>.Instance, _db);
        _state.ArmBypassIdleCheck(RunRole.Fix);
        await task.ExecuteAsync(new Progress<double>(), default);
    }

    private void CompleteLibraryScan(Issue issue)
    {
        _lastLibraryScan = new TaskResult { Key = "RefreshLibrary", Status = TaskCompletionStatus.Completed,
            StartTimeUtc = issue.DetectedAtUtc.AddMinutes(1), EndTimeUtc = issue.DetectedAtUtc.AddMinutes(2) };
        _clock.Now = new DateTimeOffset(_lastLibraryScan.EndTimeUtc.AddMinutes(1));
    }

    private async Task<Issue> ScanAndApprove()
    {
        var found = await Scanner().ScanAsync([], new Progress<double>(), default);
        _db.Issues.ReplaceDetectedIssues(IssueType.OrphanedDebris, found);
        var issue = Assert.Single(_db.Issues.GetIssues());
        Assert.IsType<NoContentResult>(Controller().ApproveIssue(issue.Id));
        return _db.Issues.GetIssue(issue.Id)!;
    }

    [Theory]
    [InlineData("leftover")]
    [InlineData("miraculous")]
    [InlineData("media")]
    [InlineData("new-media")]
    [InlineData("unique-art")]
    [InlineData("unknown-file")]
    [InlineData("changed-art")]
    [InlineData("changed-nfo")]
    [InlineData("nfo-only")]
    [InlineData("missing-art")]
    [InlineData("new-art")]
    [InlineData("large-art")]
    [InlineData("episode-metadata")]
    [InlineData("war-thumbnail")]
    [InlineData("master-display-name")]
    [InlineData("alternate-title")]
    [InlineData("plain-title")]
    [InlineData("unrelated-thumbnail")]
    [InlineData("unrelated-nfo")]
    [InlineData("identity-changed")]
    [InlineData("suffix-thumbnail")]
    [InlineData("war-episode-nfo")]
    [InlineData("different-year")]
    [InlineData("ambiguous")]
    public async Task ReleaseLeftoversAreDetectedWithoutACachedJellyfinSeries(string mode)
    {
        _config.OrphanScanMetadata = false;
        _config.OrphanScanEmptyFolders = true;
        var miraculous = mode == "miraculous";
        var title = miraculous ? "Miraculous - Tales of Ladybug & Cat Noir" : "WAR";
        var master = Directory.CreateDirectory(Path.Combine(Media, miraculous ? title : "WAR (2026)")).FullName;
        File.WriteAllText(Path.Combine(master, "episode.mkv"), "master episode");
        var show = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = title, Path = master, ProductionYear = miraculous ? 2015 : 2026 };
        show.ProviderIds["Tvdb"] = "477406";
        _series.Add(show);
        if (mode == "master-display-name") show.Name = "A different display name";
        if (mode == "ambiguous")
        {
            var other = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = "WAR", Path = Directory.CreateDirectory(Path.Combine(Media, "War")).FullName, ProductionYear = 2026 };
            other.ProviderIds["Tvdb"] = "12345";
            _series.Add(other);
        }

        var release = Directory.CreateDirectory(Path.Combine(Media, miraculous ? "Miraculous Tales of Ladybug and Cat Noir S06E18 Heartfixer 1080p DSNP WEB-DL"
            : "www.UIndex.org - War " + (mode == "different-year" ? "1999" : "2026") + " S01E07 1080p WEB-DL")).FullName;
        if (mode == "alternate-title")
        {
            Directory.Delete(release);
            release = Directory.CreateDirectory(Path.Combine(Media, "Kusuriya no Hitorigoto - 53 (WEB 1080p) [Anarchy]")).FullName;
        }
        if (mode == "plain-title")
        {
            Directory.Delete(release);
            release = Directory.CreateDirectory(Path.Combine(Media, "War")).FullName;
        }
        File.WriteAllText(Path.Combine(release, "tvshow.nfo"), "leftover metadata");
        if (mode == "alternate-title") File.WriteAllText(Path.Combine(release, "tvshow.nfo"), "<tvshow><uniqueid type=\"tvdb\">477406</uniqueid></tvshow>");
        foreach (var image in new[] { "folder", "backdrop", "landscape", "logo" }) File.WriteAllText(Path.Combine(release, image + ".jpg"), "leftover art");
        foreach (var file in Directory.EnumerateFiles(release)) File.Copy(file, Path.Combine(master, Path.GetFileName(file)));
        if (mode == "nfo-only") foreach (var file in Directory.EnumerateFiles(release, "*.jpg")) File.Delete(file);
        if (mode == "media") File.WriteAllText(Path.Combine(release, "episode.mkv"), "unmoved episode");
        if (mode == "unique-art") File.WriteAllText(Path.Combine(release, "family.jpg"), "unique picture");
        if (mode == "unknown-file") File.WriteAllText(Path.Combine(release, "notes.json"), "unique notes");
        if (mode == "unrelated-thumbnail") File.WriteAllText(Path.Combine(release, "family-thumb.jpg"), "unique picture");
        if (mode == "suffix-thumbnail") File.WriteAllText(Path.Combine(release, "DL-thumb.jpg"), "unique picture");
        if (mode == "war-episode-nfo") File.WriteAllText(Path.Combine(release, "War 2026 S01E07 1080p WEB-DL.nfo"), "episode metadata");
        if (mode == "unrelated-nfo") File.WriteAllText(Path.Combine(release, "unrelated.nfo"), "unique metadata");
        if (mode is "episode-metadata" or "alternate-title")
        {
            File.WriteAllText(Path.Combine(release, Path.GetFileName(release) + "-thumb.jpg"), "episode thumbnail");
            File.WriteAllText(Path.Combine(release, Path.GetFileName(release) + ".nfo"), "episode metadata");
            File.WriteAllText(Path.Combine(release, "season01-poster.jpg"), "season poster");
        }
        if (mode == "war-thumbnail")
        {
            foreach (var file in Directory.EnumerateFiles(release)) File.Delete(file);
            File.WriteAllText(Path.Combine(release, "War 2026 S01E07 1080p WEB-DL-thumb.jpg"), "episode thumbnail");
        }
        if (mode == "changed-art") File.WriteAllText(Path.Combine(release, "folder.jpg"), "different art");
        if (mode == "changed-nfo") File.WriteAllText(Path.Combine(release, "tvshow.nfo"), "different metadata");
        if (mode == "missing-art") File.Delete(Path.Combine(master, "logo.jpg"));
        if (mode == "large-art")
        {
            using (var file = File.OpenWrite(Path.Combine(release, "folder.jpg"))) file.SetLength(33L * 1024 * 1024);
            using (var file = File.OpenWrite(Path.Combine(master, "folder.jpg"))) file.SetLength(33L * 1024 * 1024);
        }

        var findings = await Scanner().ScanAsync([], new Progress<double>(), default);
        if (mode is "media" or "different-year" or "ambiguous")
        {
            Assert.DoesNotContain(findings, issue => issue.Path == release);
            return;
        }

        var issue = Assert.Single(findings, issue => issue.Path == release);
        Assert.Equal(OrphanCleanupScanner.KindGroupedLeftover, OrphanCleanupFixer.ReadKind(issue.DetailsJson));
        var preview = await Fixer().FixAsync(issue, null, default);
        if (mode is not ("unique-art" or "unknown-file" or "unrelated-thumbnail" or "unrelated-nfo" or "suffix-thumbnail")) Assert.True(preview.WasDryRun);
        Assert.True(Directory.Exists(release));
        _config.DryRun = false;
        _config.OrphanCleanupDisposal = DisposalMethod.Permanent;
        if (mode == "new-media") File.WriteAllText(Path.Combine(release, "new-episode.mkv"), "new media");
        if (mode == "new-art") File.WriteAllText(Path.Combine(release, "logo.jpg"), "new unique art");
        if (mode == "identity-changed") File.WriteAllText(Path.Combine(release, "tvshow.nfo"), "<tvshow><id>123</id></tvshow>");
        var result = await Fixer().FixAsync(issue, null, default);
        var preserve = mode is "new-media" or "unique-art" or "unknown-file" or "unrelated-thumbnail" or "unrelated-nfo" or "identity-changed" or "suffix-thumbnail";
        if (mode == "identity-changed") Assert.Equal(FixReason.SourceChanged, result.Reason);
        else if (preserve) Assert.Equal(FixReason.NothingToRemove, result.Reason);
        else Assert.True(result.Success, result.Message);
        Assert.Equal(preserve, Directory.Exists(release));
        Assert.True(File.Exists(Path.Combine(master, "episode.mkv")));
        Assert.Equal(mode == "alternate-title" ? "<tvshow><uniqueid type=\"tvdb\">477406</uniqueid></tvshow>" : "leftover metadata",
            File.ReadAllText(Path.Combine(master, "tvshow.nfo")));
        if (!preserve)
        {
            Assert.NotNull(result.RecyclePath);
            _bin.Restore(result.RecyclePath!, release);
            Assert.True(Directory.Exists(release));
            if (mode == "changed-art") Assert.Equal("different art", File.ReadAllText(Path.Combine(release, "folder.jpg")));
            if (mode == "changed-nfo") Assert.Equal("different metadata", File.ReadAllText(Path.Combine(release, "tvshow.nfo")));
        }
    }

    [Theory]
    [InlineData(DisposalMethod.Permanent)]
    [InlineData(DisposalMethod.RecycleBin)]
    public async Task ConfirmationKeepsApprovalPendingThenAllowsRealCleanup(DisposalMethod disposal)
    {
        _config.OrphanCleanupDisposal = disposal;
        var issue = await ScanAndApprove();
        await RunFix();
        Assert.True(Assert.Single(_db.History.GetHistory()).WasDryRun);
        _config.DryRun = false;
        await RunFix();
        var pending = Assert.Single(_db.History.GetHistory(), h => !h.WasDryRun);
        Assert.False(pending.Success);
        Assert.Equal(0, pending.BytesFreed);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
        CompleteLibraryScan(issue);
        await RunFix();
        var applied = _db.History.GetHistory().First();
        Assert.True(applied.Success);
        Assert.Equal(IssueStatus.Fixed, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.False(Directory.Exists(Target));
        if (disposal == DisposalMethod.RecycleBin)
        {
            _lastLibraryScan = null;
            Assert.True(Restore().RestoreFromHistory(applied.Id, false).Success);
            Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("old")]
    [InlineData("overlap")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("aborted")]
    [InlineData("future")]
    [InlineData("inverted")]
    [InlineData("wrong-task")]
    public async Task ConfirmationRejectsAnUnsuccessfulOrUnrelatedHostScan(string condition)
    {
        var issue = await ScanAndApprove();
        CompleteLibraryScan(issue);
        if (condition == "none") _lastLibraryScan = null;
        if (condition == "old") { _lastLibraryScan!.StartTimeUtc = issue.DetectedAtUtc.AddMinutes(-2); _lastLibraryScan.EndTimeUtc = issue.DetectedAtUtc.AddMinutes(-1); }
        if (condition == "overlap") _lastLibraryScan!.StartTimeUtc = issue.DetectedAtUtc.AddMinutes(-1);
        if (condition == "failed") _lastLibraryScan!.Status = TaskCompletionStatus.Failed;
        if (condition == "cancelled") _lastLibraryScan!.Status = TaskCompletionStatus.Cancelled;
        if (condition == "aborted") _lastLibraryScan!.Status = TaskCompletionStatus.Aborted;
        if (condition == "future") _lastLibraryScan!.EndTimeUtc = _clock.Now.UtcDateTime.AddMinutes(1);
        if (condition == "inverted") _lastLibraryScan!.EndTimeUtc = _lastLibraryScan.StartTimeUtc.AddMinutes(-1);
        if (condition == "wrong-task") _lastLibraryScan!.Key = "OtherTask";
        _config.DryRun = false;
        Assert.False((await Fixer().FixAsync(issue, null, default)).Success);
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationCannotAuthorizeAChangedFolderOrImage(bool replaceFolder)
    {
        var issue = await ScanAndApprove();
        CompleteLibraryScan(issue);
        if (replaceFolder)
        {
            Directory.Move(Target, Target + ".old");
            Directory.CreateDirectory(Target);
        }
        var poster = Path.Combine(Target, "poster.png");
        var before = File.Exists(poster) ? File.GetLastWriteTimeUtc(poster) : DateTime.UtcNow;
        File.WriteAllText(poster, "other recoverable metadata");
        File.SetLastWriteTimeUtc(poster, before);
        _config.DryRun = false;
        Assert.False((await Fixer().FixAsync(issue, null, default)).Success);
        Assert.Equal("other recoverable metadata", File.ReadAllText(poster));
    }

    [Fact]
    public async Task ConfirmationPreservesAnOwnerThatReturnsDuringTheHostScan()
    {
        var issue = await ScanAndApprove();
        CompleteLibraryScan(issue);
        _ownerReturned = true;
        _config.DryRun = false;
        await RunFix();
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.Empty(_db.History.GetHistory());
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
    }

    [Fact]
    public async Task ConfirmationRequiresRescanForLegacyQueuedMetadata()
    {
        _db.Issues.ReplaceDetectedIssues(IssueType.OrphanedDebris, [Issue()]);
        var issue = Assert.Single(_db.Issues.GetIssues());
        Assert.IsType<NoContentResult>(Controller().ApproveIssue(issue.Id));
        CompleteLibraryScan(issue);
        _config.DryRun = false;
        await RunFix();
        Assert.False(Assert.Single(_db.History.GetHistory()).Success);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.True(Directory.Exists(Target));
        Assert.IsType<NoContentResult>(Controller().RevertIssue(issue.Id));
        var refreshed = await ScanAndApprove();
        CompleteLibraryScan(refreshed);
        await RunFix();
        Assert.True(_db.History.GetHistory().First().Success);
        Assert.False(Directory.Exists(Target));
    }

    [Theory]
    [InlineData("owner", 1)]
    [InlineData("refresh", 1)]
    [InlineData("mode", 1)]
    [InlineData("metadata", 1)]
    [InlineData("protection", 1)]
    [InlineData("dryrun-replaced", 1)]
    [InlineData("owner", 2)]
    [InlineData("refresh", 2)]
    [InlineData("mode", 2)]
    [InlineData("metadata", 2)]
    [InlineData("protection", 2)]
    [InlineData("dryrun-replaced", 2)]
    public async Task ConfirmationRechecksLiveStateAfterReadingHostEvidence(string change, int changeOnRead)
    {
        var issue = await ScanAndApprove();
        CompleteLibraryScan(issue);
        _config.DryRun = false;
        var reads = 0;
        _duringTaskResultRead = () =>
        {
            if (++reads != changeOnRead) return;
            _duringTaskResultRead = null;
            if (change == "owner") _ownerReturned = true;
            if (change == "refresh") _libraryScanState = TaskState.Running;
            if (change == "mode") _config.OrphanCleanupFixMode = FixMode.Off;
            if (change == "metadata") _config.OrphanScanMetadata = false;
            if (change == "protection") { _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = Target, FixersMask = "*", CreatedAtUtc = DateTime.UtcNow }); _protection.Invalidate(); }
            if (change == "dryrun-replaced") typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Plugin.Instance, new PluginConfiguration { DryRun = true, OrphanScanMetadata = true, OrphanCleanupFixMode = FixMode.ManualApprove });
        };
        var result = await Fixer().FixAsync(issue, null, default);
        Assert.False(result.Outcome == FixOutcome.Applied);
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
    }

    [Fact]
    public async Task ConfirmationRechecksEveryImageAfterFinalAuthorization()
    {
        var backdrop = Path.Combine(Target, "backdrop.png");
        File.WriteAllText(backdrop, "initial backdrop");
        var issue = await ScanAndApprove();
        CompleteLibraryScan(issue);
        var reads = 0;
        _duringTaskResultRead = () =>
        {
            if (++reads == 2)
            {
                File.WriteAllText(backdrop, "changed backdrop");
                File.SetLastWriteTimeUtc(backdrop, DateTime.UtcNow.AddMinutes(1));
            }
        };
        _config.DryRun = false;
        Assert.False((await Fixer().FixAsync(issue, null, default)).Success);
        Assert.Equal("changed backdrop", File.ReadAllText(backdrop));
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
    }

    [Fact]
    public async Task ConfirmationCannotBeBackdatedByChangingTheIssueTimestamp()
    {
        var issue = await ScanAndApprove();
        _lastLibraryScan = new TaskResult { Key = "RefreshLibrary", Status = TaskCompletionStatus.Completed,
            StartTimeUtc = issue.DetectedAtUtc.AddHours(-2), EndTimeUtc = issue.DetectedAtUtc.AddHours(-1) };
        issue.DetectedAtUtc = issue.DetectedAtUtc.AddDays(-1);
        _config.DryRun = false;
        Assert.False((await Fixer().FixAsync(issue, null, default)).Success);
        Assert.True(Directory.Exists(Target));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("{}")]
    [InlineData("{\"version\":\"one\"}")]
    [InlineData("{\"version\":2,\"signature\":\"bad\"}")]
    public async Task ConfirmationCannotAuthorizeMalformedObservation(string observation)
    {
        var issue = await ScanAndApprove();
        CompleteLibraryScan(issue);
        var details = JsonNode.Parse(issue.DetailsJson)!.AsObject();
        details["metadataObservation"] = JsonNode.Parse(observation);
        issue.DetailsJson = details.ToJsonString();
        _config.DryRun = false;
        Assert.False((await Fixer().FixAsync(issue, null, default)).Success);
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
    }

    [Fact]
    public async Task ConfirmationRequiresHostTaskEvidenceWhenTheTaskManagerIsUnavailable()
    {
        var issue = await ScanAndApprove();
        CompleteLibraryScan(issue);
        var fixer = new OrphanCleanupFixer(new LibraryGuard(_library, _paths, timeProvider: _clock), _bin, NullLogger<OrphanCleanupFixer>.Instance, _protection);
        _config.DryRun = false;
        Assert.False((await fixer.FixAsync(issue, null, default)).Success);
        Assert.True(Directory.Exists(Target));
    }

    [Fact]
    public async Task ConfirmationSurvivesReloadAndAnotherMediaDashScan()
    {
        var issue = await ScanAndApprove();
        CompleteLibraryScan(issue);
        var reopened = new MediaDashDb(Path.Combine(_root, "test.db"));
        reopened.Issues.ReplaceDetectedIssues(IssueType.OrphanedDebris, await Scanner().ScanAsync([], new Progress<double>(), default));
        var queued = Assert.Single(reopened.Issues.GetIssues());
        Assert.Equal(issue.Id, queued.Id);
        Assert.Equal(IssueStatus.Queued, queued.Status);
        var guard = new LibraryGuard(_library, _paths, _taskManager, _clock);
        var fixer = new OrphanCleanupFixer(guard, _bin, NullLogger<OrphanCleanupFixer>.Instance, _protection);
        _config.DryRun = false;
        var result = await fixer.FixAsync(queued, null, default);
        Assert.True(result.Success, result.Message);
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(result.RecyclePath!, "poster.png")));
        Assert.False(Directory.Exists(Target));
    }

    [Theory]
    [InlineData(false, "single")]
    [InlineData(true, "bulk")]
    [InlineData(true, "all")]
    public async Task CustomMetadataScansApprovesPreviewsRecyclesAndRestores(bool selected, string approval)
    {
        _config.EnabledLibraries = selected ? ["owned"] : [];
        var found = await Scanner().ScanAsync([], new Progress<double>(), default);
        Assert.Equal(Target, Assert.Single(found).Path);
        _db.Issues.ReplaceDetectedIssues(IssueType.OrphanedDebris, found);
        var issue = Assert.Single(_db.Issues.GetIssues());
        var controller = Controller();
        if (approval == "single") Assert.IsType<NoContentResult>(controller.ApproveIssue(issue.Id));
        else if (approval == "bulk") Assert.Null(controller.BulkUpdateIssues(new BulkIssueRequest { Action = "Approve", Ids = [issue.Id] }).Result);
        else Assert.Equal(1, controller.ApproveAll().Value);
        await RunFix();
        Assert.True(Assert.Single(_db.History.GetHistory()).WasDryRun);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
        CompleteLibraryScan(issue);
        _config.DryRun = false;
        await RunFix();
        var entry = Assert.Single(_db.History.GetHistory(), h => !h.WasDryRun);
        Assert.True(entry.Success);
        Assert.False(Directory.Exists(Target));
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(entry.RecyclePath!, "poster.png")));
        Assert.Empty(await Scanner().ScanAsync([], new Progress<double>(), default));
        Assert.True(Restore().RestoreFromHistory(entry.Id, false).Success);
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("shard")]
    [InlineData("mismatch")]
    [InlineData("descendant")]
    [InlineData("guessed-root")]
    public async Task KindTagCannotAuthorizeOtherMetadataPaths(string kind)
    {
        var path = kind switch
        {
            "root" => Path.Combine(Metadata, "library"),
            "shard" => Path.Combine(Metadata, "library", "aa"),
            "mismatch" => Path.Combine(Metadata, "library", "aa", Guid.NewGuid().ToString("N")),
            "descendant" => Path.Combine(Target, "nested"),
            _ => Path.Combine(_root, "server", "metadata", "library", "aa", _id.ToString("N"))
        };
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "keep.txt"), "must survive");
        _config.DryRun = false;
        _config.OrphanCleanupDisposal = DisposalMethod.Permanent;
        var result = await Fixer().FixAsync(Issue(path), null, default);
        Assert.False(result.Success);
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(path, "keep.txt")));
    }

    [Fact]
    public async Task MetadataCleanupWaitsForLibraryRefreshAndWorksWhenItFinishes()
    {
        var issue = Assert.Single(await Scanner().ScanAsync([], new Progress<double>(), default));
        _libraryScanState = TaskState.Running;
        _config.DryRun = false;
        _config.OrphanCleanupDisposal = DisposalMethod.Permanent;
        Assert.Empty(await Scanner().ScanAsync([], new Progress<double>(), default));
        Assert.False((await Fixer().FixAsync(issue, null, default)).Success);
        Assert.True(Directory.Exists(Target));
        _libraryScanState = TaskState.Cancelling;
        Assert.False((await Fixer().FixAsync(issue, null, default)).Success);
        _libraryScanState = TaskState.Idle;
        CompleteLibraryScan(issue);
        Assert.Single(await Scanner().ScanAsync([], new Progress<double>(), default));
        Assert.True((await Fixer().FixAsync(issue, null, default)).Success);
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public async Task TemporarilyMissingOwnerLookupCannotOverrideRepositoryOwner()
    {
        _repositoryOwnerReturned = true;
        _config.DryRun = false;
        _config.OrphanCleanupDisposal = DisposalMethod.Permanent;
        Assert.Empty(await Scanner().ScanAsync([], new Progress<double>(), default));
        Assert.False((await Fixer().FixAsync(Issue(), null, default)).Success);
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
    }

    [Fact]
    public async Task OwnerReturningAfterOneMissingLookupPreventsDisposal()
    {
        _config.DryRun = false;
        _config.OrphanCleanupDisposal = DisposalMethod.Permanent;
        var lookups = 0;
        _duringLookup = () => { if (++lookups == 2) _ownerReturned = true; };
        Assert.False((await Fixer().FixAsync(Issue(), null, default)).Success);
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
    }

    [Fact]
    public async Task MissingOwnerWithFailedRepositoryQueryPreservesMetadata()
    {
        _repositoryThrows = true;
        _config.DryRun = false;
        Assert.Empty(await Scanner().ScanAsync([], new Progress<double>(), default));
        Assert.False((await Fixer().FixAsync(Issue(), null, default)).Success);
        Assert.True(Directory.Exists(Target));
    }

    [Theory]
    [InlineData("notes.txt", DisposalMethod.Permanent)]
    [InlineData("Movie.mkv", DisposalMethod.Permanent)]
    [InlineData("family.png", DisposalMethod.Permanent)]
    [InlineData("backup/poster.png", DisposalMethod.Permanent)]
    [InlineData("notes.txt", DisposalMethod.RecycleBin)]
    public async Task UnrelatedMetadataContentsPreventWholeDirectoryDisposal(string relative, DisposalMethod disposal)
    {
        var unrelated = Path.Combine(Target, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllText(unrelated, "unrelated content must survive");
        _config.DryRun = false;
        _config.OrphanCleanupDisposal = disposal;
        Assert.Empty(await Scanner().ScanAsync([], new Progress<double>(), default));
        Assert.False((await Fixer().FixAsync(Issue(), null, default)).Success);
        Assert.Equal("unrelated content must survive", File.ReadAllText(unrelated));
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
    }

    [Fact]
    public async Task UnrelatedContentAppearingDuringOwnerLookupPreservesMetadata()
    {
        var issue = await ScanAndApprove();
        CompleteLibraryScan(issue);
        _config.DryRun = false;
        _config.OrphanCleanupDisposal = DisposalMethod.Permanent;
        _duringLookup = () => File.WriteAllText(Path.Combine(Target, "notes.txt"), "late unrelated content");
        Assert.False((await Fixer().FixAsync(issue, null, default)).Success);
        Assert.Equal("late unrelated content", File.ReadAllText(Path.Combine(Target, "notes.txt")));
    }

    [Theory]
    [InlineData(DisposalMethod.Permanent)]
    [InlineData(DisposalMethod.RecycleBin)]
    public async Task RecognizedManagedImagesStillScanApproveAndCleanUp(DisposalMethod disposal)
    {
        foreach (var name in new[] { "folder.jpg", "backdrop.jpg", "backdrop12.webp", "clearart.png", "back.jpg", "landscape.png", "disc.png", "cdart.png", "logo.svg", "banner.jpg", "box.png", "menu.jpg" })
            File.WriteAllText(Path.Combine(Target, name), "generated cache content");
        _config.OrphanCleanupDisposal = disposal;
        var found = await Scanner().ScanAsync([], new Progress<double>(), default);
        _db.Issues.ReplaceDetectedIssues(IssueType.OrphanedDebris, found);
        var issue = Assert.Single(_db.Issues.GetIssues());
        Assert.IsType<NoContentResult>(Controller().ApproveIssue(issue.Id));
        CompleteLibraryScan(issue);
        _config.DryRun = false;
        await RunFix();
        var history = Assert.Single(_db.History.GetHistory());
        Assert.True(history.Success);
        Assert.Equal(IssueStatus.Fixed, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.False(Directory.Exists(Target));
        if (disposal == DisposalMethod.RecycleBin)
        {
            Assert.True(Restore().RestoreFromHistory(history.Id, false).Success);
            Assert.Equal("generated cache content", File.ReadAllText(Path.Combine(Target, "backdrop12.webp")));
        }
    }

    [Fact]
    public async Task RepositoryFailureCannotAuthorizeScanOrDeletion()
    {
        _lookupThrows = true;
        _config.DryRun = false;
        _config.OrphanCleanupDisposal = DisposalMethod.Permanent;
        Assert.Empty(await Scanner().ScanAsync([], new Progress<double>(), default));
        var result = await Fixer().FixAsync(Issue(), null, default);
        Assert.False(result.Success);
        Assert.True(Directory.Exists(Target));
    }

    [Fact]
    public async Task ReappearedDisabledOwnerIsPreservedAndApprovalStaysQueued()
    {
        _db.Issues.ReplaceDetectedIssues(IssueType.OrphanedDebris, [Issue()]);
        var issue = Assert.Single(_db.Issues.GetIssues());
        Assert.IsType<NoContentResult>(Controller().ApproveIssue(issue.Id));
        _ownerReturned = true;
        _config.DryRun = false;
        await RunFix();
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.Empty(_db.History.GetHistory());
        Assert.True(Directory.Exists(Target));
    }

    [Fact]
    public async Task ProtectedDescendantBlocksApprovalAndDirectDeletion()
    {
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = Path.Combine(Target, "poster.png"), FixersMask = "*", CreatedAtUtc = DateTime.UtcNow });
        _db.Issues.ReplaceDetectedIssues(IssueType.OrphanedDebris, [Issue()]);
        var issue = Assert.Single(_db.Issues.GetIssues());
        Assert.IsType<ConflictObjectResult>(Controller().ApproveIssue(issue.Id));
        _config.DryRun = false;
        Assert.False((await Fixer().FixAsync(Issue(), null, default)).Success);
        Assert.True(File.Exists(Path.Combine(Target, "poster.png")));
    }

    [Fact]
    public async Task ReplacementDuringOwnerLookupCannotBeDeleted()
    {
        var issue = await ScanAndApprove();
        CompleteLibraryScan(issue);
        _config.DryRun = false;
        _duringLookup = () =>
        {
            _duringLookup = null;
            Directory.Move(Target, Target + ".old");
            Directory.CreateDirectory(Target);
            File.WriteAllText(Path.Combine(Target, "poster.png"), "new provider content");
        };
        Assert.False((await Fixer().FixAsync(issue, null, default)).Success);
        Assert.Equal("new provider content", File.ReadAllText(Path.Combine(Target, "poster.png")));
    }

    [Fact]
    public async Task CancelledMetadataFixPreservesDirectory()
    {
        _config.DryRun = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fixer().FixAsync(Issue(), null, cancellation.Token));
        Assert.True(Directory.Exists(Target));
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("mode")]
    [InlineData("fixer")]
    [InlineData("libraries")]
    [InlineData("dryrun-replaced")]
    public async Task SettingsDisabledDuringOwnerLookupPreventDeletion(string setting)
    {
        var issue = await ScanAndApprove();
        CompleteLibraryScan(issue);
        _config.DryRun = false;
        _duringLookup = () =>
        {
            _duringLookup = null;
            if (setting == "metadata") _config.OrphanScanMetadata = false;
            if (setting == "mode") _config.OrphanCleanupFixMode = FixMode.Off;
            if (setting == "fixer") _config.FixerDisabled = ["OrphanedDebris"];
            if (setting == "libraries") _config.EnabledLibraries = ["missing-library"];
            if (setting == "dryrun-replaced")
            {
                typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(Plugin.Instance, new PluginConfiguration { DryRun = true, OrphanCleanupFixMode = FixMode.ManualApprove });
            }
        };
        var result = await Fixer().FixAsync(issue, null, default);
        if (setting == "dryrun-replaced") Assert.True(result.WasDryRun);
        else Assert.False(result.Success);
        Assert.True(Directory.Exists(Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedMetadataTreesAreNeverScannedOrDisposed(bool ancestor)
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside content");
        var link = Path.Combine(Target, "link");
        if (ancestor)
        {
            link = Path.GetDirectoryName(Target)!;
            Directory.Move(link, Path.Combine(outside, "original-shard"));
            Directory.CreateDirectory(Path.Combine(outside, _id.ToString("N")));
        }
        try
        {
            CreateLink(link, outside);
            Assert.Empty(await Scanner().ScanAsync([], new Progress<double>(), default));
            _config.DryRun = false;
            Assert.False((await Fixer().FixAsync(Issue(), null, default)).Success);
            Assert.Equal("outside content", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    [Fact]
    public async Task LinkPlantedDuringOwnerLookupPreventsDisposal()
    {
        var outside = Path.Combine(_root, "outside");
        var link = Path.Combine(Target, "late-link");
        Directory.CreateDirectory(outside);
        _config.DryRun = false;
        _duringLookup = () => { _duringLookup = null; CreateLink(link, outside); };
        try
        {
            Assert.False((await Fixer().FixAsync(Issue(), null, default)).Success);
            Assert.True(Directory.Exists(Target));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    [Fact]
    public void RestoreCannotFollowShardLinkPlantedDuringOwnerLookup()
    {
        var history = RecycleMetadata();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var shard = Path.GetDirectoryName(Target)!;
        _duringLookup = () =>
        {
            _duringLookup = null;
            Directory.Move(shard, shard + ".old");
            CreateLink(shard, outside);
        };
        try
        {
            Assert.False(Restore().RestoreFromHistory(history.Id, false).Success);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
            Assert.True(Directory.Exists(history.RecyclePath));
        }
        finally
        {
            if (Directory.Exists(shard)) Directory.Delete(shard);
        }
    }

    [Fact]
    public void RecoveryBesideUnrelatedReplacementStillWorks()
    {
        var history = RecycleMetadata();
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "notes.txt"), "new unrelated content");
        Assert.True(Restore().RestoreFromHistory(history.Id, false).Success);
        Assert.Equal("new unrelated content", File.ReadAllText(Path.Combine(Target, "notes.txt")));
        var recovered = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(Target)!), p => p != Target);
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(recovered, "poster.png")));
    }

    [Fact]
    public void ForceRestorePreservesProtectedReplacement()
    {
        var history = RecycleMetadata();
        Directory.CreateDirectory(Target);
        var protectedPath = Path.Combine(Target, "provider.png");
        File.WriteAllText(protectedPath, "protected replacement");
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = protectedPath, FixersMask = "*", CreatedAtUtc = DateTime.UtcNow });
        Assert.False(Restore().RestoreFromHistory(history.Id, true).Success);
        Assert.Equal("protected replacement", File.ReadAllText(protectedPath));
        Assert.True(Directory.Exists(history.RecyclePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreRequiresOwnedRecoveryWithMatchingOrigin(bool inBin)
    {
        var valid = RecycleMetadata();
        var unrelated = Path.Combine(_root, "unrelated");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "unrelated content");
        var source = inBin ? _bin.MoveToBin(unrelated) : unrelated;
        _db.History.AddHistory(new HistoryEntry { IssueId = valid.IssueId, Type = valid.Type, Path = valid.Path,
            RecyclePath = source, FixedAtUtc = DateTime.UtcNow, Success = true });
        var forged = _db.History.GetHistory().OrderByDescending(h => h.Id).First();
        Assert.False(Restore().RestoreFromHistory(forged.Id, false).Success);
        Assert.Equal("unrelated content", File.ReadAllText(Path.Combine(source, "keep.txt")));
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public void RecoverySourceReplacedDuringOwnerLookupIsPreserved()
    {
        var history = RecycleMetadata();
        _duringLookup = () =>
        {
            _duringLookup = null;
            Directory.Move(history.RecyclePath!, history.RecyclePath + ".old");
            Directory.CreateDirectory(history.RecyclePath!);
            File.WriteAllText(Path.Combine(history.RecyclePath!, "poster.png"), "replacement in recovery storage");
        };
        Assert.False(Restore().RestoreFromHistory(history.Id, false).Success);
        Assert.Equal("replacement in recovery storage", File.ReadAllText(Path.Combine(history.RecyclePath!, "poster.png")));
        Assert.False(Directory.Exists(Target));
    }

    private HistoryEntry RecycleMetadata()
    {
        _db.Issues.ReplaceDetectedIssues(IssueType.OrphanedDebris, [Issue()]);
        var issue = Assert.Single(_db.Issues.GetIssues());
        _db.History.AddHistory(new HistoryEntry { IssueId = issue.Id, Type = issue.Type, Path = Target,
            RecyclePath = _bin.MoveToBin(Target), FixedAtUtc = DateTime.UtcNow, Success = true });
        return Assert.Single(_db.History.GetHistory());
    }

    [Fact]
    public void MetadataToggleOffBlocksSingleAndBulkApproval()
    {
        _config.OrphanScanMetadata = false;
        _db.Issues.ReplaceDetectedIssues(IssueType.OrphanedDebris, [Issue()]);
        var issue = Assert.Single(_db.Issues.GetIssues());
        Assert.IsType<ConflictObjectResult>(Controller().ApproveIssue(issue.Id));
        Assert.Equal(0, Controller().ApproveAll().Value);
        Assert.IsType<ConflictObjectResult>(Controller().BulkUpdateIssues(new BulkIssueRequest { Action = "Approve", Ids = [issue.Id] }).Result);
        Assert.Equal(IssueStatus.Detected, _db.Issues.GetIssue(issue.Id)!.Status);
    }

    [Fact]
    public void OriginManifestWriteFailureIsReportedBeforeMovingContent()
    {
        var batch = Path.Combine(_root, "manifest-write-failure");
        Directory.CreateDirectory(Path.Combine(batch, RecycleBin.OriginManifestFileName));
        var writer = typeof(RecycleBin).GetMethod("AppendOriginManifest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() => writer.Invoke(_bin, [batch, Target]));
        Assert.True(error.InnerException is IOException or UnauthorizedAccessException);
        Assert.Equal("owned recoverable metadata", File.ReadAllText(Path.Combine(Target, "poster.png")));
    }

    private static void CreateLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)proxy).Call = call;
        return proxy;
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Plugin.AttachedRunState = _previousState;
        Plugin.LastFixRunBacking = _previousSummary;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _services.Dispose();
        Directory.Delete(_root, true);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

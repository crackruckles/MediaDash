using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("MediaDashDb")]
public sealed class FixTaskNoWorkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mediadash-no-work-" + Guid.NewGuid().ToString("N"));
    private readonly MediaDashDb _db;
    private readonly RunState _state = new();
    private readonly RunState? _previousState = Plugin.AttachedRunState;
    private readonly FixRunSummary? _previousSummary = Plugin.LastFixRun;
    private readonly PluginConfiguration _config = new() { DryRun = true, PauseDuringPlayback = false };

    public FixTaskNoWorkTests()
    {
        Directory.CreateDirectory(_directory);
        _db = new MediaDashDb(Path.Combine(_directory, "fixture.db"));
        Plugin.LastFixRun = null;
        Diagnostics.RemoveMatching("FixTask.NoRunnable", "No fixes ran");
    }

    [Theory]
    [InlineData(FixMode.Off, false, false, "What to fix")]
    [InlineData(FixMode.ManualApprove, true, false, "Fix order")]
    [InlineData(FixMode.ManualApprove, false, true, "Libraries")]
    public async Task ApprovedButBlockedIssuesExplainTheReasonAndRemainQueued(FixMode mode, bool disabled, bool outside, string hint)
    {
        _config.AudioFixMode = mode;
        _config.FixerDisabled = disabled ? new[] { "AudioLanguage" } : Array.Empty<string>();
        Add(IssueType.AudioLanguage, queued: true, outside);
        await Run(manual: true);

        Assert.Equal(IssueStatus.Queued, Assert.Single(_db.Issues.GetIssues()).Status);
        var summary = Assert.IsType<FixRunSummary>(Plugin.LastFixRun);
        Assert.Equal(0, summary.Attempted);
        Assert.Contains(hint, summary.NoWorkReason!);
        Assert.Equal(summary.NoWorkReason, _state.ReadSnapshot(RunRole.Fix).PauseReason);
        Assert.Equal(outside ? 1 : 0, summary.SkippedLibraryDisabled);
        Assert.Contains(Diagnostics.Recent(), d => d.Source == "FixTask.NoRunnable" && d.Message == summary.NoWorkReason);
    }

    [Fact]
    public async Task MixedBlockersAreAllReportedWithoutDoubleCounting()
    {
        _config.AudioFixMode = FixMode.Off;
        _config.SubtitleFixMode = FixMode.ManualApprove;
        _config.PlayabilityFixMode = FixMode.ManualApprove;
        _config.FixerDisabled = new[] { "AudioLanguage", "SubtitleLanguage" };
        Add(IssueType.AudioLanguage, true, true);
        Add(IssueType.SubtitleLanguage, true);
        Add(IssueType.Playability, true, true);
        await Run(manual: true);

        var reason = Plugin.LastFixRun!.NoWorkReason!;
        Assert.Contains("1 approved issue(s) have their type set to Off", reason);
        Assert.Contains("1 approved issue(s) have their fixer disabled", reason);
        Assert.Contains("1 approved issue(s) are outside enabled libraries", reason);
        Assert.Equal(3, _db.Issues.GetIssues(status: IssueStatus.Queued).Count);
    }

    [Fact]
    public async Task ManualRunWithoutApprovalExplainsConsentAndPreservesDetectedRows()
    {
        _config.AudioFixMode = FixMode.ManualApprove;
        Add(IssueType.AudioLanguage, queued: false);
        await Run(manual: true);
        Assert.Equal(IssueStatus.Detected, Assert.Single(_db.Issues.GetIssues()).Status);
        Assert.Contains("approve", Plugin.LastFixRun!.NoWorkReason!);
        Assert.DoesNotContain(Diagnostics.Recent(), d => d.Source == "FixTask.NoRunnable");
    }

    [Fact]
    public async Task ManualEmptyRunExplainsScanningAndReplacesPreviousSummary()
    {
        Plugin.LastFixRun = new FixRunSummary { FinishedAtUtc = DateTime.UtcNow.AddDays(-1), Succeeded = 20 };
        await Run(manual: true);
        Assert.Equal(0, Plugin.LastFixRun!.Succeeded);
        Assert.Contains("Scan now", Plugin.LastFixRun.NoWorkReason!);
        Assert.Contains("Scan now", _db.GetState("last_fix_run")!);
    }

    [Fact]
    public async Task ScheduledEmptyRunKeepsThePreviousSummaryAndStaysQuiet()
    {
        var previous = new FixRunSummary { Succeeded = 20 };
        Plugin.LastFixRun = previous;
        await Run(manual: false);
        Assert.Same(previous, Plugin.LastFixRun);
        Assert.DoesNotContain(Diagnostics.Recent(), d => d.Source == "FixTask.NoRunnable");
    }

    [Fact]
    public async Task ANewRunClearsThePreviousNoWorkNoteEvenWhenItDoesNotPublishASummary()
    {
        await Run(manual: true);
        var previous = Plugin.LastFixRun;
        Assert.NotNull(_state.ReadSnapshot(RunRole.Fix).PauseReason);
        await Run(manual: false);
        Assert.Same(previous, Plugin.LastFixRun);
        Assert.Null(_state.ReadSnapshot(RunRole.Fix).PauseReason);
    }

    private void Add(IssueType type, bool queued, bool outside = false)
    {
        _db.Issues.ReplaceDetectedIssues(type, new[] { new Issue { Type = type, Path = Path.Combine(_directory, outside ? "excluded" : "media", type + ".mkv"), DetailsJson = "{}", DetectedAtUtc = DateTime.UtcNow } });
        if (queued)
        {
            _db.Issues.QueueDetectedIssues(type);
        }
    }

    private Task Run(bool manual)
    {
        var library = DispatchProxy.Create<ILibraryManager, IdleCheckTests.ProxyHandler>();
        ((IdleCheckTests.ProxyHandler)(object)library).Handler = method => method.Name == "GetVirtualFolders"
            ? new List<VirtualFolderInfo> { new() { Locations = new[] { Path.Combine(_directory, "media") } } }
            : null;
        if (manual)
        {
            _state.ArmBypassIdleCheck(RunRole.Fix);
        }

        var task = new FixTask(_db.Issues, null!, null!, null!, null!, new LibraryGuard(library), _state,
            new PluginConfigurationProvider(() => _config), null!, null!, NullLogger<FixTask>.Instance);
        // No executor: a blocked/unapproved row must never reach a file operation.
        return task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
    }

    public void Dispose()
    {
        Plugin.LastFixRunBacking = _previousSummary;
        Plugin.AttachedRunState = _previousState;
        Diagnostics.RemoveMatching("FixTask.NoRunnable", "No fixes ran");
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}

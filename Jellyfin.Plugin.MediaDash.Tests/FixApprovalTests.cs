using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Analytics;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("MediaDashDb")]
public sealed class FixApprovalTests : IDisposable
{
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly RunState? _previousState = Plugin.AttachedRunState;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-approval-" + Guid.NewGuid().ToString("N"));
    private readonly MediaDashDb _db;
    private readonly PluginConfiguration _config = new();
    private readonly MediaDashController _controller;
    private readonly RunState _state = new();
    private readonly FixRunSummary? _previousSummary = Plugin.LastFixRun;

    public FixApprovalTests()
    {
        Directory.CreateDirectory(_root);
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        _controller = Controller();
    }

    private MediaDashController Controller(ITaskManager? manager = null, IFixer[]? fixers = null) => new(_db, _db.Issues, _db.History, manager!, null!, null!, null!, null!,
        [], [], null!, null!, _state, null!, null!, null!, fixers);

    [Fact]
    public void ApprovalWithoutARegisteredFixerExplainsTheRestriction()
    {
        var issue = Seed(IssueType.AudioLanguage);
        var controller = Controller(fixers: []);
        var blocked = Assert.IsType<ConflictObjectResult>(controller.ApproveIssue(issue.Id));
        Assert.Contains("No fixer", Assert.IsType<string>(blocked.Value));
        Assert.Equal(0, controller.ApproveAll().Value);
        Assert.IsType<ConflictObjectResult>(controller.ApproveAll(IssueType.AudioLanguage).Result);
        Assert.Equal(IssueStatus.Detected, _db.Issues.GetIssue(issue.Id)!.Status);
    }

    [Fact]
    public void ProtectedIssueCannotBeApprovedInSingleOrBulkPaths()
    {
        var library = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)library).Call = (method, _) => method.Name == "GetVirtualFolders"
            ? new List<VirtualFolderInfo> { new() { Locations = [_root] } } : null;
        var protection = new ProtectionService(_db.Protection, library);
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = _root, FixersMask = "*", CreatedAtUtc = DateTime.UtcNow });
        var controller = new MediaDashController(_db, _db.Issues, _db.History, null!, null!, null!, library, null!,
            [], [], null!, new LibraryGuard(library), _state, null!, null!, protection);
        var issue = Seed(IssueType.AudioLanguage);
        Assert.Contains("protected", Assert.IsType<string>(Assert.IsType<ConflictObjectResult>(controller.ApproveIssue(issue.Id)).Value));
        Assert.Equal(0, controller.ApproveAll().Value);
        Assert.IsType<ConflictObjectResult>(controller.BulkUpdateIssues(new BulkIssueRequest { Action = "Approve", Ids = [issue.Id] }).Result);
        Assert.Equal(IssueStatus.Detected, _db.Issues.GetIssue(issue.Id)!.Status);
    }

    [Fact]
    public void ApprovalRefusesDisabledLibraryInSingleAndBulkPaths()
    {
        var library = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)library).Call = (method, _) => method.Name == "GetVirtualFolders"
            ? new List<VirtualFolderInfo> { new() { ItemId = "enabled", Locations = [Path.Combine(_root, "other")] } } : null;
        var guard = new LibraryGuard(library);
        var controller = new MediaDashController(_db, _db.Issues, _db.History, null!, null!, null!, library, null!,
            [], [], null!, guard, _state, null!, null!, new ProtectionService(_db.Protection, library));
        var issue = Seed(IssueType.AudioLanguage);
        Assert.Contains("library", Assert.IsType<string>(Assert.IsType<ConflictObjectResult>(controller.ApproveIssue(issue.Id)).Value));
        Assert.Equal(0, controller.ApproveAll().Value);
        Assert.IsType<ConflictObjectResult>(controller.BulkUpdateIssues(new BulkIssueRequest { Action = "Approve", Ids = [issue.Id] }).Result);
        var listed = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<IssueDto>>(Assert.IsType<OkObjectResult>(controller.GetIssues().Result).Value));
        Assert.Contains("library", listed.FixBlockedReason!);
        Assert.Equal(IssueStatus.Detected, _db.Issues.GetIssue(issue.Id)!.Status);
    }

    [Theory]
    [InlineData(IssueType.Quality)]
    [InlineData(IssueType.Playability)]
    public void ReportOnlyFindingsStayVisibleAndCannotBeApproved(IssueType type)
    {
        var issue = Seed(type, "{\"fixerAvailable\":false}");
        var blocked = Assert.IsType<ConflictObjectResult>(_controller.ApproveIssue(issue.Id));
        Assert.Contains("report-only", Assert.IsType<string>(blocked.Value));
        Assert.Equal(0, _controller.ApproveAll().Value);
        Assert.IsType<ConflictObjectResult>(_controller.BulkUpdateIssues(new BulkIssueRequest { Action = "Approve", Ids = [issue.Id] }).Result);
        var listed = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<IssueDto>>(Assert.IsType<OkObjectResult>(_controller.GetIssues().Result).Value));
        Assert.Contains("report-only", listed.FixBlockedReason!);
        Assert.Equal(IssueStatus.Detected, _db.GetIssueStatus(issue.Id));
    }

    [Theory]
    [InlineData(IssueType.Ungrouped)]
    [InlineData(IssueType.Misplaced)]
    public void LegacyMoveFindingsRequireScanInsteadOfSettings(IssueType type)
    {
        var issue = Seed(type, "{\"action\":\"MoveFolder\"}");
        var listed = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<IssueDto>>(Assert.IsType<OkObjectResult>(_controller.GetIssues().Result).Value));
        Assert.True(listed.FixRequiresRescan);
        Assert.Contains("Re-scan", listed.FixBlockedReason!);
        Assert.IsType<ConflictObjectResult>(_controller.ApproveIssue(issue.Id));
        Assert.Equal(IssueStatus.Detected, _db.GetIssueStatus(issue.Id));
    }

    private Issue Seed(IssueType type, string details = "{}")
    {
        _db.Issues.ReplaceDetectedIssues(type, [new Issue { Type = type, Path = Path.Combine(_root, type + ".mkv"), DetailsJson = details, DetectedAtUtc = DateTime.UtcNow }]);
        return Assert.Single(_db.Issues.GetIssues(type));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("not json", false)]
    [InlineData("null", false)]
    [InlineData("[]", false)]
    [InlineData("{}", false)]
    [InlineData("{\"fixerAvailable\":true}", false)]
    [InlineData("{\"fixerAvailable\":false}", true)]
    public void ReportOnlyMetadataIsExplicitAndToleratesMalformedRows(string details, bool reportOnly)
        => Assert.Equal(reportOnly, new Issue { DetailsJson = details }.IsReportOnly);

    [Theory]
    [InlineData(FixMode.Off, false, "Off")]
    [InlineData(FixMode.ManualApprove, true, "Fix order")]
    public void SingleApprovalRefusesBlockedModeWithoutQueuing(FixMode mode, bool disabled, string hint)
    {
        _config.AudioFixMode = mode;
        _config.FixerDisabled = disabled ? ["AudioLanguage"] : [];
        var issue = Seed(IssueType.AudioLanguage);
        var result = Assert.IsType<ConflictObjectResult>(_controller.ApproveIssue(issue.Id));
        Assert.Contains(hint, Assert.IsType<string>(result.Value));
        Assert.Equal(IssueStatus.Detected, _db.Issues.GetIssue(issue.Id)!.Status);
    }

    [Fact]
    public void SwitchingToAskMeFirstAllowsApprovalWithoutRescanning()
    {
        _config.AudioFixMode = FixMode.Off;
        var issue = Seed(IssueType.AudioLanguage);
        Assert.IsType<ConflictObjectResult>(_controller.ApproveIssue(issue.Id));
        _config.AudioFixMode = FixMode.ManualApprove;
        Assert.IsType<NoContentResult>(_controller.ApproveIssue(issue.Id));
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.IsType<ConflictObjectResult>(_controller.ApproveIssue(issue.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BulkApprovalQueuesOnlyEnabledTypes(bool legacy)
    {
        _config.AudioFixMode = FixMode.ManualApprove;
        _config.TranscodeFixMode = FixMode.Off;
        var allowed = Seed(IssueType.AudioLanguage);
        var blocked = Seed(IssueType.Quality);
        var result = legacy ? _controller.ApproveAll() : _controller.BulkUpdateIssues(new BulkIssueRequest { Ids = [allowed.Id, blocked.Id], Action = "Approve" });
        Assert.Equal(1, result.Value);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(allowed.Id)!.Status);
        Assert.Equal(IssueStatus.Detected, _db.Issues.GetIssue(blocked.Id)!.Status);
    }

    [Fact]
    public void BlockedLegacyTypeApprovalExplainsWhy()
    {
        _config.TranscodeFixMode = FixMode.Off;
        Seed(IssueType.Quality);
        Assert.IsType<ConflictObjectResult>(_controller.ApproveAll(IssueType.Quality).Result);
    }

    [Fact]
    public void IssuesExplainExistingBlockedQueueAndRefreshAfterSettingsChange()
    {
        _config.AudioFixMode = FixMode.Off;
        var issue = Seed(IssueType.AudioLanguage);
        _db.Issues.QueueDetectedIssues(issue.Type);
        IssueDto Listed() => Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<IssueDto>>(Assert.IsType<OkObjectResult>(_controller.GetIssues(openOnly: true).Result).Value));
        Assert.Contains("Off", Listed().FixBlockedReason!);
        _config.AudioFixMode = FixMode.ManualApprove;
        Assert.Null(Listed().FixBlockedReason);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
    }

    [Fact]
    public void BlockedApprovalDoesNotPatchMixedLibraryConsent()
    {
        _config.FormatCompatibilityFixMode = FixMode.Off;
        var issue = Seed(IssueType.FormatCompatibility);
        _db.Issues.UpdateDetails(issue.Id, "{\"mixedLibrary\":true}");
        Assert.IsType<ConflictObjectResult>(_controller.ApproveIssue(issue.Id));
        Assert.Equal("{\"mixedLibrary\":true}", _db.Issues.GetIssue(issue.Id)!.DetailsJson);
    }

    [Fact]
    public void BulkApprovalPreservesReviewConsentAndDoesNotCountAlreadyQueuedRows()
    {
        _config.AudioFixMode = FixMode.ManualApprove;
        _config.TranscodeFixMode = FixMode.ManualApprove;
        var queued = Seed(IssueType.AudioLanguage);
        var review = Seed(IssueType.Quality);
        _db.Issues.QueueDetectedIssues(queued.Type);
        _db.Issues.UpdateDetails(review.Id, "{\"warnings\":[{\"code\":\"test\",\"message\":\"Review required\",\"blocking\":true}]}");
        Assert.Equal(0, _controller.BulkUpdateIssues(new BulkIssueRequest { Action = "Approve", Ids = [queued.Id, review.Id] }).Value);
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(queued.Id)!.Status);
        Assert.Equal(IssueStatus.Detected, _db.Issues.GetIssue(review.Id)!.Status);
        Assert.IsType<NoContentResult>(_controller.ApproveIssue(review.Id));
    }

    [Fact]
    public void BlockedBulkApprovalExplainsReasonAndDismissStillWorks()
    {
        _config.TranscodeFixMode = FixMode.Off;
        var issue = Seed(IssueType.Quality);
        var request = new BulkIssueRequest { Action = "Approve", Ids = [issue.Id] };
        Assert.IsType<ConflictObjectResult>(_controller.BulkUpdateIssues(request).Result);
        request.Action = "Dismiss";
        Assert.Equal(1, _controller.BulkUpdateIssues(request).Value);
        Assert.Equal(IssueStatus.Dismissed, _db.Issues.GetIssue(issue.Id)!.Status);
    }

    [Theory]
    [InlineData(false, TaskState.Idle, false, 503)]
    [InlineData(true, TaskState.Running, false, 409)]
    [InlineData(true, TaskState.Cancelling, false, 409)]
    [InlineData(true, TaskState.Idle, true, 503)]
    [InlineData(true, TaskState.Idle, false, 204)]
    public void StartFixReportsDispatchFailureAndDoesNotLeakManualBypass(bool registered, TaskState taskState, bool throws, int status)
    {
        var worker = DispatchProxy.Create<IScheduledTaskWorker, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)worker).Call = (method, _) => method.Name switch
        {
            "get_ScheduledTask" => RuntimeHelpers.GetUninitializedObject(typeof(FixTask)),
            "get_State" => taskState,
            _ => null
        };
        var executed = 0;
        var manager = DispatchProxy.Create<ITaskManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, _) =>
        {
            if (method.Name == "get_ScheduledTasks") return registered ? new[] { worker } : Array.Empty<IScheduledTaskWorker>();
            if (method.Name == "Execute")
            {
                executed++;
                if (throws) throw new InvalidOperationException("dispatch failed");
                return Task.CompletedTask;
            }
            return null;
        };
        var result = Controller(manager).StartFix();
        Assert.Equal(status, result is NoContentResult ? 204 : Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(registered && taskState == TaskState.Idle ? 1 : 0, executed);
        Assert.Equal(status == 204, _state.ConsumeBypassIdleCheck(RunRole.Fix));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsynchronousDispatchFailuresAreObservedWithoutWaitingForTheRun(bool alreadyFailed)
    {
        var execution = new TaskCompletionSource();
        if (alreadyFailed) execution.SetException(new InvalidOperationException("async dispatch failed"));
        var worker = DispatchProxy.Create<IScheduledTaskWorker, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)worker).Call = (method, _) => method.Name switch
        {
            "get_ScheduledTask" => RuntimeHelpers.GetUninitializedObject(typeof(FixTask)),
            "get_State" => TaskState.Idle,
            _ => null
        };
        var manager = DispatchProxy.Create<ITaskManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, _) => method.Name switch
        {
            "get_ScheduledTasks" => new[] { worker },
            "Execute" => execution.Task,
            _ => null
        };
        var result = Controller(manager).StartFix();
        if (alreadyFailed) Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
        else
        {
            Assert.IsType<NoContentResult>(result);
            execution.SetException(new InvalidOperationException("async dispatch failed"));
        }
        Assert.False(_state.ConsumeBypassIdleCheck(RunRole.Fix));
        Assert.Contains("Could not start", _state.ReadSnapshot(RunRole.Fix).PauseReason!);
        Assert.Contains(Diagnostics.Recent(), d => d.Source == "FixTask.Start" && d.Message.Contains("async dispatch failed"));
    }

    [Fact]
    public async Task ApprovalRunsThroughPlannerExecutorAndHistoryAfterModeChange()
    {
        _config.DryRun = true;
        _config.PauseDuringPlayback = false;
        _config.AnalyticsEnabled = false;
        _config.AudioFixMode = FixMode.Off;
        var issue = Seed(IssueType.AudioLanguage);
        File.WriteAllText(issue.Path, "owned fixture stays unchanged");
        Assert.IsType<ConflictObjectResult>(_controller.ApproveIssue(issue.Id));
        _config.AudioFixMode = FixMode.ManualApprove;
        Assert.IsType<NoContentResult>(_controller.ApproveIssue(issue.Id));

        var library = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)library).Call = (method, _) => method.Name == "GetVirtualFolders"
            ? new List<VirtualFolderInfo> { new() { Locations = [_root] } } : null;
        var provider = new PluginConfigurationProvider(() => _config);
        var fixer = new RecordingFixer();
        var executor = new FixExecutor(_db.Issues, [fixer], provider, TimeProvider.System,
            new ProtectionService(_db.Protection, library), NullLogger<FixExecutor>.Instance);
        var task = new FixTask(_db.Issues, null!, null!, null!,
            new AnalyticsReporter(_db.History, _db.Issues, null!, NullLogger<AnalyticsReporter>.Instance),
            new LibraryGuard(library), _state, provider, executor,
            new FixResultRecorder(_db, NullLogger<FixResultRecorder>.Instance), NullLogger<FixTask>.Instance);
        _state.ArmBypassIdleCheck(RunRole.Fix);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Equal(1, fixer.Calls);
        Assert.Equal(1, Plugin.LastFixRun!.Attempted);
        Assert.Equal(0, Plugin.LastFixRun.Failed);
        Assert.Single(_db.History.GetHistory());
        Assert.Equal(IssueStatus.Queued, _db.Issues.GetIssue(issue.Id)!.Status);
        Assert.Equal("owned fixture stays unchanged", File.ReadAllText(issue.Path));

        _config.AudioFixMode = FixMode.Off;
        _state.ArmBypassIdleCheck(RunRole.Fix);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Equal(1, fixer.Calls);
        Assert.Contains("set to Off", Plugin.LastFixRun!.NoWorkReason!);
    }

    private sealed class RecordingFixer : IFixer
    {
        public int Calls { get; private set; }
        public bool CanFix(IssueType type) => type == IssueType.AudioLanguage;
        public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(FixResult.DryRun("fixture reached fixer", 0));
        }
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Plugin.AttachedRunState = _previousState;
        Plugin.LastFixRunBacking = _previousSummary;
        Diagnostics.RemoveMatching("FixTask.Start", "Could not start the fix task");
        Diagnostics.RemoveMatching("FixTask.NoRunnable", "No fixes ran");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}

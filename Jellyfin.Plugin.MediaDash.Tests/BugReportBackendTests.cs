using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("MediaDashDb")]
public sealed class BugReportBackendTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-bug-report-" + Guid.NewGuid().ToString("N"));
    private readonly MediaDashDb _db;
    public BugReportBackendTests()
    {
        Directory.CreateDirectory(_root);
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
    }

    [Theory]
    [InlineData(false, MediaBrowser.Model.Tasks.TaskState.Idle, 503)]
    [InlineData(true, MediaBrowser.Model.Tasks.TaskState.Running, 409)]
    [InlineData(true, MediaBrowser.Model.Tasks.TaskState.Cancelling, 409)]
    public void StartScan_ReportsMissingOrBusyTask(bool registered, MediaBrowser.Model.Tasks.TaskState state, int status)
    {
        var scan = (ScanTask)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ScanTask));
        var worker = DispatchProxy.Create<MediaBrowser.Model.Tasks.IScheduledTaskWorker, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)worker).Call = (method, _) => method.Name switch
        {
            "get_ScheduledTask" => scan,
            "get_State" => state,
            _ => null
        };
        var manager = DispatchProxy.Create<MediaBrowser.Model.Tasks.ITaskManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, _) => method.Name == "get_ScheduledTasks"
            ? (registered ? new[] { worker } : Array.Empty<MediaBrowser.Model.Tasks.IScheduledTaskWorker>()) : null;
        var controller = new MediaDashController(null!, null!, null!, manager, null!, null!, null!, null!, [], [], null!, null!, new RunState(), null!, null!, null!);
        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(controller.StartScan()).StatusCode);
    }

    [Fact]
    public void HistoryEndpoints_MalformedLibraryLocation_StillReturnPartialResults()
    {
        var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)library).Call = (method, _) => method.Name == "GetVirtualFolders"
            ? new System.Collections.Generic.List<MediaBrowser.Model.Entities.VirtualFolderInfo> { new() { Name = "Library", Locations = ["\0", _root] } }
            : null;
        var controller = new MediaDashController(_db, _db.Issues, _db.History, null!, null!, null!, library, null!, [], [], null!, null!, new RunState(), null!, null!, null!);
        Assert.IsType<OkObjectResult>(controller.GetHistory().Result);
        Assert.IsType<OkObjectResult>(controller.GetHistoryStats().Result);
    }

    [Fact]
    public void AutomaticQueue_ConsentGateCommitsOnlySafeFindings()
    {
        var details = new[] { "{}", "{\"warnings\":[{\"blocking\":true}]}", "{\"fixerAvailable\":false}", "{\"requiresRescan\":true}" };
        _db.Issues.ReplaceDetectedIssues(IssueType.AudioLanguage, details.Select((json, i) => new Issue
        { Type = IssueType.AudioLanguage, Path = Path.Combine(_root, i + ".mkv"), DetailsJson = json, DetectedAtUtc = DateTime.UtcNow }).ToArray());
        var queued = _db.Issues.QueueDetectedIssues(IssueType.AudioLanguage, requireConsent: true);
        Assert.Single(queued);
        Assert.Single(_db.Issues.GetIssues(IssueType.AudioLanguage, IssueStatus.Queued));
        Assert.Equal(3, _db.Issues.GetIssues(IssueType.AudioLanguage, IssueStatus.Detected).Count);
        Assert.Equal(0, _db.Issues.RollbackAutoQueuedBlockingWarnings(queued));
    }

    [Fact]
    public void RecycleHistory_IncludesOlderThanTheFiveHundredRowHistoryWindow()
    {
        _db.History.AddHistory(new HistoryEntry { Type = IssueType.Duplicate, Path = Path.Combine(_root, "old.mkv"), RecyclePath = Path.Combine(_root, "bin", "old.mkv"), FixedAtUtc = DateTime.UtcNow, Success = true });
        for (var i = 0; i < 501; i++)
            _db.History.AddHistory(new HistoryEntry { Type = IssueType.Quality, Path = Path.Combine(_root, i + ".mkv"), FixedAtUtc = DateTime.UtcNow, Success = true });
        Assert.DoesNotContain(_db.History.GetHistory(), entry => entry.RecyclePath is not null);
        Assert.EndsWith("old.mkv", Assert.Single(_db.History.GetActiveRecycleEntries()).Path);
    }

    [Fact]
    public void GetIssues_FiveThousandRows_ResolvesProtectionLibrariesOnce()
    {
        _db.Issues.ReplaceDetectedIssues(IssueType.AudioLanguage, Enumerable.Range(0, 5000).Select(i => new Issue
        { Type = IssueType.AudioLanguage, Path = Path.Combine(_root, i + ".mkv"), DetectedAtUtc = DateTime.UtcNow }).ToArray());
        var library = DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        var calls = 0;
        ((OriginalLanguageFlowTests.CallProxy)(object)library).Call = (method, _) =>
        {
            if (method.Name == "GetItemList") return Array.Empty<MediaBrowser.Controller.Entities.BaseItem>();
            if (method.Name != "GetVirtualFolders") return null;
            calls++;
            System.Threading.Thread.Sleep(1);
            return new System.Collections.Generic.List<MediaBrowser.Model.Entities.VirtualFolderInfo> { new() { ItemId = "library", Locations = [_root] } };
        };
        _db.Protection.Add(new ProtectionRule { Kind = "library", Target = "different-library", FixersMask = "*", CreatedAtUtc = DateTime.UtcNow });
        var controller = new MediaDashController(_db, _db.Issues, _db.History, null!, null!, null!, library, null!, [], [], null!, null!, new RunState(), null!, null!, new Fixers.ProtectionService(_db.Protection, library));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rows = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<IssueDto>>(Assert.IsType<OkObjectResult>(controller.GetIssues().Result).Value);
        watch.Stop();
        Assert.Equal(5000, rows.Count);
        Assert.True(calls <= 2, $"Protection resolved libraries {calls} times; elapsed {watch.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Listing took {watch.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public void BulkRevert_HandlesQueuedAndDismissedButLeavesFixed()
    {
        _db.Issues.ReplaceDetectedIssues(IssueType.Quality, Enumerable.Range(0, 4).Select(i => new Issue
        { Type = IssueType.Quality, Path = Path.Combine(_root, i + ".mkv"), DetectedAtUtc = DateTime.UtcNow }).ToArray());
        var rows = _db.Issues.GetIssues(IssueType.Quality).ToArray();
        _db.Issues.BulkUpdateOpenIssueStatus([rows[0].Id], IssueStatus.Queued);
        _db.Issues.BulkUpdateOpenIssueStatus([rows[1].Id], IssueStatus.Dismissed);
        _db.Issues.TryTransitionIssueStatus(rows[2].Id, [IssueStatus.Detected], IssueStatus.Fixed);
        Assert.Equal(2, _db.Issues.BulkRevertDismissedIssues(rows.Select(r => r.Id).ToArray()));
        Assert.Equal(IssueStatus.Fixed, _db.Issues.GetIssueStatus(rows[2].Id));
        Assert.All(rows.Take(2), row => Assert.Equal(IssueStatus.Detected, _db.Issues.GetIssueStatus(row.Id)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void PatchApprovedInMixedLibrary_NonObjectRoot_ReturnsNull(string json)
    {
        var method = typeof(MediaDashController).GetMethod("PatchApprovedInMixedLibrary", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Null(method.Invoke(null, [json]));
    }

    [Fact]
    public void InvalidEnumFilters_ReturnBadRequestBeforeQuerying()
    {
        var controller = new MediaDashController(null!, null!, null!, null!, null!, null!, null!, null!, [], [], null!, null!, new RunState(), null!, null!, null!);
        Assert.IsType<BadRequestObjectResult>(controller.GetIssues(type: (IssueType)999).Result);
        Assert.IsType<BadRequestObjectResult>(controller.GetIssues(status: (IssueStatus)999).Result);
    }

    [Fact]
    public void SchemaV13Upgrade_AddsPathIndexes()
    {
        using var connection = _db.ConnectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP INDEX IF EXISTS idx_issues_type_path; DROP INDEX IF EXISTS idx_history_type_path; PRAGMA user_version=13";
        command.ExecuteNonQuery();
        MediaDashSchema.Migrate(connection);
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name IN ('idx_issues_type_path','idx_history_type_path')";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}





using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("MediaDashDb")]
public sealed class FailedPlayabilityRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-failed-repair-" + Guid.NewGuid().ToString("N"));
    private readonly MediaDashDb _db;
    private readonly string _path;

    public FailedPlayabilityRepairTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "movie.mkv");
        File.WriteAllBytes(_path, new byte[] { 1, 2, 3 });
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
    }

    private Issue Fresh() => new() { Type = IssueType.Playability, Path = _path, DetailsJson = """{"reason":"decode-error","detail":"damaged stream"}""", DetectedAtUtc = DateTime.UtcNow };

    private Issue RecordFailure(bool dryRun = false, bool dismissed = false)
    {
        _db.ReplaceDetectedIssues(IssueType.Playability, new[] { Fresh() });
        var issue = Assert.Single(_db.GetIssues());
        _db.TryTransitionIssueStatus(issue.Id, new[] { IssueStatus.Detected }, IssueStatus.Queued);
        issue.Status = IssueStatus.Queued;
        var file = new FileInfo(_path);
        PlayabilityRepairState.Apply(issue, file.Length, file.LastWriteTimeUtc.Ticks);
        if (dismissed) _db.TryTransitionIssueStatus(issue.Id, new[] { IssueStatus.Queued }, IssueStatus.Dismissed);
        var result = FixResult.Fail(FixReason.RepairFailed, "Repair failed; original preserved.");
        result.WasDryRun = dryRun;
        new FixResultRecorder(_db, NullLogger<FixResultRecorder>.Instance).Record(
            new PlannedFix(PlannedFixKind.Single, issue, Array.Empty<Issue>()),
            new FixExecutionResult(result, issue.Id, Array.Empty<long>()), DateTime.UtcNow);
        return Assert.Single(_db.GetIssues());
    }

    [Fact]
    public void FailedRepairReturnsToIssuesWithDeleteAndRequiresFreshApproval()
    {
        var issue = RecordFailure();
        Assert.Equal(IssueStatus.Detected, issue.Status);
        Assert.True(IssueDto.FromIssue(issue).RepairFailed);
        Assert.True(issue.HasBlockingWarnings);
        Assert.False(Assert.Single(_db.GetHistory()).Success);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(_path));
        var queued = _db.Issues.QueueDetectedIssues(IssueType.Playability);
        Assert.Equal(1, _db.Issues.RollbackAutoQueuedBlockingWarnings(queued));
        Assert.Equal(IssueStatus.Detected, Assert.Single(_db.GetIssues()).Status);
        Assert.Equal(StatusTransitionResult.Applied, _db.TryTransitionIssueStatus(issue.Id, new[] { IssueStatus.Detected }, IssueStatus.Queued));
    }

    [Fact]
    public void RescanPreservesDeleteForUnchangedFileAndClearsItForReplacement()
    {
        RecordFailure();
        _db.ReplaceDetectedIssues(IssueType.Playability, new[] { Fresh() });
        Assert.True(IssueDto.FromIssue(Assert.Single(_db.GetIssues())).RepairFailed);
        Assert.True(Assert.Single(_db.GetIssues()).HasBlockingWarnings);
        File.WriteAllBytes(_path, new byte[] { 4, 5, 6, 7 });
        Assert.False(IssueDto.FromIssue(Assert.Single(_db.GetIssues())).RepairFailed);
        _db.ReplaceDetectedIssues(IssueType.Playability, new[] { Fresh() });
        Assert.False(Assert.Single(_db.GetIssues()).HasBlockingWarnings);
        Assert.False(IssueDto.FromIssue(Assert.Single(_db.GetIssues())).RepairFailed);
    }

    [Fact]
    public void HealthyRescanClearsFailedRepair()
    {
        RecordFailure();
        _db.ReplaceDetectedIssues(IssueType.Playability, Array.Empty<Issue>());
        Assert.Empty(_db.GetIssues());
    }

    [Fact]
    public void DryRunDoesNotChangeTheQueueOrOfferDelete()
    {
        var issue = RecordFailure(dryRun: true);
        Assert.Equal(IssueStatus.Queued, issue.Status);
        Assert.False(IssueDto.FromIssue(issue).RepairFailed);
        Assert.False(issue.HasBlockingWarnings);
    }

    [Fact]
    public void DismissalDuringRepairIsPreserved()
        => Assert.Equal(IssueStatus.Dismissed, RecordFailure(dismissed: true).Status);

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"repair_failed\":true}")]
    [InlineData("{\"repair_failed\":{\"length\":\"3\",\"mtime_ticks\":1}}")]
    [InlineData("broken json")]
    public void MalformedFailureStateNeverOffersDelete(string json)
    {
        var issue = Fresh();
        issue.DetailsJson = json;
        Assert.False(PlayabilityRepairState.IsCurrent(issue));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}

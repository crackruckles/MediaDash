using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// State transitions for the Issues-tab Undo button. The controller wraps a DB status update,
/// so these tests hit the DB layer directly: revert must be legal from Queued and Dismissed,
/// and must NOT be legal from Fixed (that means the file has already been touched — Restore is
/// the right escape hatch there, not Revert).
/// </summary>
public sealed class IssueRevertTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-revert-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    public IssueRevertTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void QueuedCanRevertToDetected()
    {
        var id = InsertIssue();
        Assert.True(_db.UpdateIssueStatus(id, IssueStatus.Queued));

        Assert.True(_db.UpdateIssueStatus(id, IssueStatus.Detected));
        Assert.Equal(IssueStatus.Detected, _db.GetIssueStatus(id));
    }

    [Fact]
    public void DismissedCanRevertToDetected()
    {
        var id = InsertIssue();
        Assert.True(_db.UpdateIssueStatus(id, IssueStatus.Dismissed));

        Assert.True(_db.UpdateIssueStatus(id, IssueStatus.Detected));
        Assert.Equal(IssueStatus.Detected, _db.GetIssueStatus(id));
    }

    [Fact]
    public void RevertingRemovesQueuedFromTheFixQueueSnapshot()
    {
        // The FixTask pulls Queued issues via GetIssues(status: Queued). Reverting must remove the
        // issue from that pull, otherwise the fix task drains a queued-but-cancelled item next
        // interval.
        var id = InsertIssue();
        _db.UpdateIssueStatus(id, IssueStatus.Queued);
        _db.UpdateIssueStatus(id, IssueStatus.Detected);

        var queuedNow = _db.GetIssues(status: IssueStatus.Queued);
        Assert.DoesNotContain(queuedNow, i => i.Id == id);
    }

    [Fact]
    public void UnknownIssueIdCannotBeReverted()
    {
        // Controller returns 404 in this case; the DB layer returns false. Same signal.
        Assert.Null(_db.GetIssueStatus(9999));
        Assert.False(_db.UpdateIssueStatus(9999, IssueStatus.Detected));
    }

    private long InsertIssue()
    {
        _db.ReplaceDetectedIssues(IssueType.OrphanedDebris, [new Issue
        {
            Type = IssueType.OrphanedDebris,
            ItemId = Guid.NewGuid(),
            Path = "/lib/orphan.srt",
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow
        }]);

        var rows = _db.GetIssues(IssueType.OrphanedDebris, IssueStatus.Detected);
        Assert.Single(rows);
        return rows[0].Id;
    }
}

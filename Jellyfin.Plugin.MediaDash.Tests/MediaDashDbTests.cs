using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class MediaDashDbTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-test-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    public MediaDashDbTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private static Issue Make(string path) => new()
    {
        Type = IssueType.Playability,
        ItemId = Guid.NewGuid(),
        Path = path,
        Status = IssueStatus.Detected,
        DetectedAtUtc = DateTime.UtcNow
    };

    [Fact]
    public void ScopedScanDoesNotWipeIssuesOutsideItsScope()
    {
        _db.ReplaceDetectedIssues(IssueType.Playability, [Make("A"), Make("B")]);

        // A scan that only covered path A and found nothing there.
        _db.ReplaceDetectedIssues(IssueType.Playability, [], ["A"]);

        var remaining = _db.GetIssues(IssueType.Playability, IssueStatus.Detected);
        Assert.Single(remaining);
        Assert.Equal("B", remaining[0].Path);
    }

    [Fact]
    public void FixedIssueDoesNotBlockReDetectionOfTheSamePath()
    {
        _db.ReplaceDetectedIssues(IssueType.Playability, [Make("A")]);
        var id = _db.GetIssues(IssueType.Playability)[0].Id;
        _db.UpdateIssueStatus(id, IssueStatus.Fixed);

        _db.ReplaceDetectedIssues(IssueType.Playability, [Make("A")]);

        Assert.Single(_db.GetIssues(IssueType.Playability, IssueStatus.Detected));
    }

    [Fact]
    public void DismissedIssueSuppressesReDetectionOfTheSamePath()
    {
        _db.ReplaceDetectedIssues(IssueType.Playability, [Make("A")]);
        var id = _db.GetIssues(IssueType.Playability)[0].Id;
        _db.UpdateIssueStatus(id, IssueStatus.Dismissed);

        _db.ReplaceDetectedIssues(IssueType.Playability, [Make("A")]);

        Assert.Empty(_db.GetIssues(IssueType.Playability, IssueStatus.Detected));
    }
}

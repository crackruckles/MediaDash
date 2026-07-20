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

    [Fact]
    public void ResetScanStateWipesIssuesAndCachesButKeepsHistory()
    {
        _db.ReplaceDetectedIssues(IssueType.Playability, [Make("A")]);
        _db.StoreDecode("A", 1, 2, "some error");
        _db.StoreProbe("A", 1, 2, "{}");
        _db.AddHistory(new HistoryEntry
        {
            IssueId = 1,
            Type = IssueType.Playability,
            Path = "A",
            Action = "removed",
            BytesFreed = 100,
            FixedAtUtc = DateTime.UtcNow
        });

        _db.ResetScanState();

        Assert.Empty(_db.GetIssues());
        Assert.Null(_db.GetCachedDecode("A", 1, 2));
        Assert.Null(_db.GetCachedProbe("A", 1, 2));
        Assert.Single(_db.GetHistory());
    }

    [Fact]
    public void SchemaMigrationClearsStaleDecodeCache()
    {
        _db.StoreDecode("A", 1, 2, "stale error from old check");
        Assert.Equal("stale error from old check", _db.GetCachedDecode("A", 1, 2));

        // Simulate an older-version database by resetting user_version to 0 while the file exists.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 0";
            cmd.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // A new instance runs the migration; the stale decode entry should be wiped.
        var reopened = new MediaDashDb(_dbPath);
        Assert.Null(reopened.GetCachedDecode("A", 1, 2));
    }
}

using System;
using System.IO;
using System.Linq;
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
    public void RelocateIssuePaths_ExactFileMatch_RewritesInPlace()
    {
        _db.ReplaceDetectedIssues(IssueType.Playability, [Make("/tv/Show.S01E01.mkv"), Make("/tv/Other.mkv")]);

        var moved = _db.RelocateIssuePaths("/tv/Show.S01E01.mkv", "/tv/Show/Show.S01E01.mkv");

        Assert.Equal(1, moved);
        var paths = _db.GetIssues(IssueType.Playability).Select(i => i.Path).OrderBy(p => p).ToArray();
        Assert.Equal(new[] { "/tv/Other.mkv", "/tv/Show/Show.S01E01.mkv" }, paths);
    }

    [Fact]
    public void RelocateIssuePaths_FolderMove_RewritesEverythingUnderThePrefix()
    {
        // A folder move should carry every child issue with it (audio-language on file A, subtitle on file B, …).
        _db.ReplaceDetectedIssues(IssueType.Playability,
        [
            Make("/tv/ShowS01E01/video.mkv"),
            Make("/tv/ShowS01E01/subs.srt"),
            Make("/tv/OtherShow/video.mkv"),
        ]);

        var moved = _db.RelocateIssuePaths("/tv/ShowS01E01", "/tv/My Show/ShowS01E01");

        Assert.Equal(2, moved);
        var paths = _db.GetIssues(IssueType.Playability).Select(i => i.Path).OrderBy(p => p).ToArray();
        Assert.Equal(new[]
        {
            "/tv/My Show/ShowS01E01/subs.srt",
            "/tv/My Show/ShowS01E01/video.mkv",
            "/tv/OtherShow/video.mkv"
        }, paths);
    }

    [Fact]
    public void RelocateIssuePaths_NoMatch_NoOp()
    {
        _db.ReplaceDetectedIssues(IssueType.Playability, [Make("/tv/A.mkv")]);
        var moved = _db.RelocateIssuePaths("/tv/does-not-exist.mkv", "/tv/wherever.mkv");
        Assert.Equal(0, moved);
    }

    [Fact]
    public void RelocateIssuePaths_DoesNotOverMatchSimilarFolderNames()
    {
        // "/tv/Show" as prefix must NOT match "/tv/Show2/…". Requires a '/' or '\' boundary after the prefix.
        _db.ReplaceDetectedIssues(IssueType.Playability, [Make("/tv/Show2/video.mkv")]);
        var moved = _db.RelocateIssuePaths("/tv/Show", "/tv/Renamed");
        Assert.Equal(0, moved);
        Assert.Equal("/tv/Show2/video.mkv", _db.GetIssues(IssueType.Playability)[0].Path);
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

    [Fact]
    public void AcknowledgeHistoryEntry_SetsFlag()
    {
        _db.AddHistory(new HistoryEntry
        {
            IssueId = 1,
            Type = IssueType.SubtitleLanguage,
            Path = "/library/Film.mkv",
            Action = "test",
            BytesFreed = 4_000_000_000,
            RecyclePath = "/bin/orig/Film.mkv",
            FixedAtUtc = DateTime.UtcNow,
            Success = true
        });

        var row = _db.GetHistory().Single();
        Assert.False(row.Acknowledged);

        Assert.True(_db.AcknowledgeHistoryEntry(row.Id));

        var reread = _db.GetHistoryEntry(row.Id);
        Assert.NotNull(reread);
        Assert.True(reread!.Acknowledged);
    }

    [Fact]
    public void AcknowledgeHistoryEntry_ReturnsFalseWhenRowMissing()
    {
        Assert.False(_db.AcknowledgeHistoryEntry(9999));
    }

    [Fact]
    public void FormatProbeCache_RoundTrips()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mediadash-fmt-{Guid.NewGuid():N}.db");
        try
        {
            var db = new MediaDashDb(dbPath);

            Assert.Null(db.GetCachedFormatProbe("/a.epub", 100, 1234));

            db.StoreFormatProbe("/a.epub", 100, 1234, ok: true, reason: null);
            var hit = db.GetCachedFormatProbe("/a.epub", 100, 1234);
            Assert.NotNull(hit);
            Assert.True(hit!.Value.Ok);
            Assert.Null(hit.Value.Reason);

            db.StoreFormatProbe("/a.epub", 100, 1234, ok: false, reason: "truncated");
            var updated = db.GetCachedFormatProbe("/a.epub", 100, 1234);
            Assert.NotNull(updated);
            Assert.False(updated!.Value.Ok);
            Assert.Equal("truncated", updated.Value.Reason);

            // Size or mtime mismatch invalidates the cache.
            Assert.Null(db.GetCachedFormatProbe("/a.epub", 101, 1234));
            Assert.Null(db.GetCachedFormatProbe("/a.epub", 100, 9999));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}

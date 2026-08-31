using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// The restored_paths table is the source of truth for the "never auto-fix a file the user
/// restored" invariant. Every read/write helper is exercised here plus the interaction with
/// QueueDetectedIssues so the auto-queue gate can't be silently bypassed by a schema tweak.
/// </summary>
public sealed class RestoredPathsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-restored-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    public RestoredPathsTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void UnrestoredPath_IsNotFlagged()
    {
        Assert.False(_db.WasPathRestored("/lib/movies/Movie.mkv", IssueType.Duplicate));
    }

    [Fact]
    public void MarkPathRestored_MakesTheExactTupleFlagged()
    {
        _db.MarkPathRestored("/lib/movies/Movie.mkv", IssueType.Duplicate);
        Assert.True(_db.WasPathRestored("/lib/movies/Movie.mkv", IssueType.Duplicate));
    }

    [Fact]
    public void MarkPathRestored_ForOneType_DoesNotBlockOtherTypesAtSamePath()
    {
        // A user restoring a Duplicate fix must not also block SubtitleLanguage auto-fixes on the
        // same file — those are independent decisions. Only the manifest-only "any type" sentinel
        // blocks across types.
        _db.MarkPathRestored("/lib/tv/E.mkv", IssueType.Duplicate);
        Assert.True(_db.WasPathRestored("/lib/tv/E.mkv", IssueType.Duplicate));
        Assert.False(_db.WasPathRestored("/lib/tv/E.mkv", IssueType.SubtitleLanguage));
    }

    [Fact]
    public void MarkPathRestoredForAnyType_BlocksEveryType()
    {
        // Manifest-only restores (Files-tab delete → Restore-by-BinPath) don't know the IssueType.
        // The wildcard blocks every scanner from auto-fixing the file.
        _db.MarkPathRestoredForAnyType("/lib/orphan.srt");
        Assert.True(_db.WasPathRestored("/lib/orphan.srt", IssueType.Duplicate));
        Assert.True(_db.WasPathRestored("/lib/orphan.srt", IssueType.SubtitleLanguage));
        Assert.True(_db.WasPathRestored("/lib/orphan.srt", IssueType.OrphanedDebris));
    }

    [Fact]
    public void RepeatedRestores_AreIdempotent()
    {
        _db.MarkPathRestored("/lib/movies/A.mkv", IssueType.Duplicate);
        _db.MarkPathRestored("/lib/movies/A.mkv", IssueType.Duplicate);
        _db.MarkPathRestored("/lib/movies/A.mkv", IssueType.Duplicate);

        var restored = _db.GetRestoredPathsBlockingAutoQueue(IssueType.Duplicate);
        Assert.Single(restored);
    }

    [Fact]
    public void GetRestoredPathsBlockingAutoQueue_ReturnsBothExactAndAnyType()
    {
        _db.MarkPathRestored("/lib/movies/A.mkv", IssueType.Duplicate);
        _db.MarkPathRestoredForAnyType("/lib/movies/B.mkv");
        _db.MarkPathRestored("/lib/movies/C.mkv", IssueType.SubtitleLanguage); // different type

        var restored = _db.GetRestoredPathsBlockingAutoQueue(IssueType.Duplicate);
        Assert.Contains("/lib/movies/A.mkv", restored);
        Assert.Contains("/lib/movies/B.mkv", restored);
        Assert.DoesNotContain("/lib/movies/C.mkv", restored);
    }

    [Fact]
    public void QueueDetectedIssues_SkipsRestoredPaths()
    {
        // Seed: two Detected issues, one at a restored path.
        var issueA = new Issue
        {
            Type = IssueType.Duplicate,
            ItemId = Guid.NewGuid(),
            Path = "/lib/movies/keep.mkv",
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow,
        };
        var issueB = new Issue
        {
            Type = IssueType.Duplicate,
            ItemId = Guid.NewGuid(),
            Path = "/lib/movies/queue.mkv",
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow,
        };
        _db.ReplaceDetectedIssues(IssueType.Duplicate, [issueA, issueB]);
        _db.MarkPathRestored("/lib/movies/keep.mkv", IssueType.Duplicate);

        var queued = _db.QueueDetectedIssues(IssueType.Duplicate);
        Assert.Equal(1, queued); // only the unrestored one gets queued

        var stillDetected = _db.GetIssues(IssueType.Duplicate, IssueStatus.Detected);
        Assert.Contains(stillDetected, i => i.Path == "/lib/movies/keep.mkv");

        var nowQueued = _db.GetIssues(IssueType.Duplicate, IssueStatus.Queued);
        Assert.Contains(nowQueued, i => i.Path == "/lib/movies/queue.mkv");
    }

    [Fact]
    public void QueueDetectedIssues_HonorsRestoredPathsForAnyType()
    {
        var issue = new Issue
        {
            Type = IssueType.OrphanedDebris,
            ItemId = Guid.NewGuid(),
            Path = "/lib/orphan.srt",
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow,
        };
        _db.ReplaceDetectedIssues(IssueType.OrphanedDebris, [issue]);
        _db.MarkPathRestoredForAnyType("/lib/orphan.srt");

        var queued = _db.QueueDetectedIssues(IssueType.OrphanedDebris);
        Assert.Equal(0, queued);
    }

    [Fact]
    public void ManualApproval_StillWorks_EvenForRestoredPaths()
    {
        // Restore blocks AUTO-queue. It must not block MANUAL approval — the user is expressing a
        // new decision. UpdateIssueStatus is what the Approve button calls; verify it succeeds.
        var issue = new Issue
        {
            Type = IssueType.Duplicate,
            ItemId = Guid.NewGuid(),
            Path = "/lib/movies/A.mkv",
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow,
        };
        _db.ReplaceDetectedIssues(IssueType.Duplicate, [issue]);
        _db.MarkPathRestored("/lib/movies/A.mkv", IssueType.Duplicate);

        var id = _db.GetIssues(IssueType.Duplicate, IssueStatus.Detected)[0].Id;
        Assert.True(_db.UpdateIssueStatus(id, IssueStatus.Queued));
        Assert.Equal(IssueStatus.Queued, _db.GetIssueStatus(id));
    }

    [Fact]
    public void EmptyOrWhitespacePath_IsIgnoredSilently()
    {
        // Defence against a corrupt HistoryEntry.Path — don't blow up, just don't record.
        _db.MarkPathRestored(string.Empty, IssueType.Duplicate);
        _db.MarkPathRestored("   ", IssueType.Duplicate);
        _db.MarkPathRestoredForAnyType(string.Empty);
        Assert.Empty(_db.GetRestoredPathsBlockingAutoQueue(IssueType.Duplicate));
    }

    [Fact]
    public void SchemaMigration_RunsIdempotently()
    {
        // Reopen the DB (simulates a restart). MigrateSchema must not throw and the restored_paths
        // table must still be readable. Guards against a broken v4->v5 migration.
        _db.MarkPathRestored("/lib/movies/A.mkv", IssueType.Duplicate);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var reopened = new MediaDashDb(_dbPath);
        Assert.True(reopened.WasPathRestored("/lib/movies/A.mkv", IssueType.Duplicate));
    }
}

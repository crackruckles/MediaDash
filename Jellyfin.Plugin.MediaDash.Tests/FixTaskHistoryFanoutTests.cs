using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Fan-out invariants for the Recycle Bin tab's Restore button matrix. Every recycled file must
/// end up with its own HistoryEntry (primary via FixResult.RecyclePath, sidecars via
/// FixResult.AdditionalRecycled), because the Recycle Bin tab joins bin files to history rows via
/// RecyclePath and renders "no history" for anything unjoined. These tests pin both the DB
/// contract (many rows per issue) and the source shape of FixTask.ExecuteAsync's write loop.
/// </summary>
public sealed class FixTaskHistoryFanoutTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-fanout-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    public FixTaskHistoryFanoutTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void MultipleHistoryRowsPerIssueEachCarryDistinctRecyclePaths()
    {
        // Simulates a TrackFixer run: one main video + three external subtitle sidecars.
        var issueId = 42L;
        _db.AddHistory(NewEntry(issueId, "/lib/tv/Show S01E01.mkv", "/bin/20260827-000000-000-x/Show S01E01.mkv", "Removed 2 unwanted audio tracks."));
        _db.AddHistory(NewEntry(issueId, "/lib/tv/Show S01E01.en.srt", "/bin/20260827-000000-000-x/Show S01E01.en.srt", "Recycled external subtitle sidecar during track fix."));
        _db.AddHistory(NewEntry(issueId, "/lib/tv/Show S01E01.fr.srt", "/bin/20260827-000000-000-y/Show S01E01.fr.srt", "Recycled external subtitle sidecar during track fix."));
        _db.AddHistory(NewEntry(issueId, "/lib/tv/Show S01E01.de.srt", "/bin/20260827-000000-000-z/Show S01E01.de.srt", "Recycled external subtitle sidecar during track fix."));

        var rows = _db.GetHistory()
            .Where(h => h.IssueId == issueId)
            .ToList();

        Assert.Equal(4, rows.Count);
        Assert.Equal(4, rows.Select(r => r.RecyclePath).Distinct().Count());
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.RecyclePath)));
    }

    [Fact]
    public void RestoringOneSidecarLeavesOthersRestorableIndependently()
    {
        // The user restores the .en.srt only. The .mkv + other sidecars must remain restorable —
        // that's the whole point of one-row-per-file; a shared Restored flag would poison siblings.
        var issueId = 100L;
        _db.AddHistory(NewEntry(issueId, "/lib/tv/E.mkv", "/bin/1/E.mkv", "primary"));
        _db.AddHistory(NewEntry(issueId, "/lib/tv/E.en.srt", "/bin/2/E.en.srt", "en sidecar"));
        _db.AddHistory(NewEntry(issueId, "/lib/tv/E.fr.srt", "/bin/3/E.fr.srt", "fr sidecar"));

        var enRow = _db.GetHistory().Single(h => h.Path.EndsWith(".en.srt", StringComparison.Ordinal));
        _db.MarkRestored(enRow.Id);

        var stillRestorable = _db.GetHistory()
            .Where(h => h.IssueId == issueId && !h.Restored)
            .Select(h => h.Path)
            .ToHashSet();
        Assert.Contains("/lib/tv/E.mkv", stillRestorable);
        Assert.Contains("/lib/tv/E.fr.srt", stillRestorable);
        Assert.DoesNotContain("/lib/tv/E.en.srt", stillRestorable);
    }

    [Fact]
    public void FixTaskSourceStillContainsTheAdditionalRecycledFanoutBlock()
    {
        // Grep-style guard: assert that FixTask.ExecuteAsync still writes one HistoryEntry per
        // AdditionalRecycled entry. A future refactor that drops the loop would silently
        // regress every multi-file fix (TrackFixer external subs, EmbeddedCoverArtFixer strips).
        var fixTaskPath = Path.Combine(RepoRoot(), "Jellyfin.Plugin.MediaDash", "ScheduledTasks", "FixTask.cs");
        var src = File.ReadAllText(fixTaskPath);

        Assert.Contains("result.AdditionalRecycled", src);
        Assert.Contains("foreach (var extra in result.AdditionalRecycled)", src);
        // The AddHistory inside the loop must reference the sidecar's own RecyclePath, not the
        // primary result's RecyclePath — verifies the pattern hasn't accidentally been re-pointed
        // at the wrong field.
        Assert.Contains("RecyclePath = extra.RecyclePath", src);
    }

    private static HistoryEntry NewEntry(long issueId, string path, string binPath, string action) => new()
    {
        IssueId = issueId,
        Type = IssueType.SubtitleLanguage,
        Path = path,
        Action = action,
        BytesFreed = 0,
        RecyclePath = binPath,
        FixedAtUtc = DateTime.UtcNow,
        WasDryRun = false,
        Success = true
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.MediaDash.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// An open issue at a path and type that was already marked Fixed must be reported as having
/// come back, so the user learns something keeps recreating it (or the fix changed nothing).
/// </summary>
[Collection("MediaDashDb")]
public sealed class RecurringIssueTests : IDisposable
{
    private const string Leftover = "/owned/TV/Show.S01E01.720p-GRP";
    private readonly string _path = Path.Combine(Path.GetTempPath(), "mediadash-recurring-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;
    private readonly DateTime _start = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    public RecurringIssueTests()
    {
        _db = new MediaDashDb(_path);
    }

    [Fact]
    public void IssueFoundAgainAfterAFixThatChangedNothingIsFlagged()
    {
        // The reported case: the fixer declined (NothingToRemove), the issue was closed as Fixed,
        // and the next scan found the same leftover folder.
        var first = AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Fixed, _start);
        AddHistory(first, IssueType.OrphanedDebris, Leftover, _start.AddMinutes(5), "Folder was left in place.", success: false);
        var reopened = AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Detected, _start.AddHours(1));

        var recurrence = Assert.Single(_db.Issues.GetRecurrences()).Value;

        Assert.Equal(reopened, recurrence.IssueId);
        Assert.Equal(IssueType.OrphanedDebris, recurrence.Type);
        Assert.Equal(Leftover, recurrence.Path);
        Assert.Equal(1, recurrence.TimesFixed);
        Assert.Equal("Folder was left in place.", recurrence.LastFixMessage);
        Assert.False(recurrence.LastFixSucceeded);
        Assert.Equal(_start.AddMinutes(5), recurrence.LastFixedAtUtc);
    }

    [Fact]
    public void RepeatedFixesAreCountedOncePerFixedIssueWithTheNewestFixReported()
    {
        var a = AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Fixed, _start);
        AddHistory(a, IssueType.OrphanedDebris, Leftover, _start.AddMinutes(1), "retry failed", success: false);
        AddHistory(a, IssueType.OrphanedDebris, Leftover, _start.AddMinutes(2), "recycled");
        var b = AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Fixed, _start.AddDays(1));
        AddHistory(b, IssueType.OrphanedDebris, Leftover, _start.AddDays(1).AddMinutes(1), "recycled again");
        var c = AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Fixed, _start.AddDays(2));
        AddHistory(c, IssueType.OrphanedDebris, Leftover, _start.AddDays(2).AddMinutes(1), "recycled a third time");
        AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Queued, _start.AddDays(3));

        var recurrence = Assert.Single(_db.Issues.GetRecurrences()).Value;

        Assert.Equal(3, recurrence.TimesFixed);
        Assert.Equal("recycled a third time", recurrence.LastFixMessage);
        Assert.True(recurrence.LastFixSucceeded);
        Assert.Equal(_start.AddMinutes(1), recurrence.FirstFixedAtUtc);
        Assert.Equal(_start.AddDays(2).AddMinutes(1), recurrence.LastFixedAtUtc);
    }

    [Fact]
    public void ScanCycleThroughTheStoreFlagsTheReDetectedRow()
    {
        _db.ReplaceDetectedIssues(IssueType.OrphanedDebris, new[] { NewIssue(_start) });
        var fixedId = Assert.Single(_db.GetIssues(IssueType.OrphanedDebris, IssueStatus.Detected)).Id;
        SetStatus(fixedId, IssueStatus.Fixed);
        AddHistory(fixedId, IssueType.OrphanedDebris, Leftover, _start.AddMinutes(1), "Folder was left in place.", success: false);

        _db.ReplaceDetectedIssues(IssueType.OrphanedDebris, new[] { NewIssue(_start.AddHours(1)) });

        var reopened = Assert.Single(_db.GetIssues(IssueType.OrphanedDebris, IssueStatus.Detected));
        Assert.NotEqual(fixedId, reopened.Id);
        Assert.Equal(reopened.Id, Assert.Single(_db.Issues.GetRecurrences()).Key);
    }

    [Fact]
    public void UnrelatedEarlierRowsDoNotFlagAnIssue()
    {
        // Same path, different type: a new problem, not the old one back.
        var otherType = AddIssue(IssueType.Quality, Leftover, IssueStatus.Fixed, _start);
        AddHistory(otherType, IssueType.Quality, Leftover, _start.AddMinutes(1), "re-encoded");
        // Same type, different path.
        var otherPath = AddIssue(IssueType.OrphanedDebris, Leftover + "-other", IssueStatus.Fixed, _start);
        AddHistory(otherPath, IssueType.OrphanedDebris, Leftover + "-other", _start.AddMinutes(1), "recycled");
        // A dry run changes nothing, so it is not a fix that could have been undone.
        var dryRun = AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Fixed, _start);
        AddHistory(dryRun, IssueType.OrphanedDebris, Leftover, _start.AddMinutes(1), "preview", dryRun: true);
        // The user restored this fix, so its return is expected (the restored badge covers it).
        var restored = AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Fixed, _start);
        _db.MarkRestored(AddHistory(restored, IssueType.OrphanedDebris, Leftover, _start.AddMinutes(1), "recycled"));
        // Ignored, never fixed.
        var dismissed = AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Dismissed, _start);
        AddHistory(dismissed, IssueType.OrphanedDebris, Leftover, _start.AddMinutes(1), "ignored");
        // Fixed after the open row was detected: it cannot be what the open row came back from.
        var later = AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Fixed, _start);
        AddHistory(later, IssueType.OrphanedDebris, Leftover, _start.AddDays(5), "recycled later");

        AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Detected, _start.AddDays(1));

        Assert.Empty(_db.Issues.GetRecurrences());
    }

    [Fact]
    public void ClosedRowsAreNeverReportedAsRecurring()
    {
        var a = AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Fixed, _start);
        AddHistory(a, IssueType.OrphanedDebris, Leftover, _start.AddMinutes(1), "recycled");
        var b = AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Fixed, _start.AddDays(1));
        AddHistory(b, IssueType.OrphanedDebris, Leftover, _start.AddDays(1).AddMinutes(1), "recycled again");
        AddIssue(IssueType.OrphanedDebris, Leftover, IssueStatus.Dismissed, _start.AddDays(2));

        Assert.Empty(_db.Issues.GetRecurrences());
    }

    [Fact]
    public void ApiRowCarriesTheTypeName()
    {
        var row = RecurringIssue.From(new IssueRecurrence { IssueId = 7, Type = IssueType.OrphanedDebris, Path = Leftover, TimesFixed = 2 });
        Assert.Equal("OrphanedDebris", row.Type);
        Assert.Equal(2, row.TimesFixed);
        Assert.Equal(Leftover, row.Path);
    }

    [Fact]
    public void CancelledLookupThrows()
    {
        Assert.Throws<OperationCanceledException>(() => _db.Issues.GetRecurrences(new CancellationToken(canceled: true)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static Issue NewIssue(DateTime detected) => new()
    {
        Type = IssueType.OrphanedDebris,
        Path = Leftover,
        Status = IssueStatus.Detected,
        DetectedAtUtc = detected,
        DetailsJson = "{\"kind\":\"GroupedLeftover\"}",
        SuggestedFix = "Recycle leftover series artwork."
    };

    private long AddIssue(IssueType type, string path, IssueStatus status, DateTime detected)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO issues (type, item_id, path, status, detected_at_utc) VALUES (@type, @item, @path, @status, @at); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("@type", (int)type);
        command.Parameters.AddWithValue("@item", Guid.Empty.ToString("N"));
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@status", (int)status);
        command.Parameters.AddWithValue("@at", detected.Ticks);
        return (long)command.ExecuteScalar()!;
    }

    private void SetStatus(long id, IssueStatus status)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE issues SET status = @status WHERE id = @id";
        command.Parameters.AddWithValue("@status", (int)status);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    private long AddHistory(long issueId, IssueType type, string path, DateTime when, string action, bool dryRun = false, bool success = true)
    {
        _db.History.AddHistory(new HistoryEntry
        {
            IssueId = issueId, Type = type, Path = path, Action = action, FixedAtUtc = when, WasDryRun = dryRun, Success = success
        });
        return _db.GetHistory(1000).Where(h => h.IssueId == issueId).Max(h => h.Id);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();
        return connection;
    }
}

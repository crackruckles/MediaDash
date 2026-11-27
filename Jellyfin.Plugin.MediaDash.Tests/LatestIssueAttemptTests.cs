using System;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("MediaDashDb")]
public sealed class LatestIssueAttemptTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "mediadash-attempts-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;
    private readonly ITestOutputHelper _output;

    public LatestIssueAttemptTests(ITestOutputHelper output)
    {
        _output = output;
        _db = new MediaDashDb(_path);
    }

    [Theory]
    [InlineData(80000, 1000)]
    [InlineData(1000000, 2000)]
    public void Issue80_LoadedHistoryReturnsOneNewestAttemptPerIssueWithoutStalling(int rowCount, int issueCount)
    {
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x + 1 FROM n WHERE x < @rows)
                INSERT INTO history(issue_id, type, path, action, bytes_freed, fixed_at_utc, dry_run, success)
                SELECT (x - 1) % @issues + 1, 0, '/owned/movie.mkv', 'attempt ' || x, 0, @when, 0, 0 FROM n
                """;
            command.Parameters.AddWithValue("@rows", rowCount);
            command.Parameters.AddWithValue("@issues", issueCount);
            command.Parameters.AddWithValue("@when", DateTime.UtcNow.Ticks);
            command.ExecuteNonQuery();
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var attempts = _db.History.GetLatestAttemptsByIssueIds(Enumerable.Range(1, issueCount).Select(i => (long)i).ToArray());
        watch.Stop();
        _output.WriteLine($"History rows={rowCount}, requested issues={issueCount}, lookup={watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(issueCount, attempts.Count);
        Assert.Equal("attempt " + (rowCount - issueCount + 1), attempts[1].Action);
        Assert.Equal("attempt " + rowCount, attempts[issueCount].Action);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "#80 history lookup exceeded the generous 10-second regression budget.");
    }

    [Fact]
    public void EqualTimestampsUseTheNewestHistoryIdAndIgnoreDryRuns()
    {
        var when = DateTime.UtcNow;
        Add(42, when, "older attempt");
        Add(42, when, "newer attempt");
        Add(42, when.AddMinutes(1), "dry run", dryRun: true);
        var result = _db.History.GetLatestAttemptsByIssueIds(new long[] { 42, 42, -1, 0, 99 });
        Assert.Equal("newer attempt", Assert.Single(result).Value.Action);
    }

    [Fact]
    public void LookupCrossesChunkBoundariesAndReturnsOnlyRequestedIds()
    {
        var when = DateTime.UtcNow;
        for (var id = 1; id <= 1001; id++)
        {
            Add(id, when, "failed", success: false);
        }

        var result = _db.History.GetLatestAttemptsByIssueIds(Enumerable.Range(1, 1001).Select(id => (long)id).ToArray());
        Assert.Equal(1001, result.Count);
        Assert.All(result.Values, attempt => Assert.False(attempt.Success));
        Assert.Equal(1001, result[1001].IssueId);
    }

    [Fact]
    public void AbortedIssuesRequestDoesNotReadTheDatabaseOrCreateAResponse()
    {
        var state = Plugin.AttachedRunState;
        try
        {
            var controller = new MediaDashController(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new RunState(), null!, null!, null!);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = new CancellationToken(canceled: true) } };
            Assert.Throws<OperationCanceledException>(() => controller.GetIssues(openOnly: true));
        }
        finally
        {
            Plugin.AttachedRunState = state;
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void StartupEnsuresHistoryLookupIndexWithoutChangingHistory(int version)
    {
        Add(42, DateTime.UtcNow, "retained attempt");
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP INDEX idx_history_issue_id_dry_run; PRAGMA user_version = " + version;
            command.ExecuteNonQuery();
            MediaDashSchema.CreateTables(connection);
            MediaDashSchema.Migrate(connection);
            command.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'idx_history_issue_id_dry_run'";
            Assert.Contains("issue_id, dry_run, fixed_at_utc", (string)command.ExecuteScalar()!);
        }

        Assert.Equal("retained attempt", _db.History.GetLatestAttemptsByIssueIds(new long[] { 42 })[42].Action);
    }

    [Fact]
    public void CancelledHistoryLookupDoesNotRunOrReturnPartialResults()
    {
        Assert.Throws<OperationCanceledException>(() => _db.History.GetLatestAttemptsByIssueIds(new long[] { 42 }, new CancellationToken(canceled: true)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();
        return connection;
    }

    private void Add(long issueId, DateTime when, string action, bool dryRun = false, bool success = true) => _db.History.AddHistory(new HistoryEntry
    {
        IssueId = issueId, Type = IssueType.AudioLanguage, Path = "/owned/movie.mkv", Action = action, FixedAtUtc = when, WasDryRun = dryRun, Success = success
    });
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Bug-pattern audit 2026-09-19: the `POST /Issues/ApproveAll` and `POST /Issues/Bulk` endpoints
/// were routing straight to <see cref="MediaDashDb.QueueDetectedIssues"/> /
/// <see cref="MediaDashDb.BulkUpdateOpenIssueStatus"/> without the consent rollback FixTask uses
/// after its auto-queue pass. Blocking-warning rows (bitmap-sub removal, low-space overrides,
/// etc.) require an explicit per-row Approve from a user who has expanded the warning bubble; a
/// bulk "Approve all shown" click doesn't guarantee they saw it. These tests pin the DB-layer
/// contract the endpoints now rely on: bulk-approve returns the exact just-transitioned IDs so
/// the rollback can revert only those, and manually-approved blocking rows sitting in Queued
/// stay put.
/// </summary>
[Collection("MediaDashDb")]
public sealed class BulkApproveConsentTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-bulk-consent-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    private const string BlockingDetails = "{\"warnings\":[{\"code\":\"bitmap-subs-dropped\",\"blocking\":true,\"message\":\"drops 1 sub\"}]}";
    private static readonly IssueStatus[] FromDetected = [IssueStatus.Detected];

    public BulkApproveConsentTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void BulkApprove_OnlyReturnsDetectedToQueuedTransitions()
    {
        // A stale client snapshot can include IDs that are already Queued (someone manually
        // approved them 10 min ago). The endpoint's Approve action must not confuse those with
        // rows freshly transitioned by THIS request — otherwise the consent-rollback pass would
        // revert a legitimate prior manual approval. Fixed by tightening the source-status
        // filter to `status = @detected` on the Queued target and returning RETURNING id.
        var ids = InsertIssues(
            ("/lib/fresh.mkv", string.Empty),
            ("/lib/already-queued.mkv", string.Empty));
        var freshId = ids["/lib/fresh.mkv"];
        var alreadyQueuedId = ids["/lib/already-queued.mkv"];
        _db.TryTransitionIssueStatus(alreadyQueuedId, FromDetected, IssueStatus.Queued);

        var transitioned = _db.BulkUpdateOpenIssueStatus(new List<long> { freshId, alreadyQueuedId }, IssueStatus.Queued);

        Assert.Equal(new[] { freshId }, transitioned.ToArray());
        Assert.Equal(IssueStatus.Queued, _db.GetIssueStatus(freshId));
        Assert.Equal(IssueStatus.Queued, _db.GetIssueStatus(alreadyQueuedId));
    }

    [Fact]
    public void AutomaticQueueRollsBackReportOnlyRowsWithoutBlockingSupportedRows()
    {
        var ids = InsertIssues(("/lib/report-only.mkv", "{\"fixerAvailable\":false}"), ("/lib/supported.mkv", "{}"));
        var queued = _db.QueueDetectedIssues(IssueType.AudioLanguage);
        Assert.Equal(1, _db.RollbackAutoQueuedBlockingWarnings(queued));
        Assert.Equal(IssueStatus.Detected, _db.GetIssueStatus(ids["/lib/report-only.mkv"]));
        Assert.Equal(IssueStatus.Queued, _db.GetIssueStatus(ids["/lib/supported.mkv"]));
    }

    [Fact]
    public void BulkDismiss_StillTouchesBothDetectedAndQueuedRows()
    {
        // The tightened source filter is Approve-specific. Dismiss must still catch already-queued
        // rows so a user can cancel a fix they'd previously approved.
        var ids = InsertIssues(
            ("/lib/detected.mkv", string.Empty),
            ("/lib/queued.mkv", string.Empty));
        var detectedId = ids["/lib/detected.mkv"];
        var queuedId = ids["/lib/queued.mkv"];
        _db.TryTransitionIssueStatus(queuedId, FromDetected, IssueStatus.Queued);

        var transitioned = _db.BulkUpdateOpenIssueStatus(new List<long> { detectedId, queuedId }, IssueStatus.Dismissed);

        Assert.Equal(2, transitioned.Count);
        Assert.Equal(IssueStatus.Dismissed, _db.GetIssueStatus(detectedId));
        Assert.Equal(IssueStatus.Dismissed, _db.GetIssueStatus(queuedId));
    }

    [Fact]
    public void BulkApprove_PlusRollback_LeavesManuallyApprovedBlockingRowsAlone()
    {
        // Full end-to-end shape of the fix: user manually approved a blocking-warning row
        // yesterday (it's in Queued). Today they hit "Approve all shown" on a filtered view
        // that happens to include both a fresh blocking row and yesterday's row. The bulk
        // op must promote the fresh Detected row, and the rollback must revert only that
        // one — leaving yesterday's explicit consent intact.
        var ids = InsertIssues(
            ("/lib/yesterday.mkv", BlockingDetails),
            ("/lib/fresh-blocking.mkv", BlockingDetails),
            ("/lib/fresh-benign.mkv", string.Empty));
        var yesterdayId = ids["/lib/yesterday.mkv"];
        var freshBlockingId = ids["/lib/fresh-blocking.mkv"];
        var freshBenignId = ids["/lib/fresh-benign.mkv"];
        _db.TryTransitionIssueStatus(yesterdayId, FromDetected, IssueStatus.Queued);

        var transitioned = _db.BulkUpdateOpenIssueStatus(
            new List<long> { yesterdayId, freshBlockingId, freshBenignId },
            IssueStatus.Queued);
        var reverted = _db.RollbackAutoQueuedBlockingWarnings(transitioned);

        Assert.Equal(2, transitioned.Count); // fresh-blocking + fresh-benign, NOT yesterday
        Assert.Equal(1, reverted);           // only fresh-blocking bounces back
        Assert.Equal(IssueStatus.Queued, _db.GetIssueStatus(yesterdayId));       // untouched
        Assert.Equal(IssueStatus.Detected, _db.GetIssueStatus(freshBlockingId)); // consent gate held
        Assert.Equal(IssueStatus.Queued, _db.GetIssueStatus(freshBenignId));     // runs normally
    }

    private Dictionary<string, long> InsertIssues(params (string path, string detailsJson)[] rows)
    {
        var issues = new List<Issue>(rows.Length);
        foreach (var (path, detailsJson) in rows)
        {
            issues.Add(new Issue
            {
                Type = IssueType.AudioLanguage,
                ItemId = Guid.NewGuid(),
                Path = path,
                DetailsJson = detailsJson,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow
            });
        }

        _db.ReplaceDetectedIssues(IssueType.AudioLanguage, issues);

        var pathToId = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in _db.GetIssues(IssueType.AudioLanguage, IssueStatus.Detected))
        {
            pathToId[row.Path] = row.Id;
        }

        foreach (var (path, _) in rows)
        {
            Assert.True(pathToId.ContainsKey(path), "Inserted issue not found: " + path);
        }

        return pathToId;
    }
}

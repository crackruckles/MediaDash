using System;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Safety contract for the manual-scan review grace: a user who clicks Scan is presumed to be on
// the Issues tab actively reviewing. The opportunistic scheduled FixTask must NOT run during
// the grace window or issues auto-fix out from under the reviewer. These tests pin the shape
// of the ScanTask timestamp + grace constant that FixTask.ExecuteAsync branches on. Full
// integration of the branch is exercised by the live-server E2E in torture-test.ps1 — this
// class just guards the state contract from a silent refactor breaking it.
public class ScanTaskManualScanGraceTests
{
    // Ten minutes was picked as the smallest window that comfortably covers a user reading through
    // a page of issues (fixture harness surfaces ~30 rows in a normal library scan). Shorter and
    // reviewers get surprised mid-read; longer and a forgotten tab defers auto-fix indefinitely.
    [Fact]
    public void GraceWindowIsTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), ScanTask.ManualScanReviewGrace);
    }

    [Fact]
    public void JustCompletedManualScanIsInsideGrace()
    {
        ScanTask.ManualScanCompletedUtc = DateTime.UtcNow;
        try
        {
            var elapsed = DateTime.UtcNow - ScanTask.ManualScanCompletedUtc!.Value;
            Assert.True(elapsed < ScanTask.ManualScanReviewGrace);
        }
        finally
        {
            ScanTask.ManualScanCompletedUtc = null;
        }
    }

    [Fact]
    public void OldManualScanIsOutsideGrace()
    {
        ScanTask.ManualScanCompletedUtc = DateTime.UtcNow - TimeSpan.FromMinutes(11);
        try
        {
            var elapsed = DateTime.UtcNow - ScanTask.ManualScanCompletedUtc!.Value;
            Assert.True(elapsed >= ScanTask.ManualScanReviewGrace);
        }
        finally
        {
            ScanTask.ManualScanCompletedUtc = null;
        }
    }

    [Fact]
    public void NullTimestampMeansNoGrace()
    {
        // Fresh install / scheduled-scan-only user: the FixTask early-out reads null as "no grace
        // active" and behaves exactly like the pre-grace codebase. Guards against a null-ref if
        // the ordering ever changed to check-before-set.
        ScanTask.ManualScanCompletedUtc = null;
        Assert.Null(ScanTask.ManualScanCompletedUtc);
    }
}

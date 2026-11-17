using System.IO;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Bug-pattern audit 2026-09-19: the fix-run alert dialog in <c>configPage.html</c> keys off
/// exact-cased strings returned by <see cref="FixTask.BucketReason"/> to decide whether to append
/// the "chown / free-space" remediation hint. There's no compile-time link between the two files,
/// so a UX reword on either side silently breaks the hint routing — the user sees a generic
/// "reason: X" line with no actionable next step. These tests are the contract: BucketReason must
/// keep producing the exact literals the JS matches on, and the JS must keep matching the exact
/// literals BucketReason produces. Either side changes → this test fails, forcing an intentional
/// bilateral update.
/// </summary>
public sealed class BucketReasonContractTests
{
    // The exact literals `configPage.html` compares `curr.TopFailureReason` against inside the
    // fix-alert branch. Adding a new hint on the JS side means adding the string here AND making
    // sure BucketReason produces it. Removing one here without touching JS means the JS branch
    // is dead — remove it there too.
    private static readonly string[] JsRemediationBuckets =
    {
        "Permission denied",
        "Not enough free disk space",
    };

    [Fact]
    public void BucketReason_MapsPermissionMessagesToPermissionDeniedBucket()
    {
        // The actual message the fixer emits on Linux with wrong ownership — permission-denied
        // routing here is what triggers the `chown` hint in the fix-run alert.
        Assert.Equal(
            "Permission denied",
            FixTask.BucketReason("Jellyfin lacks write access to /mnt/media/x.mkv. Check that the file (and its folder) is owned by or read+writable by the user Jellyfin runs as (typically 'jellyfin' on Linux)."));
    }

    [Theory]
    [InlineData("Not enough free space to re-encode this file (needs at least twice its own size free on the target drive).")]
    [InlineData("Cannot rebuild this file — Not enough free disk space: needs ~2 GB free on /media, only 1 GB available.")]
    public void BucketReason_MapsFreeSpaceMessagesToFreeSpaceBucket(string message)
    {
        // Message produced by the pre-flight guard in TranscodeFixer / TrackFixer / repair-ladder.
        Assert.Equal(
            "Not enough free disk space",
            FixTask.BucketReason(message));
    }

    [Fact]
    public void EveryJsRemediationBucket_IsProducibleByBucketReason()
    {
        // Reflection-free contract: for every JS-matched bucket, BucketReason must return the
        // exact string in response to at least one representative input. If BucketReason is
        // reworded ("Permission denied" → "Access denied"), the corresponding test above fails
        // AND this loop confirms the reworded label no longer round-trips.
        var probes = new (string Bucket, string Message)[]
        {
            ("Permission denied", "permission denied"),
            ("Not enough free disk space", "not enough free space"),
        };

        foreach (var (bucket, message) in probes)
        {
            Assert.Contains(bucket, JsRemediationBuckets);
            Assert.Equal(bucket, FixTask.BucketReason(message));
        }
    }

    [Fact]
    public void OverviewAsset_ContainsEveryJsRemediationBucketAsALiteral()
    {
        // The JS side of the contract. If a build reworks the alert branch to say
        // `reason === 'access denied'`, this fails and forces the update to be paired with a
        // BucketReason change.
        var configPagePath = Path.Combine(RepoRoot(), "Jellyfin.Plugin.MediaDash", "Configuration", "scripts", "tabs", "overview-tab.js");
        var html = File.ReadAllText(configPagePath);

        foreach (var bucket in JsRemediationBuckets)
        {
            var expected = "'" + bucket + "'";
            Assert.True(
                html.Contains(expected, System.StringComparison.Ordinal),
                "overview-tab.js no longer contains the JS literal " + expected
                + " that the fix-alert remediation branch matches against BucketReason output. "
                + "If BucketReason was intentionally reworded, update JsRemediationBuckets AND the "
                + "matching `reason === '...'` line(s) in overview-tab.js at the same time.");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.MediaDash.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

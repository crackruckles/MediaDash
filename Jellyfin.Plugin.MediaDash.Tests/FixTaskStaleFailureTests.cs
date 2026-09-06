using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class FixTaskStaleFailureTests
{
    [Theory]
    [InlineData("The file no longer exists; re-scan to refresh the list.")]
    [InlineData("The source no longer exists; re-scan to refresh the list.")]
    [InlineData("Nothing to remove any more — the file may have changed since the scan. Re-scan to refresh.")]
    [InlineData("The file plays fine now — nothing was removed. Re-scan to clear this issue.")]
    [InlineData("An item with the same name already exists at '/mnt/media/Complete/TV/Yellowstone/Yellowstone (2018)' — rename or remove it, or move this one manually.")]
    [InlineData("A file with the same name already exists at '/mnt/media/target.mkv' — nothing was moved. Rename or remove the existing file, or move this one manually.")]
    public void IsStaleFailure_KnownStalePatterns_ReturnTrue(string message)
    {
        // These are the exact strings the fixers emit when scan-time state no longer matches disk.
        // Retrying is guaranteed to hit the same wall — auto-clear so the fix task stops looping every 15 min.
        Assert.True(FixTask.IsStaleFailure(message));
    }

    [Theory]
    [InlineData("Jellyfin can't write to '/mnt/media'. Grant read+write permission.")]
    [InlineData("Not enough free disk space to rebuild this file.")]
    [InlineData("Rebuilding the file failed; the original is untouched. Details: some ffmpeg error")]
    [InlineData("The re-encoded file failed verification; the original is untouched.")]
    [InlineData("")]
    public void IsStaleFailure_TransientOrRealFailures_ReturnFalse(string message)
    {
        // Permission, disk space, ffmpeg, verification — all recoverable on retry (user fixes permissions,
        // frees space, external tool releases file, etc.). Must NOT be treated as stale.
        Assert.False(FixTask.IsStaleFailure(message));
    }
}

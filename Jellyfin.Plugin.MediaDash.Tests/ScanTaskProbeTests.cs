using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class ScanTaskProbeTests
{
    [Fact]
    public async Task ProbeReachableAsync_ReachablePath_ReturnsTrueQuickly()
    {
        // Any temp dir is guaranteed to respond within microseconds; the probe should return true
        // well inside the 5-second window used by the real scan.
        var tempRoot = Path.Combine(Path.GetTempPath(), "mediadash-probe-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempRoot);
        try
        {
            var reached = await ScanTask.ProbeReachableAsync(tempRoot, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.True(reached);
        }
        finally
        {
            Directory.Delete(tempRoot);
        }
    }

    [Fact]
    public async Task ProbeReachableAsync_NonExistentPath_StillReturnsTrue()
    {
        // The probe only cares whether the syscall RETURNED in time. A path that doesn't exist
        // still returns quickly — the scanners handle "missing" themselves; the probe's job is to
        // catch hung mounts, not to gate on existence.
        var missing = Path.Combine(Path.GetTempPath(), "mediadash-does-not-exist-" + Guid.NewGuid());
        var reached = await ScanTask.ProbeReachableAsync(missing, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(reached);
    }

    [Fact]
    public async Task ProbeReachableAsync_ZeroTimeout_ReturnsFalse()
    {
        // Simulates a hung mount by starving the probe of any time to run. TimeSpan.Zero forces
        // Task.Delay to complete first, so Task.WhenAny picks the delay — exactly the branch that
        // fires when NFS hangs. If this ever returns true, the WhenAny ordering broke and hung
        // mounts would silently be accepted.
        var reached = await ScanTask.ProbeReachableAsync(Path.GetTempPath(), TimeSpan.Zero, CancellationToken.None);
        Assert.False(reached);
    }
}

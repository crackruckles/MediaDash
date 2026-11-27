using System;
using System.Diagnostics;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class UncVolumeProbeTests
{
    [Fact]
    public void HungSharesNeverBlockPollsOrStackMeasurementsAndWorkersAreBounded()
    {
        using var release = new ManualResetEventSlim(false);
        using var started = new CountdownEvent(2);
        var calls = 0;
        var probe = new UncVolumeProbe(_ =>
        {
            Interlocked.Increment(ref calls);
            started.Signal();
            release.Wait(TimeSpan.FromSeconds(10));
            return new(true, 50, 100);
        }, TimeSpan.FromSeconds(30), maxConcurrent: 2);
        try
        {
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
            {
                Assert.False(probe.TryGet(@"\\nas\share" + i, out _, out _));
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < 50; i++)
            {
                Assert.False(probe.TryGet(@"\\NAS\SHARE0", out _, out _));
            }

            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void CacheExpiresFailedRefreshKeepsStaleReadingAndRecoveryReplacesIt()
    {
        var now = DateTime.UtcNow;
        var calls = 0;
        var mode = 0;
        var probe = new UncVolumeProbe(_ =>
        {
            Interlocked.Increment(ref calls);
            return Volatile.Read(ref mode) switch
            {
                1 => throw new InvalidOperationException("offline"),
                2 => new(true, 70, 100),
                _ => new(true, 50, 100)
            };
        }, TimeSpan.FromSeconds(30), () => now);

        Assert.False(probe.TryGet(@"\\nas\a", out _, out _));
        Assert.True(SpinWait.SpinUntil(() => probe.TryGet(@"\\nas\a", out _, out _), 5000));
        Assert.True(probe.TryGet(@"\\NAS\A", out var first, out var stale));
        Assert.Equal(50, first.FreeBytes);
        Assert.False(stale);
        Assert.Equal(1, calls);

        Volatile.Write(ref mode, 1);
        now = now.AddSeconds(31);
        Assert.True(probe.TryGet(@"\\nas\a", out var cached, out stale));
        Assert.Equal(50, cached.FreeBytes);
        Assert.True(stale);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 2 && probe.ActiveMeasurements == 0, 5000));
        Assert.True(probe.TryGet(@"\\nas\a", out _, out stale));
        Assert.True(stale);
        Assert.Equal(2, calls); // failures are cached too

        Volatile.Write(ref mode, 2);
        now = now.AddSeconds(31);
        probe.TryGet(@"\\nas\a", out _, out _);
        Assert.True(SpinWait.SpinUntil(() => probe.TryGet(@"\\nas\a", out var r, out var s) && !s && r.FreeBytes == 70, 5000));
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, -1, 100)]
    [InlineData(true, 1, 0)]
    [InlineData(true, 101, 100)]
    public void InvalidOrUnavailableReadingsAreOmitted(bool ok, long free, long total)
    {
        var probe = new UncVolumeProbe(_ => new(ok, free, total), TimeSpan.FromSeconds(30));
        Assert.False(probe.TryGet(@"\\nas\a", out _, out _));
        Assert.True(SpinWait.SpinUntil(() => probe.ActiveMeasurements == 0, 5000));
        Assert.False(probe.TryGet(@"\\nas\a", out _, out _));
    }
}

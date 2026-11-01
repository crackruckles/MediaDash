using System;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// <see cref="UncVolumeProbe"/> keeps an offline or sluggish NAS from stalling the Overview: bounded
/// waits, a short reading cache, and at most one measurement in flight per share.
/// </summary>
public sealed class UncVolumeProbeTests
{
    private const string Share = @"\\sunnynas\Shows";
    private static readonly UncVolumeProbe.Reading Good = new(true, 500, 1000);

    [Fact]
    public void ReturnsAFreshReading()
    {
        var probe = new UncVolumeProbe(_ => Good, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

        Assert.True(probe.TryGet(Share, out var free, out var total));
        Assert.Equal(500, free);
        Assert.Equal(1000, total);
    }

    [Fact]
    public void ReadingIsCachedForTheTtl()
    {
        var calls = 0;
        var now = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc);
        var probe = new UncVolumeProbe(
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Good;
            },
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30),
            () => now);

        Assert.True(probe.TryGet(Share, out _, out _));
        now = now.AddSeconds(10);
        Assert.True(probe.TryGet(Share, out _, out _));
        Assert.Equal(1, calls);

        now = now.AddSeconds(25); // 35 s after the first reading: past the 30 s ttl
        Assert.True(probe.TryGet(Share, out _, out _));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ShareThatCannotBeMeasuredIsReportedUnavailable()
    {
        var probe = new UncVolumeProbe(_ => default, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

        Assert.False(probe.TryGet(Share, out var free, out var total));
        Assert.Equal(0, free);
        Assert.Equal(0, total);
    }

    [Fact]
    public void MeasureThatThrowsIsReportedUnavailableNotPropagated()
    {
        var probe = new UncVolumeProbe(
            _ => throw new InvalidOperationException("boom"),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30));

        Assert.False(probe.TryGet(Share, out _, out _));
    }

    [Fact]
    public void HungShareDoesNotBlockLongerThanTheTimeoutAndNeverStacksMeasurements()
    {
        using var release = new ManualResetEventSlim(false);
        var calls = 0;
        var timeout = TimeSpan.FromMilliseconds(300);
        var probe = new UncVolumeProbe(
            _ =>
            {
                Interlocked.Increment(ref calls);
                release.Wait(TimeSpan.FromSeconds(20));
                return Good;
            },
            timeout,
            TimeSpan.FromSeconds(30));

        try
        {
            // No last-good reading exists yet, so a hung share is left out.
            Assert.False(probe.TryGet(Share, out _, out _));

            // Past the timeout the share is treated as hung: later polls must return without waiting
            // again, and must not start a second measurement while the first is still stuck.
            Thread.Sleep(350);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(probe.TryGet(Share, out _, out _));
            Assert.False(probe.TryGet(Share, out _, out _));
            Assert.True(stopwatch.Elapsed < timeout);
            Assert.Equal(1, calls);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void SlowShareServesTheLastGoodReadingThenRecovers()
    {
        using var hang = new ManualResetEventSlim(false);
        var now = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc);
        var mode = "good";
        var probe = new UncVolumeProbe(
            _ =>
            {
                if (mode == "hang")
                {
                    hang.Wait(TimeSpan.FromSeconds(20));
                    return new UncVolumeProbe.Reading(true, 111, 1000);
                }

                return Good;
            },
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30),
            () => now);

        try
        {
            Assert.True(probe.TryGet(Share, out var free, out _));
            Assert.Equal(500, free);

            // Reading goes stale and the share stops answering: keep showing the last good numbers.
            now = now.AddSeconds(31);
            mode = "hang";
            Assert.True(probe.TryGet(Share, out free, out _));
            Assert.Equal(500, free);

            // Share wakes up; the stuck measurement completes and is picked up on the next poll.
            hang.Set();
            now = now.AddSeconds(1);
            var recovered = SpinWait.SpinUntil(
                () => probe.TryGet(Share, out var latest, out _) && latest == 111,
                TimeSpan.FromSeconds(5));
            Assert.True(recovered);
        }
        finally
        {
            hang.Set();
        }
    }

    [Fact]
    public void SharesAreTrackedIndependently()
    {
        var probe = new UncVolumeProbe(
            root => root.EndsWith("Shows", StringComparison.Ordinal) ? Good : default,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30));

        Assert.True(probe.TryGet(@"\\sunnynas\Shows", out _, out _));
        Assert.False(probe.TryGet(@"\\sunnynas\Offline", out _, out _));
    }
}

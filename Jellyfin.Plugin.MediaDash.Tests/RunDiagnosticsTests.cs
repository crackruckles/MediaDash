using System;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class RunDiagnosticsTests
{
    [Fact]
    public void HeartbeatKeepsTheOldestOperationAndIgnoresRepeatedProgress()
    {
        var time = new TestClock();
        using var run = new RunDiagnostics(NullLogger.Instance, "scan", "Scanner", time);
        run.Report(10);
        using var first = run.BeginOperation("directory enumeration");
        time.Advance(TimeSpan.FromSeconds(20));
        using (run.BeginOperation("media process"))
        {
            run.Report(10);
            time.Advance(TimeSpan.FromSeconds(10));
            var state = run.ReadState();
            Assert.Equal("directory enumeration", state.Operation);
            Assert.Equal(2, state.ActiveOperations);
            Assert.Equal(30, state.SecondsSinceProgress);
        }

        run.Report(11);
        Assert.Equal(0, run.ReadState().SecondsSinceProgress);
        Assert.Equal(1, run.ReadState().ActiveOperations);
    }

    [Fact]
    public void ThroughputRequiresTwoValidMonotonicSamples()
    {
        Assert.Equal(30, DiskIo.BytesPerSecond(10, 70, 2));
        Assert.Equal(0, DiskIo.BytesPerSecond(10, 10, 2));
        Assert.Null(DiskIo.BytesPerSecond(10, 9, 2));
        Assert.Null(DiskIo.BytesPerSecond(null, 10, 2));
        Assert.Null(DiskIo.BytesPerSecond(10, 20, 0));
    }

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        internal void Advance(TimeSpan span) => _ticks += span.Ticks;
    }
}

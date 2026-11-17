using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class DiskIoTests
{
    [Fact]
    public void LinuxDevicesIncludeNvmeVirtioAndSkipPartitionsAndVirtualMemoryDevices()
    {
        var devices = DiskIo.ParseLinuxDevices([
            "259 0 nvme0n1 1 0 12 0 1 0 34 0",
            "259 1 nvme0n1p1 1 0 500 0 1 0 600 0",
            "252 0 vda 1 0 10 0 1 0 20 0",
            "252 1 vda1 1 0 500 0 1 0 600 0",
            "7 0 loop0 1 0 500 0 1 0 600 0",
            "1 0 ram0 1 0 500 0 1 0 600 0"]);
        Assert.Equal(2, devices.Count);
        Assert.Equal((6144L, 17408L), devices["nvme0n1"]);
        Assert.Equal((5120L, 10240L), devices["vda"]);
    }

    [Fact]
    public void LinuxDevicesSkipStackedDeviceMapperAndRaidVolumes()
    {
        var devices = DiskIo.ParseLinuxDevices([
            "8 0 sda 1 0 12 0 1 0 34 0",
            "252 0 dm-0 1 0 500 0 1 0 600 0",
            "252 10 dm-10 1 0 500 0 1 0 600 0",
            "9 0 md0 1 0 500 0 1 0 600 0"]);
        Assert.Equal(["sda"], devices.Keys);
        Assert.Equal((6144L, 17408L), DiskIo.ParseLinuxCounters([
            "8 0 sda 1 0 12 0 1 0 34 0",
            "252 0 dm-0 1 0 12 0 1 0 34 0"]));
    }

    [Fact]
    public void LinuxCountersIncludeWholeDisksAndConvertSectorsToBytes()
    {
        var counters = DiskIo.ParseLinuxCounters([
            "8 1 sda1 1 0 100 0 1 0 200 0",
            "8 0 sda  1 0 12 0 1 0 34 0",
            "8 16 sdb 1 0 500 0 1 0 600 0"]);
        Assert.Equal((262144L, 324608L), counters);
        Assert.Equal((6144L, 17408L), DiskIo.ParseLinuxCounters(["8 0 sdb 1 0 12 0 1 0 34 0"]));
        Assert.Null(DiskIo.ParseLinuxCounters(["8 0 sda 1 0 invalid 0 1 0 34 0"]));
    }

    [Fact]
    public void LinuxSourceReadsTheExposedKernelCounters()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/diskstats"))
        {
            return;
        }

        var expected = DiskIo.ParseLinuxCounters(File.ReadLines("/proc/diskstats"));
        var actual = DiskIo.ReadCounters();
        Assert.Equal("whole disks", DiskIo.Source);
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (expected is { } baseline && actual is { } sample)
        {
            Assert.True(sample.Read >= baseline.Read);
            Assert.True(sample.Write >= baseline.Write);
        }
    }
}

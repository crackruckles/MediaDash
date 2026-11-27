using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Faster sampled playability checks: hardware-decode fallback noise is ignored, media processes
/// stay one at a time in Low system impact mode, and opted-in scanners evaluate two files at once
/// (results still in item order) only when Low system impact mode is off.
/// </summary>
[Collection("Diagnostics")]
public sealed class ScanConcurrencyTests : IDisposable
{
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-concurrency-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _config = new();
    private readonly MediaDashDb _db;

    public ScanConcurrencyTests()
    {
        Directory.CreateDirectory(_root);
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        var plugin = (Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void HardwareFallbackLinesAreNotDecodeDiagnostics()
    {
        var stderr = string.Join('\n',
            "[mpeg4 @ 0x5] [error] No support for codec mpeg4 profile 0.",
            "[mpeg4 @ 0x5] [error] Failed setup for format vaapi: hwaccel initialisation returned error.",
            "[h264 @ 0x6] [error] Failed setup for format vaapi: hwaccel initialisation returned error.",
            "[in#0/matroska,webm @ 0x7] [error] Element at 0x769e82 exceeds containing master element");

        Assert.Equal("[in#0/matroska,webm @ 0x7] [error] Element at 0x769e82 exceeds containing master element", FfprobeService.DecodeDiagnostics(stderr));
    }

    [Fact]
    public void NoServerConfigurationMeansSoftwareDecoding()
    {
        var service = new FfprobeService(null!, _db.Probes, NullLogger<FfprobeService>.Instance);
        Assert.Empty(service.ResolveSampledHardwareDecodeArgs());
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task MediaProcessesAreExclusiveOnlyInLowImpactMode(bool lowImpact, int expectedConcurrent)
    {
        _config.LowSystemImpactMode = lowImpact;
        var first = await MediaProcess.AcquireAsync(CancellationToken.None);
        var second = MediaProcess.AcquireAsync(CancellationToken.None);
        var completed = await Task.WhenAny(second, Task.Delay(300)) == second;
        Assert.Equal(expectedConcurrent == 2, completed);
        await first.DisposeAsync();
        await (await second).DisposeAsync();
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task OptedInScannerRunsTwoFilesAtOnceOnlyWithoutLowImpactMode(bool lowImpact, int expectedPeak)
    {
        if (Environment.ProcessorCount < 4)
        {
            return;
        }

        _config.LowSystemImpactMode = lowImpact;
        var items = Enumerable.Range(0, 8).Select(i => (BaseItem)new Movie { Id = Guid.NewGuid(), Path = Path.Combine(_root, "missing-" + i + ".mkv") }).ToList();
        var scanner = new RecordingScanner(new FfprobeService(null!, _db.Probes, NullLogger<FfprobeService>.Instance));
        var reports = new List<double>();

        var issues = await scanner.ScanAsync(items, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(expectedPeak, scanner.Peak);
        Assert.Equal(items.Select(item => item.Path), issues.Select(issue => issue.Path));
        Assert.Equal(100, reports[^1]);
        Assert.True(reports.Zip(reports.Skip(1)).All(pair => pair.First <= pair.Second));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class RecordingScanner(FfprobeService ffprobe) : ProbingScannerBase(ffprobe, NullLogger.Instance)
    {
        private int _active;
        private int _peak;

        public int Peak => _peak;

        public override IssueType Type => IssueType.Playability;

        protected override bool SupportsParallelFiles => true;

        protected override async Task<Issue?> EvaluateAsync(BaseItem item, string path, FfprobeData? probe, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _active);
            int seen;
            while ((seen = Volatile.Read(ref _peak)) < now && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
            {
            }

            // Later items finish sooner so out-of-order completion is exercised.
            await Task.Delay(item.Path.EndsWith("0.mkv", StringComparison.Ordinal) ? 120 : 40, cancellationToken);
            Interlocked.Decrement(ref _active);
            return new Issue();
        }
    }
}

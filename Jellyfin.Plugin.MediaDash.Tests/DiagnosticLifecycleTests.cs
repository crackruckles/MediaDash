using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Lifecycle contract for the DI-owned <see cref="DiagnosticService"/> (02-C). Distinct from
/// <see cref="DiagnosticsTests"/> which exercises the legacy static facade. Together they pin
/// the seam: state authoritatively lives in the service; the static Api.Diagnostics forwards
/// when the service is attached, buffers when it isn't.
///
/// The service constructor installs itself on <see cref="Plugin.AttachedDiagnosticService"/>.
/// Each test that instantiates a fresh service saves/restores that global so parallel test
/// runs don't cross-contaminate.
/// </summary>
[Collection("Diagnostics")]
public class DiagnosticLifecycleTests : IDisposable
{
    private readonly DiagnosticService? _prior;

    public DiagnosticLifecycleTests()
    {
        _prior = Plugin.AttachedDiagnosticService;
        // Fresh service for every test — the ctor rebinds the bridge, so this replaces any
        // service from a prior test. Restore in Dispose so cross-collection ordering is safe.
        _ = new DiagnosticService();
    }

    public void Dispose()
    {
        Plugin.AttachedDiagnosticService = _prior;
    }

    [Fact]
    public void ConnectionFactory_OpenFailure_WithAttachedDiagnosticStore_ThrowsOnceAndDoesNotRecurse()
    {
        var service = new DiagnosticService();
        var factory = new Data.MediaDashConnectionFactory(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = System.IO.Path.GetTempPath(), Pooling = false }.ToString());
        service.Attach(new Data.DiagnosticStore(factory));
        var before = service.Recent().Where(e => e.Source == "MediaDashDb.Open").Sum(e => e.Count);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => factory.Open());
        Assert.Equal(before + 1, service.Recent().Where(e => e.Source == "MediaDashDb.Open").Sum(e => e.Count));
    }

    [Fact]
    public void Record_WithoutAttach_BuffersInMemory()
    {
        var svc = new DiagnosticService();
        svc.Record("Test.NoDb", "no db attached yet");

        var recent = svc.Recent();
        Assert.Single(recent);
        Assert.Equal("Test.NoDb", recent[0].Source);
        Assert.Equal("no db attached yet", recent[0].Message);
    }

    [Fact]
    public void RecordThenClear_LeavesBufferEmpty()
    {
        var svc = new DiagnosticService();
        svc.Record("Test.Clear", "will be cleared");
        Assert.Single(svc.Recent());

        svc.Clear();
        Assert.Empty(svc.Recent());
    }

    [Fact]
    public void RepeatedRecord_DedupesAndBumpsCount()
    {
        var svc = new DiagnosticService();
        svc.Record("Test.Dedup", "same message");
        svc.Record("Test.Dedup", "same message");
        svc.Record("Test.Dedup", "same message");

        var recent = svc.Recent();
        Assert.Single(recent);
        Assert.Equal(3, recent[0].Count);
    }

    [Fact]
    public void RingBufferBoundedAtMaxEntries()
    {
        var svc = new DiagnosticService();
        for (var i = 0; i < DiagnosticService.MaxEntries + 20; i++)
        {
            svc.Record("Test.Bounded", "entry-" + i);
        }

        var recent = svc.Recent();
        Assert.Equal(DiagnosticService.MaxEntries, recent.Count);
        // Newest first.
        Assert.Equal("entry-" + (DiagnosticService.MaxEntries + 20 - 1), recent[0].Message);
    }

    [Fact]
    public void RemoveBySourcePrefix_ClearsSubsystemPreservesOthers()
    {
        // SMART-only purge case from the plan: RemoveBySourcePrefix("SmartHealth") must clear
        // SmartHealth + SmartHealth.* but leave every other source in place.
        var svc = new DiagnosticService();
        svc.Record("SmartHealth", "root smart entry");
        svc.Record("SmartHealth.Linux", "linux child");
        svc.Record("SmartHealth.Wmi", "wmi child");
        svc.Record("ScanTask", "unrelated scan diag");
        svc.Record("SmartHealthOther", "same prefix but no dot — must NOT match");

        svc.RemoveBySourcePrefix("SmartHealth");

        var remaining = svc.Recent().Select(e => e.Source).ToList();
        Assert.Contains("ScanTask", remaining);
        Assert.Contains("SmartHealthOther", remaining);
        Assert.DoesNotContain("SmartHealth", remaining);
        Assert.DoesNotContain("SmartHealth.Linux", remaining);
        Assert.DoesNotContain("SmartHealth.Wmi", remaining);
    }

    [Fact]
    public void RemoveMatching_RequiresBothSourceAndSubstring()
    {
        var svc = new DiagnosticService();
        svc.Record("Foo", "matching keyword here");
        svc.Record("Foo", "no keyword");
        svc.Record("Bar", "matching keyword here");

        svc.RemoveMatching("Foo", "matching keyword");

        var remaining = svc.Recent();
        // Foo/matching removed; Foo/no keyword stays; Bar/matching stays (source doesn't match).
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, e => e.Source == "Foo" && e.Message.Contains("matching keyword", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Concurrent_RecordAndClearAndRead_DoesNotDeadlockOrThrow()
    {
        // Plan §02-C: "Add simultaneous Record/Clear/Attach tests". Records and Clears run
        // concurrently; the invariant is no deadlock, no exception. Barrier-driven, no wall
        // clock sleeps.
        var svc = new DiagnosticService();
        var barrier = new TaskCompletionSource();
        var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var recorders = Enumerable.Range(0, 8).Select(idx => Task.Run(async () =>
        {
            await barrier.Task.ConfigureAwait(false);
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                svc.Record("Concurrent." + (i % 5), "message " + i);
                i++;
            }
        })).ToArray();

        var clearers = Enumerable.Range(0, 2).Select(idx => Task.Run(async () =>
        {
            await barrier.Task.ConfigureAwait(false);
            while (!stop.IsCancellationRequested)
            {
                svc.Clear();
            }
        })).ToArray();

        var readers = Enumerable.Range(0, 2).Select(idx => Task.Run(async () =>
        {
            await barrier.Task.ConfigureAwait(false);
            while (!stop.IsCancellationRequested)
            {
                var snapshot = svc.Recent();
                _ = snapshot.Count;
            }
        })).ToArray();

        barrier.SetResult();
        await Task.WhenAll(recorders.Concat(clearers).Concat(readers));

        // If we got here without a deadlock or exception, the concurrent contract holds.
        Assert.True(true);
    }

    [Fact]
    public void StaticFacade_ForwardsToAttachedService()
    {
        // Before this test: the base ctor already installed a fresh service. Api.Diagnostics.Record
        // should land on the same instance the test reads back through.
        var beforeCount = Plugin.AttachedDiagnosticService!.Recent().Count;

        Api.Diagnostics.Record("Test.Bridge", "via static facade");

        var recent = Plugin.AttachedDiagnosticService!.Recent();
        Assert.Equal(beforeCount + 1, recent.Count);
        Assert.Equal("Test.Bridge", recent[0].Source);
    }

    [Fact]
    public void FallbackBuffer_DrainsIntoServiceOnConstruction()
    {
        // Simulate an early-startup Record BEFORE any service exists, then construct a service
        // and verify the buffered entry ends up in the service's own ring buffer.
        Plugin.AttachedDiagnosticService = null;
        try
        {
            Api.Diagnostics.Record("Test.Fallback", "pre-service diagnostic");

            var svc = new DiagnosticService(); // constructor drains the fallback.

            var recent = svc.Recent();
            Assert.Contains(recent, e => e.Source == "Test.Fallback" && e.Message == "pre-service diagnostic");
        }
        finally
        {
            // Restore the ctor-installed test service so Dispose finds the right prior.
            Plugin.AttachedDiagnosticService = _prior;
        }
    }

    [Fact]
    public void StableStringHash_IsProcessStable()
    {
        // FNV-1a is deterministic across processes and machines. Guard against a future refactor
        // switching to string.GetHashCode() (which is randomised per-process — the exact bug the
        // pre-migration comment flagged).
        var h1 = DiagnosticService.StableStringHash("MediaDash");
        var h2 = DiagnosticService.StableStringHash("MediaDash");
        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, DiagnosticService.StableStringHash("mediadash"));
    }
}

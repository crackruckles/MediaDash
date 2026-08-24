using System.Linq;
using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void ConsecutiveIdenticalEntriesCollapseIntoACountedRow()
    {
        Diagnostics.Clear();
        Diagnostics.Record("FixTask.PermissionDenied", "denied on /mnt/media/foo");
        Diagnostics.Record("FixTask.PermissionDenied", "denied on /mnt/media/foo");
        Diagnostics.Record("FixTask.PermissionDenied", "denied on /mnt/media/foo");

        var entries = Diagnostics.Recent();
        Assert.Single(entries);
        Assert.Equal(3, entries[0].Count);
    }

    [Fact]
    public void DifferentSourceOrMessageStartsANewEntry()
    {
        Diagnostics.Clear();
        Diagnostics.Record("A", "one");
        Diagnostics.Record("A", "two");
        Diagnostics.Record("B", "two");

        var entries = Diagnostics.Recent();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal(1, e.Count));
    }

    [Fact]
    public void RepeatedEntryMergesBackAcrossAnUnrelatedInterruption()
    {
        // Diagnostics.Record now scans the whole buffer for a match (mirrors the SQLite ON CONFLICT
        // semantics of the persisted table). So a third A/one after B/other doesn't start a new row
        // — it merges back into the earlier A/one, bumps its Count to 3, and moves it to the head.
        Diagnostics.Clear();
        Diagnostics.Record("A", "one");
        Diagnostics.Record("A", "one");     // 2x
        Diagnostics.Record("B", "other");   // interruption
        Diagnostics.Record("A", "one");     // merges back into the earlier row, count → 3

        var entries = Diagnostics.Recent().ToList();
        Assert.Equal(2, entries.Count);
        // Newest first: A/one (3, moved to head after the merge), B/other (1)
        Assert.Equal("A", entries[0].Source);
        Assert.Equal(3, entries[0].Count);
        Assert.Equal("B", entries[1].Source);
        Assert.Equal(1, entries[1].Count);
    }
}

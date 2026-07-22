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
    public void CountResumesAfterAnUnrelatedEntry()
    {
        Diagnostics.Clear();
        Diagnostics.Record("A", "one");
        Diagnostics.Record("A", "one");     // 2x
        Diagnostics.Record("B", "other");   // interruption
        Diagnostics.Record("A", "one");     // separate row, not merged back into the earlier one

        var entries = Diagnostics.Recent().ToList();
        Assert.Equal(3, entries.Count);
        // Newest first: A/one (1), B/other (1), A/one (2)
        Assert.Equal(1, entries[0].Count);
        Assert.Equal(1, entries[1].Count);
        Assert.Equal(2, entries[2].Count);
    }
}

using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// "Empty every recycle bin": which batches across which roots MediaDash may delete. Real temp
/// directories; the rule under test is ownership evidence, not the delete loop.
/// </summary>
public sealed class RecycleBinEmptyPlanTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "mediadash-emptyplan-" + Guid.NewGuid().ToString("N"));

    public RecycleBinEmptyPlanTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void CollectsCurrentDefaultAndHistoryRootsIncludingLegacyBatches()
    {
        var current = Root("current");
        var defaultRoot = Root("default");
        var old = Root("old-bin");

        var owned = Batch(current, "20261101-101010-123-0a1b2c3d", marker: true, bytes: 10);
        var legacyReferenced = Batch(current, "20260810-181402-682", bytes: 20);
        var legacyUnreferenced = Batch(current, "20260811-181402-682", bytes: 40);
        var defaultLegacy = Batch(defaultRoot, "20260701-010101-001", bytes: 5);
        var oldNewFormat = Batch(old, "20260901-090909-999-deadbeef", bytes: 7);
        var lookalike = Batch(old, "20260902-090909-999-cafef00d", bytes: 9);
        Directory.CreateDirectory(Path.Combine(current, "Not a batch"));

        var history = new[]
        {
            Path.Combine(legacyReferenced, "a.mkv"),
            Path.Combine(oldNewFormat, "b.mkv"),
        };
        var roots = new[] { current, defaultRoot, old, current };

        var plan = RecycleBin.BuildEmptyPlan(roots, current, defaultRoot, history);

        var all = plan.SelectMany(l => l.Batches).ToHashSet();
        Assert.Contains(owned, all);
        Assert.Contains(legacyReferenced, all);
        Assert.Contains(defaultLegacy, all);
        Assert.Contains(oldNewFormat, all);
        Assert.DoesNotContain(legacyUnreferenced, all);
        Assert.DoesNotContain(lookalike, all);
        Assert.Equal(3, plan.Count);

        var cur = Assert.Single(plan, l => l.IsCurrent);
        Assert.Equal(30, cur.SizeBytes);
        Assert.Equal(2, cur.Batches.Count);
    }

    [Fact]
    public void MissingRootsAndEmptyRootsAreLeftOut()
    {
        var current = Root("current");
        var plan = RecycleBin.BuildEmptyPlan([current, Path.Combine(_scratch, "gone")], current, Path.Combine(_scratch, "default"), []);
        Assert.Empty(plan);
    }

    [Theory]
    [InlineData("20260810-181402-682", true)]
    [InlineData("20260810-181402-682-0a1b2c3d", false)]
    [InlineData("20261399-181402-682", false)]
    [InlineData("Movies", false)]
    public void LegacyBatchNameIsTimestampOnly(string name, bool expected)
    {
        Assert.Equal(expected, RecycleBin.IsLegacyBatchName(name));
    }

    private string Root(string name)
    {
        var path = Path.Combine(_scratch, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Batch(string root, string name, bool marker = false, int bytes = 1)
    {
        var batch = Path.Combine(root, name);
        Directory.CreateDirectory(batch);
        File.WriteAllBytes(Path.Combine(batch, "file.bin"), new byte[bytes]);
        if (marker)
        {
            File.WriteAllBytes(Path.Combine(batch, RecycleBin.OwnershipMarkerFileName), []);
        }

        return batch;
    }
}

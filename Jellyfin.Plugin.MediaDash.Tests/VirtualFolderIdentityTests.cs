using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class VirtualFolderIdentityTests
{
    [Fact]
    public void ReturnsItemIdWhenPopulated()
    {
        var folder = new VirtualFolderInfo { ItemId = "abc123" };
        Assert.Equal("abc123", VirtualFolderIdentity.GetId(folder));
    }

    [Fact]
    public void ReturnsNullWhenNeitherItemIdNorLookupYieldsIdentity()
    {
        var folder = new VirtualFolderInfo { ItemId = null };
        Assert.Null(VirtualFolderIdentity.GetId(folder));
    }

    [Fact]
    public void FallsBackToLookupByNameWhenItemIdIsNull()
    {
        var folder = new VirtualFolderInfo { ItemId = null, Name = "MediaDash Test" };
        var lookup = new Dictionary<string, string> { ["MediaDash Test"] = "deadbeefcafef00d1234567890abcdef" };
        Assert.Equal("deadbeefcafef00d1234567890abcdef", VirtualFolderIdentity.GetId(folder, lookup));
    }

    [Fact]
    public void PrefersItemIdOverLookup()
    {
        // Native ItemId wins even when a lookup is passed — v10.11 hosts never pay for the fallback.
        var folder = new VirtualFolderInfo { ItemId = "native", Name = "test" };
        var lookup = new Dictionary<string, string> { ["test"] = "reflected" };
        Assert.Equal("native", VirtualFolderIdentity.GetId(folder, lookup));
    }

    [Fact]
    public void LookupIsCaseInsensitiveOnName()
    {
        var folder = new VirtualFolderInfo { ItemId = null, Name = "mediadash test" };
        var lookup = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["MediaDash Test"] = "matched",
        };
        Assert.Equal("matched", VirtualFolderIdentity.GetId(folder, lookup));
    }

    [Fact]
    public void MakeKeyTrimsWhitespace()
    {
        Assert.Equal("MediaDash Test", VirtualFolderIdentity.MakeKey("  MediaDash Test  "));
    }

    [Fact]
    public void MakeKeyReturnsEmptyWhenNameMissing()
    {
        Assert.Equal(string.Empty, VirtualFolderIdentity.MakeKey(null));
        Assert.Equal(string.Empty, VirtualFolderIdentity.MakeKey(string.Empty));
    }
}

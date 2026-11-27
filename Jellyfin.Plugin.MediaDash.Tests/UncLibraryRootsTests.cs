using System.Linq;
using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Libraries on a NAS addressed by UNC path never match a drive letter, so the Overview storage card
/// used to omit them. <see cref="UncLibraryRoots"/> picks the share roots out of the library paths.
/// </summary>
public sealed class UncLibraryRootsTests
{
    [Fact]
    public void ExtractsShareRootFromNestedUncPath()
    {
        var roots = UncLibraryRoots.From(new[] { @"\\sunnynas\Shows\The 4400\Season 1" }).ToArray();
        Assert.Equal(new[] { @"\\sunnynas\Shows" }, roots);
    }

    [Fact]
    public void ShareRootItselfIsKept()
    {
        Assert.Equal(new[] { @"\\sunnynas\Shows" }, UncLibraryRoots.From(new[] { @"\\sunnynas\Shows" }).ToArray());
        Assert.Equal(new[] { @"\\sunnynas\Shows" }, UncLibraryRoots.From(new[] { @"\\sunnynas\Shows\" }).ToArray());
    }

    [Fact]
    public void LibrariesOnTheSameShareAreDeduplicatedCaseInsensitively()
    {
        var roots = UncLibraryRoots.From(new[]
        {
            @"\\sunnynas\Shows\Kids",
            @"\\SUNNYNAS\shows\Adults",
            @"\\sunnynas\Movies",
        }).ToArray();
        Assert.Equal(new[] { @"\\sunnynas\Shows", @"\\sunnynas\Movies" }, roots);
    }

    [Fact]
    public void ExtendedLengthUncFormIsNormalised()
    {
        var roots = UncLibraryRoots.From(new[] { @"\\?\UNC\sunnynas\Shows\Kids" }).ToArray();
        Assert.Equal(new[] { @"\\sunnynas\Shows" }, roots);
    }

    [Theory]
    [InlineData(@"C:\Media\Shows")]
    [InlineData(@"S:\")]
    [InlineData("/mnt/media/shows")]
    [InlineData(@"\\?\C:\Media")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\sunnynas")]
    [InlineData(@"\\")]
    [InlineData("")]
    [InlineData("   ")]
    public void NonShareAndMalformedPathsAreIgnored(string path)
    {
        Assert.Empty(UncLibraryRoots.From(new[] { path }));
    }

    [Fact]
    public void MixedListKeepsOnlyShares()
    {
        var roots = UncLibraryRoots.From(new[] { @"C:\Local", @"\\nas\a\x", "/mnt/b", @"\\nas\c" }).ToArray();
        Assert.Equal(new[] { @"\\nas\a", @"\\nas\c" }, roots);
    }
}

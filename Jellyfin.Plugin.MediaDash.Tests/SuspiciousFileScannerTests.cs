using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class SuspiciousFileScannerTests
{
    [Theory]
    [InlineData("/movies/rip/setup.exe")]
    [InlineData("C:\\Media\\Show\\install.MSI")]
    [InlineData("/tv/season1/codec-installer.SCR")]
    [InlineData("/movies/x/hack.ps1")]
    [InlineData("/movies/x/loader.jar")]
    [InlineData("/movies/x/dropper.lnk")]
    public void FlagsKnownExecutableExtensions(string path)
    {
        Assert.True(SuspiciousFileScanner.IsSuspicious(path));
    }

    [Theory]
    [InlineData("/movies/rip/movie.mkv")]
    [InlineData("/tv/show.s01e01.mp4")]
    [InlineData("/music/album/track.flac")]
    [InlineData("/movies/movie.srt")]
    [InlineData("/movies/poster.jpg")]
    [InlineData("/movies/no-extension")]
    [InlineData("")]
    public void IgnoresMediaAndSidecarFiles(string path)
    {
        Assert.False(SuspiciousFileScanner.IsSuspicious(path));
    }
}

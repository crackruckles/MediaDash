using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class ArtworkFixerTests
{
    [Fact]
    public void DeleteArtwork_RemovesFile()
    {
        var tmp = Path.GetTempFileName() + ".jpg";
        File.WriteAllBytes(tmp, new byte[] { 0x00 });
        try
        {
            Assert.True(File.Exists(tmp));
            var result = ArtworkFixer.DeleteArtworkFile(tmp);
            Assert.True(result);
            Assert.False(File.Exists(tmp));
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    [Fact]
    public void DeleteArtwork_MissingFile_ReturnsFalse()
    {
        var reallyGone = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".jpg");
        Assert.False(ArtworkFixer.DeleteArtworkFile(reallyGone));
    }
}

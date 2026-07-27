using System;
using System.IO;
using System.IO.Compression;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class ComicProbeServiceTests
{
    [Fact]
    public void Cbz_Valid_WithOneImage_ReportsOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"good-{Guid.NewGuid():N}.cbz");
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                zip.CreateEntry("001.jpg");
            }

            var result = ComicProbeService.Probe(tmp);
            Assert.True(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Cbz_NoImages_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.cbz");
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                zip.CreateEntry("readme.txt");
            }

            var result = ComicProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Cbz_NotAZip_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.cbz");
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x00, 0x01, 0x02 });
            var result = ComicProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Cbr_NotARar_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.cbr");
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x00 });
            var result = ComicProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Cb7_NotA7z_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.cb7");
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x00 });
            var result = ComicProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

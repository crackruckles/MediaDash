using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class BookProbeServiceTests
{
    [Fact]
    public void Epub_Valid_ReportsOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"good-{Guid.NewGuid():N}.epub");
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write("application/epub+zip");
            }

            var result = BookProbeService.Probe(tmp);
            Assert.True(result.Ok);
            Assert.Null(result.Reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Epub_MissingMimetype_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.epub");
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                zip.CreateEntry("content.opf");
            }

            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
            Assert.NotNull(result.Reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Epub_TruncatedZip_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"trunc-{Guid.NewGuid():N}.epub");
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Pdf_Valid_ReportsOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"good-{Guid.NewGuid():N}.pdf");
        try
        {
            var content = "%PDF-1.4\n%âãÏÓ\n1 0 obj<</Pages 2 0 R/Type/Catalog>>endobj\n2 0 obj<</Kids[]/Count 0/Type/Pages>>endobj\nxref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000063 00000 n \ntrailer<</Root 1 0 R/Size 3>>\nstartxref\n107\n%%EOF\n";
            File.WriteAllText(tmp, content);
            var result = BookProbeService.Probe(tmp);
            Assert.True(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Pdf_MissingEofMarker_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllText(tmp, "%PDF-1.4\nnot a real pdf");
            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Pdf_MissingHeader_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllText(tmp, "hello");
            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Mobi_BadMagic_IsFlagged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.mobi");
        try
        {
            File.WriteAllBytes(tmp, new byte[100]);
            var result = BookProbeService.Probe(tmp);
            Assert.False(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Mobi_ValidMagic_ReportsOk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"good-{Guid.NewGuid():N}.mobi");
        try
        {
            var bytes = new byte[128];
            Encoding.ASCII.GetBytes("BOOKMOBI").CopyTo(bytes, 60);
            File.WriteAllBytes(tmp, bytes);
            var result = BookProbeService.Probe(tmp);
            Assert.True(result.Ok);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

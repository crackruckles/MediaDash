using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class ArtworkScannerTests
{
    [Fact]
    public void ZeroByteFile_IsFlaggedAsCorrupt()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, Array.Empty<byte>());
            var reason = ArtworkScanner.EvaluateFile(tmp, expectedLength: null);
            Assert.NotNull(reason);
            Assert.Contains("empty", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void ValidPng_IsNotFlagged()
    {
        var tmp = Path.GetTempFileName() + ".png";
        try
        {
            // 1x1 transparent PNG — a real, minimum-viable PNG that SkiaSharp will decode successfully.
            File.WriteAllBytes(tmp, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));
            var reason = ArtworkScanner.EvaluateFile(tmp, expectedLength: null);
            Assert.Null(reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void TruncatedPng_IsFlaggedAsCorrupt()
    {
        var tmp = Path.GetTempFileName() + ".png";
        try
        {
            // The first 8 bytes of a valid PNG signature, but no IHDR/IDAT/IEND — SkiaSharp returns null.
            File.WriteAllBytes(tmp, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var reason = ArtworkScanner.EvaluateFile(tmp, expectedLength: null);
            Assert.NotNull(reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void SizeMismatch_IsFlagged()
    {
        var tmp = Path.GetTempFileName() + ".png";
        try
        {
            File.WriteAllBytes(tmp, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));
            var actualLength = new FileInfo(tmp).Length;
            var reason = ArtworkScanner.EvaluateFile(tmp, expectedLength: actualLength + 100);
            Assert.NotNull(reason);
            Assert.Contains("size", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

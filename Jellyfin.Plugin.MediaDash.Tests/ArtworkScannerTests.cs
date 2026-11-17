using System;
using System.IO;
using System.Text;
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

    [Fact]
    public void ValidSvg_IsNotFlagged()
    {
        // #88: a well-formed SVG was being passed to SkiaSharp's raster decoder, whose
        // "decode returned null bitmap" was labelled corruption. The scanner must leave
        // non-raster formats alone instead of asserting on-disk damage.
        var tmp = Path.GetTempFileName() + ".svg";
        try
        {
            File.WriteAllText(
                tmp,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
                + "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\">"
                + "<rect width=\"10\" height=\"10\" fill=\"black\"/></svg>",
                Encoding.UTF8);
            var reason = ArtworkScanner.EvaluateFile(tmp, expectedLength: null);
            Assert.Null(reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void LockedFile_IsNotFlagged()
    {
        // #88 audit check 3: a healthy PNG held with FileShare.None by another process was
        // being labelled "decode error: sharing violation". A transient access failure is
        // not proof of corruption; the scanner must stay quiet until a later read succeeds.
        var tmp = Path.GetTempFileName() + ".png";
        try
        {
            File.WriteAllBytes(tmp, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));
            using (var _ = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var reason = ArtworkScanner.EvaluateFile(tmp, expectedLength: null);
                Assert.Null(reason);
            }
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void UnknownBinarySignature_IsNotFlagged()
    {
        // Random bytes with no recognised raster magic are not corruption evidence.
        // Preventing false flagging matters more than catching every exotic corruption case:
        // a false-positive deletes healthy artwork; a false-negative leaves it alone.
        var tmp = Path.GetTempFileName() + ".dat";
        try
        {
            var random = new byte[4096];
            new Random(99).NextBytes(random);
            random[0] = 0x00;
            random[1] = 0x00;
            random[2] = 0x00;
            File.WriteAllBytes(tmp, random);
            var reason = ArtworkScanner.EvaluateFile(tmp, expectedLength: null);
            Assert.Null(reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void CorruptImageThatThrows_IsFlaggedNotUnhandled()
    {
        // A JPEG-extension file with random garbage bytes can make SkiaSharp throw
        // (SKException) rather than merely return null. Confirm the scanner catches it.
        var tmp = Path.GetTempFileName() + ".jpg";
        try
        {
            var random = new byte[4096];
            new Random(42).NextBytes(random);
            // Force a plausible JPEG header start so SkiaSharp attempts to decode:
            random[0] = 0xFF;
            random[1] = 0xD8;
            random[2] = 0xFF;
            File.WriteAllBytes(tmp, random);
            var reason = ArtworkScanner.EvaluateFile(tmp, expectedLength: null);
            // Either "decode failed" (SkiaSharp returned null) or "decode error: ..." (SkiaSharp threw and we caught):
            // both are acceptable non-null outcomes. What matters is no unhandled exception escapes.
            Assert.NotNull(reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

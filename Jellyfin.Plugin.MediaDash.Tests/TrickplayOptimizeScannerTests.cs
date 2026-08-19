using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class TrickplayOptimizeScannerTests
{
    // Magic-byte prefixes.
    private static readonly byte[] JpgMagic = [0xFF, 0xD8, 0xFF, 0xE0];
    private static readonly byte[] WebpHeader = System.Text.Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBP");

    [Fact]
    public void LooksLikeJpg_TrueForRealJpgHeader()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, JpgMagic);
            Assert.True(TrickplayOptimizeScanner.LooksLikeJpg(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void LooksLikeJpg_FalseForWebPBytesRenamedJpg()
    {
        // The core "don't re-flag files we already converted" case: WebP bytes, .jpg extension.
        var tmp = Path.GetTempFileName() + ".jpg";
        try
        {
            File.WriteAllBytes(tmp, WebpHeader);
            Assert.False(TrickplayOptimizeScanner.LooksLikeJpg(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void LooksLikeJpg_FalseForZeroByteFile()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, Array.Empty<byte>());
            Assert.False(TrickplayOptimizeScanner.LooksLikeJpg(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void MeasureConvertibleJpgs_CountsOnlyRealJpgs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "trickplay-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "0.jpg"), JpgMagic);
            File.WriteAllBytes(Path.Combine(dir, "1.jpg"), JpgMagic);
            // Already-converted file: WebP bytes with .jpg extension. Must NOT be counted.
            File.WriteAllBytes(Path.Combine(dir, "2.jpg"), WebpHeader);

            var (count, bytes) = TrickplayOptimizeScanner.MeasureConvertibleJpgs(dir);
            Assert.Equal(2, count);
            Assert.Equal(JpgMagic.Length * 2, bytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MeasureConvertibleJpgs_MissingDirReturnsZero()
    {
        var missing = Path.Combine(Path.GetTempPath(), "trickplay-nope-" + Guid.NewGuid().ToString("N"));
        var (count, bytes) = TrickplayOptimizeScanner.MeasureConvertibleJpgs(missing);
        Assert.Equal(0, count);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void SiblingTrickplayDir_DerivedFromVideoPath()
    {
        // Jellyfin 12 media-folder layout: sibling folder is basename + ".trickplay".
        var video = Path.Combine("D:", "media", "Movies", "Foo (2023)", "Foo (2023).mkv");
        var expected = Path.Combine("D:", "media", "Movies", "Foo (2023)", "Foo (2023).trickplay");
        Assert.Equal(expected, TrickplayOptimizeScanner.SiblingTrickplayDir(video));
    }

    [Fact]
    public void SiblingTrickplayDir_NullOrEmptyReturnsNull()
    {
        Assert.Null(TrickplayOptimizeScanner.SiblingTrickplayDir(string.Empty));
        Assert.Null(TrickplayOptimizeScanner.SiblingTrickplayDir(null!));
    }

    [Fact]
    public void ShouldWalkMediaFolder_TrueWhenSettingOn_NoProbeNeeded()
    {
        var probeCalls = 0;
        var walk = TrickplayOptimizeScanner.ShouldWalkMediaFolder(
            saveTrickplayWithMedia: true,
            sampleVideoPaths: new[] { @"D:\lib\a.mkv", @"D:\lib\b.mkv" },
            dirExists: _ => { probeCalls++; return false; });
        Assert.True(walk);
        Assert.Equal(0, probeCalls); // shouldn't probe when the setting already says yes
    }

    [Fact]
    public void ShouldWalkMediaFolder_FalseWhenSettingOff_AndProbeMissesEverywhere()
    {
        var walk = TrickplayOptimizeScanner.ShouldWalkMediaFolder(
            saveTrickplayWithMedia: false,
            sampleVideoPaths: new[] { @"D:\lib\a.mkv", @"D:\lib\b.mkv", @"D:\lib\c.mkv" },
            dirExists: _ => false);
        Assert.False(walk);
    }

    [Fact]
    public void ShouldWalkMediaFolder_TrueWhenSettingOff_ButProbeFindsLegacy()
    {
        // Setting says data-folder, but one item still has an old media-folder sibling. Must walk.
        var expectedSibling = Path.Combine(@"D:\lib", "b.trickplay");
        var walk = TrickplayOptimizeScanner.ShouldWalkMediaFolder(
            saveTrickplayWithMedia: false,
            sampleVideoPaths: new[] { @"D:\lib\a.mkv", @"D:\lib\b.mkv", @"D:\lib\c.mkv" },
            dirExists: p => string.Equals(p, expectedSibling, StringComparison.OrdinalIgnoreCase));
        Assert.True(walk);
    }

    [Fact]
    public void ShouldWalkMediaFolder_ProbeIsBoundedByProbeSampleSize()
    {
        // Even with a huge item list, probe must stop after ProbeSampleSize items so slow storage
        // doesn't turn the "cheap probe" into the O(items) walk we were trying to avoid.
        var probed = new List<string>();
        var paths = Enumerable.Range(0, 100).Select(i => $@"D:\lib\{i}.mkv").ToArray();
        TrickplayOptimizeScanner.ShouldWalkMediaFolder(
            saveTrickplayWithMedia: false,
            sampleVideoPaths: paths,
            dirExists: p => { probed.Add(p); return false; });
        Assert.True(probed.Count <= TrickplayOptimizeScanner.ProbeSampleSize);
    }
}

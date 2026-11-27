using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Production 2026-11-27: "MobLand S02E08 … MeGusta.iso" was a 1 GB ISO holding a single .EXE.
public sealed class DiscImageInspectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "md-iso-" + Guid.NewGuid().ToString("N"));

    public DiscImageInspectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void FakeEpisodeImage_HoldingAProgram_IsFlaggedAsMalware()
    {
        var path = Write("MobLand S02E08 1080p WEB H264 - MeGusta.iso", Image(files: ["MOBLAND S02E08 WE DONT TAL.EXE;1"]));
        var contents = DiscImageInspector.Inspect(path);
        Assert.True(contents.Readable);
        Assert.True(contents.HoldsPrograms);

        var issue = SuspiciousFileScanner.DiscImageIssue(path);
        Assert.NotNull(issue);
        Assert.Contains("MOBLAND S02E08 WE DONT TAL.EXE", issue!.SuggestedFix, StringComparison.Ordinal);
        Assert.False(issue.HasBlockingWarnings);
    }

    [Fact]
    public void RealDvdImage_IsLeftAlone()
    {
        var path = Write("Movie (1999).iso", Image(dirs: [("VIDEO_TS", ["VIDEO_TS.IFO;1", "VTS_01_1.VOB;1"])]));
        var contents = DiscImageInspector.Inspect(path);
        Assert.True(contents.HasDiscVideo);
        Assert.Null(SuspiciousFileScanner.DiscImageIssue(path));
    }

    [Fact]
    public void RetailDiscWithPcExtras_IsLeftAlone()
    {
        var path = Write("Movie (2004).iso", Image(files: ["SETUP.EXE;1", "AUTORUN.INF;1"], dirs: [("VIDEO_TS", ["VIDEO_TS.IFO;1"])]));
        Assert.Null(SuspiciousFileScanner.DiscImageIssue(path));
    }

    [Fact]
    public void BluRayImage_IsLeftAlone()
    {
        var path = Write("Movie (2010).iso", Image(dirs: [("BDMV", ["INDEX.BDM;1"])]));
        Assert.Null(SuspiciousFileScanner.DiscImageIssue(path));
    }

    [Fact]
    public void ProgramsInSubfolders_AreFound()
    {
        var path = Write("Show S01E01.iso", Image(dirs: [("DATA", ["PAYLOAD.SCR;1", "README.TXT;1"])]));
        Assert.Equal(["DATA/PAYLOAD.SCR"], DiscImageInspector.Inspect(path).Programs);
    }

    [Fact]
    public void ImageWithNoVideo_NeedsApproval()
    {
        var path = Write("Some Game.iso", Image(files: ["GAME.DAT;1", "README.TXT;1"]));
        var issue = SuspiciousFileScanner.DiscImageIssue(path);
        Assert.NotNull(issue);
        Assert.True(issue!.HasBlockingWarnings);
    }

    [Fact]
    public void JolietNames_AreReadInFull()
    {
        var path = Write("Show S01E02.iso", Image(files: ["Show S01E02 1080p WEB-DL x264.exe;1"], joliet: true));
        Assert.Equal(["Show S01E02 1080p WEB-DL x264.exe"], DiscImageInspector.Inspect(path).Programs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(40000)]
    public void NonIsoFiles_AreNeverFlagged(int size)
    {
        var path = Write("Movie.img", new byte[size]);
        Assert.False(DiscImageInspector.Inspect(path).Readable);
        Assert.Null(SuspiciousFileScanner.DiscImageIssue(path));
    }

    [Fact]
    public void CorruptDirectory_IsNotFlagged()
    {
        var bytes = Image(files: ["A.EXE;1"]);
        Array.Fill(bytes, (byte)0xFF, 20 * 2048, 2048);
        var path = Write("Broken.iso", bytes);
        Assert.Null(SuspiciousFileScanner.DiscImageIssue(path));
    }

    [Theory]
    [InlineData("x.iso", true)]
    [InlineData("x.IMG", true)]
    [InlineData("x.mkv", false)]
    [InlineData("x.nrg", false)]
    public void OnlyIsoAndImgAreInspected(string name, bool expected) => Assert.Equal(expected, DiscImageInspector.IsDiscImage(name));

    private static byte[] Image(string[]? files = null, (string Name, string[] Files)[]? dirs = null, bool joliet = false)
    {
        const int S = 2048;
        files ??= [];
        dirs ??= [];
        var image = new byte[(24 + dirs.Length) * S];
        var rootSector = 20;
        var root = new List<byte[]> { Record(rootSector, S, true, [0], false), Record(rootSector, S, true, [1], false) };
        for (var i = 0; i < dirs.Length; i++)
        {
            var sector = 21 + i;
            root.Add(Record(sector, S, true, Name(dirs[i].Name, joliet), false));
            var sub = new List<byte[]> { Record(sector, S, true, [0], false), Record(rootSector, S, true, [1], false) };
            sub.AddRange(dirs[i].Files.Select(file => Record(30, 10, false, Name(file, joliet), false)));
            Copy(image, sector * S, sub);
        }

        root.AddRange(files.Select(file => Record(30, 10, false, Name(file, joliet), false)));
        Copy(image, rootSector * S, root);

        var descriptor = 16;
        if (joliet)
        {
            Descriptor(image, descriptor++, 2, rootSector, joliet: true);
        }
        else
        {
            Descriptor(image, descriptor++, 1, rootSector, joliet: false);
        }

        image[descriptor * S] = 255;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, (descriptor * S) + 1);
        return image;
    }

    private static byte[] Name(string name, bool joliet) => joliet ? Encoding.BigEndianUnicode.GetBytes(name) : Encoding.ASCII.GetBytes(name);

    private static void Descriptor(byte[] image, int sector, byte type, int rootSector, bool joliet)
    {
        var at = sector * 2048;
        image[at] = type;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, at + 1);
        image[at + 6] = 1;
        if (joliet)
        {
            image[at + 88] = 0x25;
            image[at + 89] = 0x2F;
            image[at + 90] = 0x45;
        }

        Record(rootSector, 2048, true, [0], false).CopyTo(image, at + 156);
    }

    private static byte[] Record(int extent, int length, bool directory, byte[] name, bool unused)
    {
        var size = 33 + name.Length + ((33 + name.Length) % 2);
        var record = new byte[size];
        record[0] = (byte)size;
        BitConverter.GetBytes((uint)extent).CopyTo(record, 2);
        BitConverter.GetBytes((uint)length).CopyTo(record, 10);
        record[25] = (byte)(directory ? 2 : 0);
        record[32] = (byte)name.Length;
        name.CopyTo(record, 33);
        return record;
    }

    private static void Copy(byte[] image, int at, List<byte[]> records)
    {
        foreach (var record in records)
        {
            record.CopyTo(image, at);
            at += record.Length;
        }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

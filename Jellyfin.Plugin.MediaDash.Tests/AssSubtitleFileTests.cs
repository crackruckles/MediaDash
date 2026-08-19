using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class AssSubtitleFileTests
{
    private const string SampleAss =
        "[Script Info]\n" +
        "Title: Sample\n" +
        "\n" +
        "[V4+ Styles]\n" +
        "Format: Name, Fontname, Fontsize, PrimaryColour\n" +
        "Style: Default,Noto Sans,20,&H00FFFFFF\n" +
        "Style: Sign,KaraokeFont,24,&H00FFFF00\n" +
        "\n" +
        "[Events]\n" +
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
        "Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,Hello\n" +
        "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\fn OverrideFont}World\n" +
        "\n" +
        "[Fonts]\n" +
        "fontname: NotoSans-Bold_B0.ttf\n" +
        "aaaabbbbccccddddaaaabbbbccccddddaaaabbbbccccdddd\n" +
        "aaaabbbbccccddddaaaabbbbccccddddaaaabbbbccccdddd\n" +
        "fontname: TotallyUnusedFont_R0.ttf\n" +
        "eeeeffffgggghhhheeeeffffgggghhhheeeeffffgggghhhh\n" +
        "fontname: KaraokeFont_R0.ttf\n" +
        "iiiijjjjkkkkllllmmmmnnnnooooppppqqqqrrrrssssuuuu\n";

    [Fact]
    public void Parse_ExtractsReferencedFontnames_FromStylesAndInlineOverrides()
    {
        var ass = AssSubtitleFile.ParseBytes(Encoding.UTF8.GetBytes(SampleAss));
        var refs = ass.ReferencedFontnames();

        Assert.Contains("Noto Sans", refs);
        Assert.Contains("KaraokeFont", refs);
        Assert.Contains("OverrideFont", refs);
        Assert.Equal(3, refs.Count);
    }

    [Fact]
    public void Parse_EnumeratesEmbeddedFonts_WithBytesEstimate()
    {
        var ass = AssSubtitleFile.ParseBytes(Encoding.UTF8.GetBytes(SampleAss));
        var fonts = ass.EmbeddedFonts();

        Assert.Equal(3, fonts.Count);
        Assert.Equal("NotoSans-Bold_B0.ttf", fonts[0].Filename);
        Assert.Equal("TotallyUnusedFont_R0.ttf", fonts[1].Filename);
        Assert.Equal("KaraokeFont_R0.ttf", fonts[2].Filename);
        Assert.All(fonts, f => Assert.True(f.BytesEstimate > 0));
    }

    [Fact]
    public void CanonicalizeName_StripsCommonSuffixesAndSeparators()
    {
        // Style suffix + extension.
        Assert.Equal("notosans", AssSubtitleFile.CanonicalizeName("NotoSans-Bold_B0.ttf"));
        // Just extension + suffix, no dashes.
        Assert.Equal("karaokefont", AssSubtitleFile.CanonicalizeName("KaraokeFont_R0.ttf"));
        // Style name with a space collapses.
        Assert.Equal("notosans", AssSubtitleFile.CanonicalizeName("Noto Sans"));
    }

    [Fact]
    public void IsReferenced_MatchesEmbeddedFilenameToStyleName()
    {
        var refs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "Noto Sans", "KaraokeFont" };
        Assert.True(AssSubtitleFile.IsReferenced("NotoSans-Bold_B0.ttf", refs));
        Assert.True(AssSubtitleFile.IsReferenced("KaraokeFont_R0.ttf", refs));
        Assert.False(AssSubtitleFile.IsReferenced("TotallyUnusedFont_R0.ttf", refs));
    }

    [Fact]
    public void StripFontsExcept_KeepsOnlyListedBlocks_AndRoundTripsThroughSave()
    {
        var ass = AssSubtitleFile.ParseBytes(Encoding.UTF8.GetBytes(SampleAss));
        ass.StripFontsExcept(new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "NotoSans-Bold_B0.ttf",
            "KaraokeFont_R0.ttf"
        });

        var tmp = Path.GetTempFileName();
        try
        {
            ass.Save(tmp);
            var reparsed = AssSubtitleFile.Parse(tmp);
            var kept = reparsed.EmbeddedFonts();
            Assert.Equal(2, kept.Count);
            Assert.DoesNotContain(kept, f => f.Filename.Contains("TotallyUnused", System.StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void ForceFontname_RewritesStyleFontnamesAndInlineOverrides()
    {
        var ass = AssSubtitleFile.ParseBytes(Encoding.UTF8.GetBytes(SampleAss));
        ass.ForceFontname("Arial");
        ass.ClearAllFonts();

        var tmp = Path.GetTempFileName();
        try
        {
            ass.Save(tmp);
            var written = File.ReadAllText(tmp);
            Assert.Contains("Style: Default,Arial", written, System.StringComparison.Ordinal);
            Assert.Contains("Style: Sign,Arial", written, System.StringComparison.Ordinal);
            Assert.Contains(@"\fnArial", written, System.StringComparison.Ordinal);
            Assert.DoesNotContain("[Fonts]", written, System.StringComparison.Ordinal);

            var reparsed = AssSubtitleFile.Parse(tmp);
            Assert.Empty(reparsed.EmbeddedFonts());
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void PreservesUtf8Bom_WhenPresent()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var withBom = bom.Concat(Encoding.UTF8.GetBytes(SampleAss)).ToArray();
        var ass = AssSubtitleFile.ParseBytes(withBom);
        var tmp = Path.GetTempFileName();
        try
        {
            ass.Save(tmp);
            var raw = File.ReadAllBytes(tmp);
            Assert.True(raw.Length >= 3);
            Assert.Equal(0xEF, raw[0]);
            Assert.Equal(0xBB, raw[1]);
            Assert.Equal(0xBF, raw[2]);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Utf16File_IsRefusedWithNotSupported()
    {
        var utf16 = new byte[] { 0xFF, 0xFE, 0x41, 0x00, 0x42, 0x00 };
        Assert.Throws<System.NotSupportedException>(() => AssSubtitleFile.ParseBytes(utf16));
    }
}

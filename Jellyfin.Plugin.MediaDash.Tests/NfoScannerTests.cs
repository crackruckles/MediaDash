using System;
using System.IO;
using System.Text;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class NfoScannerTests
{
    [Fact]
    public void EvaluateFile_ZeroByte_IsFlaggedEmpty()
    {
        var tmp = Path.GetTempFileName() + ".nfo";
        try
        {
            File.WriteAllBytes(tmp, Array.Empty<byte>());
            var reason = NfoScanner.EvaluateFile(tmp);
            Assert.NotNull(reason);
            Assert.Contains("empty", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void EvaluateFile_ValidMovie_IsNotFlagged()
    {
        var tmp = Path.GetTempFileName() + ".nfo";
        try
        {
            File.WriteAllText(tmp,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<movie><title>Foo</title><year>2023</year></movie>\n");
            Assert.Null(NfoScanner.EvaluateFile(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void EvaluateFile_ValidTvshow_IsNotFlagged()
    {
        var tmp = Path.GetTempFileName() + ".nfo";
        try
        {
            File.WriteAllText(tmp, "<tvshow><title>Show</title></tvshow>");
            Assert.Null(NfoScanner.EvaluateFile(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void EvaluateFile_ValidEpisodedetails_IsNotFlagged()
    {
        var tmp = Path.GetTempFileName() + ".nfo";
        try
        {
            File.WriteAllText(tmp, "<episodedetails><title>Ep1</title></episodedetails>");
            Assert.Null(NfoScanner.EvaluateFile(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void EvaluateFile_MalformedXml_IsFlagged()
    {
        var tmp = Path.GetTempFileName() + ".nfo";
        try
        {
            // Missing closing tag — XmlReader throws on parse.
            File.WriteAllText(tmp, "<movie><title>Unfinished");
            var reason = NfoScanner.EvaluateFile(tmp);
            Assert.NotNull(reason);
            Assert.Contains("malformed", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void EvaluateFile_UnknownRoot_IsFlagged()
    {
        var tmp = Path.GetTempFileName() + ".nfo";
        try
        {
            File.WriteAllText(tmp, "<garbage><oops /></garbage>");
            var reason = NfoScanner.EvaluateFile(tmp);
            Assert.NotNull(reason);
            Assert.Contains("not a Jellyfin NFO type", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void EvaluateFile_ValidSeason_IsNotFlagged()
    {
        // Jellyfin writes season.nfo with <season> root. Field report A3: this was flagged as
        // "not a Jellyfin NFO type", and the CorruptNfo fixer then deletes it.
        var tmp = Path.GetTempFileName() + ".nfo";
        try
        {
            File.WriteAllText(tmp, "<season><seasonnumber>1</seasonnumber></season>");
            Assert.Null(NfoScanner.EvaluateFile(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void EvaluateFile_MovieWithTrailingUrl_IsNotFlagged()
    {
        // Kodi/Jellyfin NFO convention: bare provider URL after </movie>. Document-mode XmlReader
        // throws on this; Fragment mode (and Jellyfin's own reader) accepts it. Field report A3.
        var tmp = Path.GetTempFileName() + ".nfo";
        try
        {
            File.WriteAllText(tmp,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<movie><title>Foo</title></movie>\n" +
                "https://www.themoviedb.org/movie/12345\n");
            Assert.Null(NfoScanner.EvaluateFile(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void EvaluateFile_NonXmlGarbage_IsFlagged()
    {
        var tmp = Path.GetTempFileName() + ".nfo";
        try
        {
            File.WriteAllBytes(tmp, Encoding.UTF8.GetBytes("this is a plaintext note, not xml at all"));
            var reason = NfoScanner.EvaluateFile(tmp);
            Assert.NotNull(reason);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class ReportedScannerRegressionTests
{
    [Fact]
    public void DismissedDuplicateLoser_IsNotMarkedDoomed()
    {
        var findings = new[] { new Issue { Path = "keep.mkv" }, new Issue { Path = "delete.mkv" } };
        Assert.Equal(new[] { "delete.mkv" }, ScanTask.ComputeDoomedPaths(findings, new[] { "keep.mkv" }));
    }

    [Fact]
    public void UnknownBitrate_DoesNotLoseRankingCriterion()
    {
        var unknown = new DuplicateScanner.Candidate { Path = "unknown.mkv", Size = 100, Bitrate = 0 };
        var known = new DuplicateScanner.Candidate { Path = "known.mkv", Size = 200, Bitrate = 1000 };
        Assert.Same(unknown, DuplicateScanner.Rank([unknown, known], ["Bitrate", "Size"], [])[0]);
    }

    [Fact]
    public void SubtitleSidecars_RequireStemBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "md-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "S01E1.en.ass"), "subtitle");
            File.WriteAllText(Path.Combine(root, "S01E10.en.ass"), "subtitle");
            Assert.Equal(new[] { Path.Combine(root, "S01E1.en.ass") }, SubtitleFontScanner.FindSidecars(Path.Combine(root, "S01E1.mkv")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("https://www.imdb.com/title/tt1234567/")]
    [InlineData("tt1234567")]
    [InlineData("https://www.themoviedb.org/movie/123")]
    [InlineData("https://thetvdb.com/series/example")]
    public void ProviderHintNfo_IsPreserved(string text)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, text);
            Assert.Null(NfoScanner.EvaluateFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

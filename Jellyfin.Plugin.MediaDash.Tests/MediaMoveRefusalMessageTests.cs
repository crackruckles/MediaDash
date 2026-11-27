using System;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class MediaMoveRefusalMessageTests
{
    private const string Root = "/tv/Dig (2026)";

    private static MediaMovePlan.Payload File(string name, long length = 2_570_000_000, long ticks = 638_680_000_000_000_000, ulong id = 7)
        => new(name, false, length, ticks, 1, id, string.Empty);

    [Fact]
    public void ARewrittenFileNamesTheFileAndBothSizes()
    {
        var change = MediaMoveSnapshot.DescribeChange(Root, [File("Pilot.mkv")], [File("Pilot.mkv", length: 1_100_000_000)], true);
        Assert.Equal("'Pilot.mkv' was rewritten (its size changed from 2.39 GB to 1.02 GB)", change);
        var message = MediaMoveSnapshot.ChangedSinceApproval(change);
        Assert.StartsWith("Not moved: 'Pilot.mkv' was rewritten", message, StringComparison.Ordinal);
        Assert.Contains("Nothing was moved or changed.", message, StringComparison.Ordinal);
        Assert.Contains("Run a scan", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReplacedCopyIsReportedWhenSizeAndTimeMatch()
        => Assert.Equal("'Pilot.mkv' was replaced by a new copy of the file", MediaMoveSnapshot.DescribeChange(Root, [File("Pilot.mkv")], [File("Pilot.mkv", id: 8)], true));

    [Fact]
    public void AModifiedFileReportsWhenItChanged()
        => Assert.StartsWith("'Pilot.mkv' was modified (last changed ", MediaMoveSnapshot.DescribeChange(Root, [File("Pilot.mkv")], [File("Pilot.mkv", ticks: 638_680_000_100_000_000)], true), StringComparison.Ordinal);

    [Fact]
    public void RemovedAndAddedFilesAreNamed()
    {
        Assert.Equal("'Pilot.srt' was removed or renamed", MediaMoveSnapshot.DescribeChange(Root, [File("Pilot.mkv"), File("Pilot.srt")], [File("Pilot.mkv")], true));
        Assert.Equal("a new file 'Pilot.nfo' appeared in 'Dig (2026)'", MediaMoveSnapshot.DescribeChange(Root, [File("Pilot.mkv")], [File("Pilot.mkv"), File("Pilot.nfo")], true));
    }
}

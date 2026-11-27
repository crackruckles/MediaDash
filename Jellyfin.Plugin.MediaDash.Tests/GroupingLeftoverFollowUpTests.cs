using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class GroupingLeftoverFollowUpTests
{
    [Fact]
    public void ReadMoveTarget_PrefersTarget()
        => Assert.Equal("/tv/Show (2025)/Season 02/e.mkv", FixTask.ReadMoveTarget("{\"action\":\"MoveFile\",\"target\":\"/tv/Show (2025)/Season 02/e.mkv\",\"targetPath\":\"/other\"}"));

    [Fact]
    public void ReadMoveTarget_FallsBackToTargetPath()
        => Assert.Equal("/tv/Show/e.mkv", FixTask.ReadMoveTarget("{\"targetPath\":\"/tv/Show/e.mkv\"}"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"target\":5}")]
    [InlineData("{\"target\":\"\"}")]
    public void ReadMoveTarget_IgnoresMissingOrBadTargets(string? details)
        => Assert.Null(FixTask.ReadMoveTarget(details));
}
using System.Diagnostics;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class MediaProcessTests
{
    [Fact]
    public void IdlePriorityIsAppliedBeforeEncoderStarts()
    {
        var start = new ProcessStartInfo("/opt/jellyfin ffmpeg/ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add("movie with spaces.mkv");
        MediaProcess.WrapWithIonice(start, "/usr/bin/ionice");
        Assert.Equal("/usr/bin/ionice", start.FileName);
        Assert.Equal(new[] { "-t", "-c", "3", "--", "/opt/jellyfin ffmpeg/ffmpeg", "-i", "movie with spaces.mkv" }, start.ArgumentList);
        Assert.True(start.RedirectStandardError);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void MissingIoniceLeavesEncoderAndArgumentsUnchanged()
    {
        var start = new ProcessStartInfo("ffprobe");
        start.ArgumentList.Add("movie.mkv");
        MediaProcess.WrapWithIonice(start, null);
        Assert.Equal("ffprobe", start.FileName);
        Assert.Equal(new[] { "movie.mkv" }, start.ArgumentList);
    }
}

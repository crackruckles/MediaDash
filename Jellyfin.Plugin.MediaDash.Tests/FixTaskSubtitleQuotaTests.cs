using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class FixTaskSubtitleQuotaTests
{
    [Theory]
    [InlineData("OpenSubtitles download limit reached")]
    [InlineData("en: OpenSubtitles download limit reached")]
    [InlineData("en: OpenSubtitles download limit reached; fr: OpenSubtitles download limit reached")]
    [InlineData("download quota exceeded, try again later")]
    public void IsSubtitleProviderQuotaExhausted_MatchesKnownWordings(string message)
    {
        Assert.True(FixTask.IsSubtitleProviderQuotaExhausted(message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("en: no matches from any provider")]
    [InlineData("en: provider unreachable (Name or service not known)")]
    [InlineData("Jellyfin can't write to '/mnt/media'.")]
    public void IsSubtitleProviderQuotaExhausted_LeavesUnrelatedFailuresAlone(string message)
    {
        Assert.False(FixTask.IsSubtitleProviderQuotaExhausted(message));
    }
}

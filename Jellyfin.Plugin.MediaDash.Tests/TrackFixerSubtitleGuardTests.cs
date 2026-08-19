using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class TrackFixerSubtitleGuardTests
{
    // Regression: some Jellyfin builds report embedded PGS/Bluray subtitle streams as IsExternal=true
    // with Path pointing at the container itself. The scanner filter should never write that path into
    // the issue's externalFiles list — but if a stale DetailsJson from an earlier build did, the fixer
    // guard must still catch it, otherwise the remuxed video is moved to the recycle bin and its size
    // is credited as "subtitle bytes freed" (the 1.1 GB reclaim mystery).
    [Fact]
    public void SelfReferentialSubtitleIsFlagged()
    {
        var video = "/library/movies/Film (2020)/Film.mkv";
        Assert.True(TrackFixer.IsSelfReferentialSubtitle(video, video));
    }

    [Fact]
    public void CaseInsensitiveMatchIsFlagged()
    {
        Assert.True(TrackFixer.IsSelfReferentialSubtitle(
            "/library/movies/Film/FILM.MKV",
            "/library/movies/Film/film.mkv"));
    }

    [Fact]
    public void TrueSidecarIsNotFlagged()
    {
        Assert.False(TrackFixer.IsSelfReferentialSubtitle(
            "/library/movies/Film/film.fra.srt",
            "/library/movies/Film/film.mkv"));
    }

    [Fact]
    public void EmptyStringsAreNotFlagged()
    {
        Assert.False(TrackFixer.IsSelfReferentialSubtitle(string.Empty, "/library/movies/film.mkv"));
        Assert.False(TrackFixer.IsSelfReferentialSubtitle("/library/movies/film.mkv", string.Empty));
    }

    [Fact]
    public void GetExternalFilesRoundtripsBadDataSoTheFixerGuardMatters()
    {
        // Documents that a pre-guard scanner could persist the video path here — the fixer's guard is
        // the last line of defence for existing installs with stale JSON in the DB.
        var video = "/library/movies/Film.mkv";
        var json = "{\"externalFiles\":[\"" + video + "\",\"/library/movies/Film.fra.srt\"]}";
        var parsed = TrackFixer.GetExternalFilesForTest(json);
        Assert.Equal(2, parsed.Count);
        Assert.Contains(video, parsed);
    }
}

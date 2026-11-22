using System;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Production 2026-11-22: Jellyfin merged three releases of Rick and Morty S09E05 into one episode with
// alternate versions, so the duplicate scanner (which skips merged versions since field report A4)
// never offered them. Same-episode versions are now candidates; A4-style merges are not.
public sealed class DuplicateMergedVersionTests
{
    private static Episode Episode(string path, params string[] versions)
    {
        var seriesId = Guid.Parse("8e057f32-273c-043a-9056-f55c015ea066");
        return new Episode { Id = Guid.NewGuid(), SeriesId = seriesId, ParentIndexNumber = 9, IndexNumber = 5, Path = path, LocalAlternateVersions = versions };
    }

    [Fact]
    public void SameEpisodeMergedVersionsAreDuplicateCandidates()
    {
        var episode = Episode("/tv/Rick and Morty (2013)/Season 9/Rick and Morty S09E05 Jer Bud 1080p AMZN WEB-DL DDP5 1 H 264-playWEB.mkv",
            "/tv/Rick and Morty (2013)/Season 9/Rick.and.Morty.S09E05.Jer.Bud.1080p.AMZN.WEB-DL.DDP5.1.H.264-FLUX.mkv",
            "/tv/Rick and Morty (2013)/Season 9/rick.and.morty.s09e05.multi.1080p.web.h264-higgsboson.mkv");

        var group = Assert.Single(DuplicateScanner.CandidateGroups([episode]));
        Assert.Equal(3, group.Value.Select(entry => entry.Path).Distinct().Count());
        Assert.All(group.Value, entry => Assert.Same(episode, entry.Item));
    }

    [Fact]
    public void DifferentReleasesOfOneEpisodeAreOneEdition()
    {
        var episode = Episode("/tv/Rick and Morty (2013)/Season 9/Rick and Morty S09E05 Jer Bud 1080p AMZN WEB-DL DDP5 1 H 264-playWEB.mkv",
            "/tv/Rick and Morty (2013)/Season 9/Rick.and.Morty.S09E05.Jer.Bud.1080p.AMZN.WEB-DL.DDP5.1.H.264-FLUX.mkv",
            "/tv/Rick and Morty (2013)/Season 9/rick.and.morty.s09e05.multi.1080p.web.h264-higgsboson.mkv");
        var group = Assert.Single(DuplicateScanner.CandidateGroups([episode]));
        var edition = Assert.Single(DuplicateScanner.SplitByEdition(group.Key, group.Value));
        Assert.Equal(3, edition.Count);
    }

    [Fact]
    public void LabelledVersionsAndExplicitEditionsOfAnEpisodeStaySeparate()
    {
        var labelled = Episode("/tv/Show/Season 1/Show S01E05 - 1080p.mkv", "/tv/Show/Season 1/Show S01E05 - 4K.mkv");
        var group = Assert.Single(DuplicateScanner.CandidateGroups([labelled]));
        Assert.All(DuplicateScanner.SplitByEdition(group.Key, group.Value), edition => Assert.Single(edition));

        var tagged = Episode("/tv/Show/Season 1/Show S01E05 {edition-Extended}.mkv", "/tv/Show/Season 1/Show.S01E05.1080p-GRP.mkv");
        var taggedGroup = Assert.Single(DuplicateScanner.CandidateGroups([tagged]));
        Assert.All(DuplicateScanner.SplitByEdition(taggedGroup.Key, taggedGroup.Value), edition => Assert.Single(edition));
    }

    [Fact]
    public void MergedVersionsOfDifferentEpisodesStayIntentional()
    {
        // Field report A4: Jellyfin merged different episodes into one item's versions.
        var episode = Episode("/tv/Firefly/Season 1/Firefly S01E01.mkv", "/tv/Firefly/Season 1/Firefly S01E02.mkv");
        Assert.Empty(DuplicateScanner.CandidateGroups([episode]));

        var unnumbered = Episode("/tv/Show/Season 1/Show S01E05.mkv", "/tv/Show/Season 1/Bonus cut.mkv");
        Assert.Empty(DuplicateScanner.CandidateGroups([unnumbered]));
    }
}

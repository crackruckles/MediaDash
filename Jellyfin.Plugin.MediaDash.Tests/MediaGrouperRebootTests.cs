using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// GitHub #43 regression guard. Jellyfin's TVDb match collapses year-differentiated reboot
/// series into a single Series entity with a bare SeriesName ("Doctor Who", "Silo") — grouping
/// on SeriesName alone destroys the year-suffixed folders users manually create on disk. The
/// resolver must honor the on-disk year suffix before falling back to metadata.
/// </summary>
public sealed class MediaGrouperRebootTests
{
    private const string TvRoot = "/media/tv";

    // Rule 1 — on-disk parent-of-parent year suffix wins over collapsed SeriesName.
    [Fact]
    public void DoctorWho1963_KeepsRebootFolderFromOnDisk()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Doctor Who",
            seriesPremiereYear: 1963,
            episodeFilePath: "/media/tv/Doctor Who (1963)/Season 01/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Doctor Who (1963)", result);
    }

    [Fact]
    public void DoctorWho2005_KeepsRebootFolderFromOnDisk()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Doctor Who",
            seriesPremiereYear: 1963, // TVDb reports the ORIGINAL premiere year — on-disk must still win
            episodeFilePath: "/media/tv/Doctor Who (2005)/Season 01/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Doctor Who (2005)", result);
    }

    [Fact]
    public void DoctorWho2024_KeepsRebootFolderFromOnDisk()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Doctor Who",
            seriesPremiereYear: 1963,
            episodeFilePath: "/media/tv/Doctor Who (2024)/Season 01/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Doctor Who (2024)", result);
    }

    // Vaygrim's report: SeriesName "Silo" was clobbering the user's "Silo (2023)/" folder.
    [Fact]
    public void Silo2023_KeepsUserYearSuffixFromOnDisk()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Silo",
            seriesPremiereYear: 2023,
            episodeFilePath: "/media/tv/Silo (2023)/Season 1/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Silo (2023)", result);
    }

    // Rule 1 also fires for the "no Season/ subfolder" layout — series files loose in Series/.
    [Fact]
    public void OnDiskYearSuffix_FiresForNoSeasonLayout()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Silo",
            seriesPremiereYear: 2023,
            episodeFilePath: "/media/tv/Silo (2023)/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Silo (2023)", result);
    }

    // Rule 2 — Jellyfin PremiereDate appends year suffix when on-disk lacks one.
    [Fact]
    public void NoOnDiskYear_UsesPremiereDateSuffix()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Breaking Bad",
            seriesPremiereYear: 2008,
            episodeFilePath: "/media/tv/Breaking Bad/Season 01/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Breaking Bad (2008)", result);
    }

    // Rule 2 must NOT re-append a year when SeriesName is already year-tagged.
    [Fact]
    public void PremiereDate_DoesNotDoubleUpAlreadyYearTaggedSeriesName()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Show (2023)",
            seriesPremiereYear: 2023,
            episodeFilePath: "/media/tv/loose-file/S01E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Show (2023)", result);
    }

    // Rule 3 — no on-disk hint, no PremiereDate: plain SeriesName as today.
    [Fact]
    public void NoDisambiguationSignals_FallsBackToPlainSeriesName()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Breaking Bad",
            seriesPremiereYear: null,
            episodeFilePath: "/media/tv/Breaking Bad/Season 01/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Breaking Bad", result);
    }

    // Loose file at library root — no ancestor to inspect, no crash, falls through cleanly.
    [Fact]
    public void FileLooseAtLibraryRoot_UsesPlainSeriesName()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Some Show",
            seriesPremiereYear: null,
            episodeFilePath: "/media/tv/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Some Show", result);
    }

    // Empty SeriesName falls back to filename extraction (pre-existing behavior of
    // ExtractShowNameFromFilename): a filename like "The Office S03E14" extracts "The Office"
    // and grouping still succeeds. Just verifies the reboot resolver doesn't break that path.
    [Fact]
    public void EmptySeriesName_FallsBackToFilenameExtraction()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: null,
            seriesPremiereYear: null,
            episodeFilePath: "/media/tv/The Office S03E14.mkv",
            tvRoot: TvRoot);
        Assert.Equal("The Office", result);
    }

    // Guard: a top-level year-tagged LIBRARY folder ("TV (2024)/") must NOT be picked as
    // the series folder for everything under it. Only strictly-inside-library ancestors qualify.
    [Fact]
    public void LibraryRootWithYearSuffix_NotUsedAsSeriesFolder()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Breaking Bad",
            seriesPremiereYear: 2008,
            episodeFilePath: "/media/TV (2024)/Breaking Bad/Season 01/E01.mkv",
            tvRoot: "/media/TV (2024)");
        // Root is "TV (2024)"; parent-of-parent equals the root and is excluded; falls to PremiereDate.
        Assert.Equal("Breaking Bad (2008)", result);
    }
}

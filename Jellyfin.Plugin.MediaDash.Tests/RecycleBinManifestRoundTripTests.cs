using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Manifest side is the guarantee that every recycled file remembers where it came from — even
/// files that no HistoryEntry references (external subtitle sidecars, pre-strip audio originals,
/// user-initiated Files-tab deletes). These tests exercise the read side of that contract; the
/// write side runs inside MoveToBin (Plugin.Instance-bound) so is validated by
/// FixerMoveToBinParityTests + the manual QA on localhost.
/// </summary>
public class RecycleBinManifestRoundTripTests
{
    [Fact]
    public void ReadOriginManifest_ReturnsEmpty_WhenManifestFileAbsent()
    {
        using var t = new BatchDir();
        // No manifest written — a pre-manifest legacy batch that predates the sidecar reader.
        Assert.Empty(RecycleBin.ReadOriginManifest(t.Batch));
    }

    [Fact]
    public void ReadOriginManifest_ReturnsAllNonBlankLines()
    {
        using var t = new BatchDir();
        var manifest = Path.Combine(t.Batch, RecycleBin.OriginManifestFileName);
        File.WriteAllText(manifest,
            @"C:\media\movies\A Movie.mkv" + Environment.NewLine +
            Environment.NewLine +                                              // blank line — skipped
            @"   " + Environment.NewLine +                                     // whitespace — skipped
            @"C:\media\music\Song.mp3" + Environment.NewLine);

        var lines = RecycleBin.ReadOriginManifest(t.Batch);
        Assert.Equal(2, lines.Length);
        Assert.Equal(@"C:\media\movies\A Movie.mkv", lines[0]);
        Assert.Equal(@"C:\media\music\Song.mp3", lines[1]);
    }

    [Fact]
    public void MatchManifestEntryToFile_PicksTheMatchingBasename()
    {
        var manifest = new[]
        {
            @"C:\media\movies\A Movie.mkv",
            @"C:\media\music\Song.mp3",
            @"C:\media\tv\S01E01.mkv"
        };

        var match = RecycleBin.MatchManifestEntryToFile(manifest, "Song.mp3");
        Assert.Equal(@"C:\media\music\Song.mp3", match);
    }

    [Fact]
    public void MatchManifestEntryToFile_ReturnsNull_WhenBasenameNotPresent()
    {
        var manifest = new[] { @"C:\media\A.mkv", @"C:\media\B.mkv" };
        Assert.Null(RecycleBin.MatchManifestEntryToFile(manifest, "C.mkv"));
    }

    [Fact]
    public void MatchManifestEntryToFile_PicksFirstOnAmbiguousBasenames()
    {
        // Two library paths sharing a basename (e.g. "cover.jpg" in every music folder) — pick the
        // first-appended line. Ambiguity is an edge case; deterministic behavior beats surprise.
        var manifest = new[]
        {
            @"C:\media\music\Album A\cover.jpg",
            @"C:\media\music\Album B\cover.jpg"
        };

        var match = RecycleBin.MatchManifestEntryToFile(manifest, "cover.jpg");
        Assert.Equal(@"C:\media\music\Album A\cover.jpg", match);
    }

    [Fact]
    public void MatchManifestEntryToFile_IsCaseSensitive_MatchingWindowsAndLinuxWithSameSemantics()
    {
        // Origin paths are stored verbatim from the caller. A Linux caller with a case-sensitive
        // filesystem must not have "Song.mp3" match "song.mp3" — that would silently restore to the
        // wrong file. Keep the compare Ordinal so both platforms agree.
        var manifest = new[] { @"/media/music/Song.mp3" };
        Assert.Null(RecycleBin.MatchManifestEntryToFile(manifest, "song.mp3"));
        Assert.Equal(@"/media/music/Song.mp3", RecycleBin.MatchManifestEntryToFile(manifest, "Song.mp3"));
    }

    [Fact]
    public void MatchManifestEntryToFile_SkipsBlankLines()
    {
        // ReadOriginManifest strips blanks, but the matcher is defensive against a caller passing raw lines.
        var manifest = new[] { string.Empty, @"C:\media\A.mkv" };
        Assert.Equal(@"C:\media\A.mkv", RecycleBin.MatchManifestEntryToFile(manifest, "A.mkv"));
    }

    private sealed class BatchDir : IDisposable
    {
        public BatchDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "mediadash-manifest-" + Guid.NewGuid().ToString("N"));
            Batch = Directory.CreateDirectory(Path.Combine(Root, "20260827-120000-000-a1b2c3d4")).FullName;
        }

        public string Root { get; }

        public string Batch { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup — a lingering file handle shouldn't fail the test.
            }
        }
    }
}

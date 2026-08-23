using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class OrphanCleanupScannerTests
{
    // ---------- HasCompanionVideo (orphan subtitle detection) ----------

    [Fact]
    public void HasCompanionVideo_TrueWhenSameBasenameVideoExists()
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Sub("Foo.mkv"), string.Empty);
        var sub = scratch.Sub("Foo.srt");
        File.WriteAllText(sub, string.Empty);
        Assert.True(OrphanCleanupScanner.HasCompanionVideo(sub));
    }

    [Fact]
    public void HasCompanionVideo_TrueForLanguageSuffixedSubtitle()
    {
        // Foo.en.srt should pair with Foo.mkv (strip the trailing .en language token).
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Sub("Foo.mkv"), string.Empty);
        var sub = scratch.Sub("Foo.en.srt");
        File.WriteAllText(sub, string.Empty);
        Assert.True(OrphanCleanupScanner.HasCompanionVideo(sub));
    }

    [Fact]
    public void HasCompanionVideo_FalseWhenNoVideoInFolder()
    {
        using var scratch = new Scratch();
        var sub = scratch.Sub("Standalone.srt");
        File.WriteAllText(sub, string.Empty);
        Assert.False(OrphanCleanupScanner.HasCompanionVideo(sub));
    }

    [Fact]
    public void HasCompanionVideo_FalseWhenOnlyUnrelatedVideosPresent()
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Sub("Different.mkv"), string.Empty);
        var sub = scratch.Sub("MissingParent.en.srt");
        File.WriteAllText(sub, string.Empty);
        Assert.False(OrphanCleanupScanner.HasCompanionVideo(sub));
    }

    // ---------- HasCompanionVideoForTrickplay ----------

    [Fact]
    public void HasCompanionVideoForTrickplay_TrueWhenCompanionExists()
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Sub("Bar.mp4"), string.Empty);
        var tp = scratch.Sub("Bar.trickplay");
        Directory.CreateDirectory(tp);
        Assert.True(OrphanCleanupScanner.HasCompanionVideoForTrickplay(tp));
    }

    [Fact]
    public void HasCompanionVideoForTrickplay_FalseWhenCompanionGone()
    {
        using var scratch = new Scratch();
        var tp = scratch.Sub("Ghost.trickplay");
        Directory.CreateDirectory(tp);
        Assert.False(OrphanCleanupScanner.HasCompanionVideoForTrickplay(tp));
    }

    // ---------- DetectEmptyFolders ----------

    [Fact]
    public void DetectEmptyFolders_FlagsVideoFreeSubtreeButNotLibraryRoot()
    {
        using var scratch = new Scratch();
        // Library root itself: empty. Must NOT be flagged.
        // Movies/Junk/  has only a .nfo (no video). Must be flagged.
        // Movies/Keep/Real.mkv                     — must NOT be flagged.
        Directory.CreateDirectory(scratch.Sub("Junk"));
        File.WriteAllText(Path.Combine(scratch.Sub("Junk"), "info.nfo"), string.Empty);
        Directory.CreateDirectory(scratch.Sub("Keep"));
        File.WriteAllText(Path.Combine(scratch.Sub("Keep"), "Real.mkv"), string.Empty);

        var issues = new List<Issue>();
        OrphanCleanupScanner.DetectEmptyFolders(new[] { scratch.Root }, issues, CancellationToken.None);

        Assert.Single(issues);
        Assert.EndsWith("Junk", issues[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectEmptyFolders_MusicLibrary_NotFlagged()
    {
        // 2026-08-23 regression (B5). A music library has zero VIDEO files by definition —
        // previously every artist/album directory registered as "video-free" and the fixer wiped
        // the whole library. Music extensions must count as media for the empty-folder pass.
        using var scratch = new Scratch();
        Directory.CreateDirectory(scratch.Sub("Artist/Album"));
        File.WriteAllText(Path.Combine(scratch.Sub("Artist/Album"), "01 - Track.mp3"), string.Empty);
        File.WriteAllText(Path.Combine(scratch.Sub("Artist/Album"), "02 - Track.flac"), string.Empty);

        var issues = new List<Issue>();
        OrphanCleanupScanner.DetectEmptyFolders(new[] { scratch.Root }, issues, CancellationToken.None);

        Assert.Empty(issues);
    }

    [Fact]
    public void DetectEmptyFolders_BooksAndComicsAndPictures_NotFlagged()
    {
        // Same class of bug for other non-video libraries. Any recognised media extension keeps
        // the containing folder off the delete list.
        using var scratch = new Scratch();
        Directory.CreateDirectory(scratch.Sub("Books"));
        File.WriteAllText(Path.Combine(scratch.Sub("Books"), "Dune.epub"), string.Empty);
        Directory.CreateDirectory(scratch.Sub("Comics"));
        File.WriteAllText(Path.Combine(scratch.Sub("Comics"), "Watchmen.cbz"), string.Empty);
        Directory.CreateDirectory(scratch.Sub("Photos"));
        File.WriteAllText(Path.Combine(scratch.Sub("Photos"), "IMG_0001.jpg"), string.Empty);

        var issues = new List<Issue>();
        OrphanCleanupScanner.DetectEmptyFolders(new[] { scratch.Root }, issues, CancellationToken.None);

        Assert.Empty(issues);
    }

    [Fact]
    public void DetectEmptyFolders_TopmostVideoFreeIsPickedNotNestedLeaves()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(scratch.Sub("Junk"));
        Directory.CreateDirectory(Path.Combine(scratch.Sub("Junk"), "SubA"));
        Directory.CreateDirectory(Path.Combine(scratch.Sub("Junk"), "SubB"));
        File.WriteAllText(Path.Combine(scratch.Sub("Junk"), "SubA", "notes.txt"), string.Empty);

        var issues = new List<Issue>();
        OrphanCleanupScanner.DetectEmptyFolders(new[] { scratch.Root }, issues, CancellationToken.None);

        // Only ONE issue — the topmost "Junk", not both SubA and SubB.
        Assert.Single(issues);
        Assert.EndsWith("Junk", issues[0].Path, StringComparison.Ordinal);
    }

    // ---------- DetectOrphanSubtitles ----------

    [Fact]
    public void DetectOrphanSubtitles_EmitsIssueForSidecarsWithNoCompanion()
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Sub("Video.mkv"), string.Empty);
        File.WriteAllText(scratch.Sub("Video.en.srt"), "paired");
        File.WriteAllText(scratch.Sub("Ghost.en.srt"), "orphan");

        var issues = new List<Issue>();
        OrphanCleanupScanner.DetectOrphanSubtitles(new[] { scratch.Root }, issues, CancellationToken.None);

        Assert.Single(issues);
        Assert.EndsWith("Ghost.en.srt", issues[0].Path, StringComparison.Ordinal);
    }

    // ---------- DetectOrphanTrickplay ----------

    [Fact]
    public void DetectOrphanTrickplay_EmitsIssueForFoldersWithNoCompanion()
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Sub("Movie1.mkv"), string.Empty);
        Directory.CreateDirectory(scratch.Sub("Movie1.trickplay"));
        Directory.CreateDirectory(scratch.Sub("MovieGone.trickplay"));

        var issues = new List<Issue>();
        OrphanCleanupScanner.DetectOrphanTrickplay(new[] { scratch.Root }, issues, CancellationToken.None);

        Assert.Single(issues);
        Assert.EndsWith("MovieGone.trickplay", issues[0].Path, StringComparison.Ordinal);
    }

    private sealed class Scratch : IDisposable
    {
        public Scratch()
        {
            Root = Path.Combine(Path.GetTempPath(), "orphan-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Sub(string name) => System.IO.Path.Combine(Root, name);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup — test cleanup can race with FS locks on Windows.
            }
        }
    }
}

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

    // Field report: SDH / forced / multi-token sidecars were being flagged as orphans and deleted.
    // The old token-stripping logic only removed ONE trailing dot-token with a ≤5-char cap, so
    // Foo.en.sdh.srt never reduced past Foo.en and Foo.forced.srt was never stripped at all
    // (forced = 6 chars). Prefix matching handles arbitrary trailing metadata tokens.
    [Theory]
    [InlineData("Foo.en.sdh.srt")]
    [InlineData("Foo.en.hi.srt")]
    [InlineData("Foo.en.cc.srt")]
    [InlineData("Foo.forced.srt")]
    [InlineData("Foo.en.forced.srt")]
    [InlineData("Foo.default.srt")]
    [InlineData("Foo.foreign.srt")]
    [InlineData("Foo.sign.srt")]
    [InlineData("Foo.commentary.srt")]
    [InlineData("Foo.eng.SDH.srt")]
    [InlineData("Foo.en.sdh.forced.srt")]
    public void HasCompanionVideo_TrueForSubtitleFlavourTokens(string sidecarName)
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Sub("Foo.mkv"), string.Empty);
        var sub = scratch.Sub(sidecarName);
        File.WriteAllText(sub, string.Empty);
        Assert.True(OrphanCleanupScanner.HasCompanionVideo(sub));
    }

    [Fact]
    public void HasCompanionVideo_FalseWhenVideoIsMerelyPrefixSubstringWithoutDotSeparator()
    {
        // Foobar.en.srt must NOT be treated as a companion of Foo.mkv — the video base "Foo" is a
        // substring but not a dotted prefix. Prevents deleting a real orphan just because an unrelated
        // shorter-named video sits nearby.
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Sub("Foo.mkv"), string.Empty);
        var sub = scratch.Sub("Foobar.en.srt");
        File.WriteAllText(sub, string.Empty);
        Assert.False(OrphanCleanupScanner.HasCompanionVideo(sub));
    }

    // Every extension in SubtitleFormats.Extensions must be recognised by the orphan pass — if we
    // don't recognise it, a real orphan of that format won't be swept AND (worse) an existing sidecar
    // whose extension we don't know is never checked for a companion, so it stays. The list below
    // must match SubtitleFormats.Extensions exactly; add a case when a new format lands there.
    [Theory]
    [InlineData(".srt")]
    [InlineData(".ass")]
    [InlineData(".ssa")]
    [InlineData(".vtt")]
    [InlineData(".sub")]
    [InlineData(".idx")]
    [InlineData(".sup")]
    [InlineData(".smi")]
    [InlineData(".sami")]
    [InlineData(".mks")]
    public void SubtitleExtensions_ContainsFormat(string ext)
    {
        Assert.Contains(ext, OrphanCleanupScanner.SubtitleExtensions);
    }

    // Spot-check formerly-missing Jellyfin video extensions now covered via MediaFormats.Video.
    // Previously .iso, .strm, .wtv, .rm, .asf, .m2t, .mxf, .mk3d were not in VideoExtensions —
    // an .en.srt next to an .iso rip was flagged as an orphan and deleted.
    [Theory]
    [InlineData(".iso")]
    [InlineData(".strm")]
    [InlineData(".wtv")]
    [InlineData(".rm")]
    [InlineData(".asf")]
    [InlineData(".m2t")]
    [InlineData(".mxf")]
    [InlineData(".mk3d")]
    [InlineData(".xvid")]
    [InlineData(".dvr-ms")]
    public void VideoExtensions_CoversFullJellyfinList(string ext)
    {
        Assert.Contains(ext, OrphanCleanupScanner.VideoExtensions);
    }

    // MediaExtensions is the "user media" union for the empty-folder pass. Confirm audio and photo
    // libraries won't collapse to "empty" — previously Jellyfin's audio list beyond the 13 curated
    // codes went unrecognised, meaning a folder with only .aiff / .opus-in-.oga / .dsf / etc could
    // still be flagged.
    [Theory]
    [InlineData(".mp3")]
    [InlineData(".flac")]
    [InlineData(".opus")]
    [InlineData(".oga")]
    [InlineData(".aiff")]
    [InlineData(".dsf")]
    [InlineData(".ape")]
    [InlineData(".epub")]
    [InlineData(".cbz")]
    [InlineData(".heic")]
    [InlineData(".mkv")]
    public void MediaExtensions_CoversAllMediaKinds(string ext)
    {
        Assert.Contains(ext, OrphanCleanupScanner.MediaExtensions);
    }

    // ProbingScannerBase skips these — ffmpeg can't decode disc images, stub files or split-archive
    // markers as an ordinary stream, so probing them yields only diagnostic noise. Critically, this
    // also prevents PlayabilityScanner from flagging an .iso rip as "unplayable" and the fixer from
    // recycling it. Every entry below must remain in NonProbable AND in Video — the file is
    // Jellyfin-classified as video (so orphan pairing still works) but not sent to ffmpeg.
    [Theory]
    [InlineData(".iso")]
    [InlineData(".img")]
    [InlineData(".nrg")]
    [InlineData(".ifo")]
    [InlineData(".strm")]
    [InlineData(".disc")]
    [InlineData(".001")]
    [InlineData(".bin")]
    public void NonProbable_ExtensionsAreListed(string ext)
    {
        Assert.Contains(ext, MediaFormats.NonProbable);
    }

    [Theory]
    [InlineData(".iso")]
    [InlineData(".strm")]
    [InlineData(".ifo")]
    public void NonProbable_AlsoAppearsInVideoSoOrphanPairingStillWorks(string ext)
    {
        // A subtitle sitting next to Movie.iso must still find its companion — the orphan pass runs
        // on the filesystem, not through ffmpeg, so these formats are correctly classed as video
        // for pairing purposes while being blocked from probing.
        Assert.Contains(ext, MediaFormats.Video);
    }

    [Fact]
    public void HasCompanionVideo_TrueForSubtitleInSubtitlesSubfolder()
    {
        // The Season01/Subtitles/ layout still resolves upward, and the prefix rule applies there too.
        using var scratch = new Scratch();
        var seasonDir = scratch.Sub("Season 01");
        Directory.CreateDirectory(seasonDir);
        File.WriteAllText(Path.Combine(seasonDir, "Foo.mkv"), string.Empty);
        var subsDir = Path.Combine(seasonDir, "Subtitles");
        Directory.CreateDirectory(subsDir);
        var sub = Path.Combine(subsDir, "Foo.en.sdh.srt");
        File.WriteAllText(sub, string.Empty);
        Assert.True(OrphanCleanupScanner.HasCompanionVideo(sub));
    }

    // Extended container-folder recognition — any variant a real library uses. Whitelist covers
    // localized names (Spanish/French/German/Italian/Russian/Chinese/Japanese/Korean), and the
    // prefix rule catches "Subs-EN", "Subtitles (SDH)", "CaptionsForced", etc.
    [Theory]
    [InlineData("Subtitle")]
    [InlineData("Caption")]
    [InlineData("CC")]
    [InlineData("SRT")]
    [InlineData("Subs-EN")]
    [InlineData("Subtitles (SDH)")]
    [InlineData("CaptionsForced")]
    [InlineData("subtitulos")]
    [InlineData("Legendas")]
    [InlineData("Sous-titres")]
    [InlineData("Untertitel")]
    [InlineData("sottotitoli")]
    [InlineData("字幕")]
    public void HasCompanionVideo_TrueForBroadenedSubfolderNames(string folderName)
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Sub("Foo.mkv"), string.Empty);
        var subsDir = scratch.Sub(folderName);
        Directory.CreateDirectory(subsDir);
        var sub = Path.Combine(subsDir, "Foo.en.srt");
        File.WriteAllText(sub, string.Empty);
        Assert.True(OrphanCleanupScanner.HasCompanionVideo(sub));
    }

    [Fact]
    public void HasCompanionVideo_FalseForUnrelatedSubfolder()
    {
        // A folder that neither matches the whitelist nor starts with "sub" / "caption" still
        // shouldn't walk upward — otherwise an adjacent Extras/ or Bonus/ folder would false-pair.
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Sub("Foo.mkv"), string.Empty);
        var otherDir = scratch.Sub("Extras");
        Directory.CreateDirectory(otherDir);
        var sub = Path.Combine(otherDir, "Foo.en.srt");
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

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Task-2 regressions for duplicate-vs-alias handling. The fixer must never recycle a hardlinked
/// or symlinked copy, must refuse disposal when the keeper and loser now resolve to the same
/// physical file, and must refuse when the OS identity cannot be read (platform failure is
/// inconclusive, not evidence of independence).
/// </summary>
[Collection("Diagnostics")]
public sealed class DuplicateAliasTests : IDisposable
{
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-dup-alias-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _config = new() { DryRun = false };
    private readonly MediaDashDb _db;
    private readonly LibraryGuard _guard;
    private readonly ILibraryMonitor _monitor;
    private readonly ILibraryManager _manager;

    public DuplicateAliasTests()
    {
        Directory.CreateDirectory(_root);
        _db = new MediaDashDb(Path.Combine(_root, "test.db"));
        var plugin = (Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, _config);
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
        var manager = DispatchProxy.Create<ILibraryManager, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)manager).Call = (method, _) =>
            method.Name == "GetVirtualFolders"
                ? new System.Collections.Generic.List<MediaBrowser.Model.Entities.VirtualFolderInfo> { new() { Locations = [_root] } }
                : null;
        _manager = manager;
        _guard = new LibraryGuard(manager);
        _monitor = DispatchProxy.Create<ILibraryMonitor, OriginalLanguageFlowTests.CallProxy>();
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string Write(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private DuplicateFixer NewFixer() => new(
        _guard, NewRecycleBin(), _monitor, _manager, NullLogger<DuplicateFixer>.Instance);

    private RecycleBin NewRecycleBin()
    {
        var paths = DispatchProxy.Create<MediaBrowser.Common.Configuration.IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (method, _) => method.Name switch
        {
            "get_DataPath" => (object)Path.Combine(_root, "bin"),
            "get_ProgramDataPath" => Path.Combine(_root, "bin"),
            _ => null,
        };
        return new RecycleBin(paths, NullLogger<RecycleBin>.Instance, _db.History);
    }

    [Theory]
    [InlineData(DisposalMethod.Permanent)]
    [InlineData(DisposalMethod.RecycleBin)]
    public async Task DuplicateSweep_LeavesSiblingBooksAndUnrelatedFiles(DisposalMethod disposal)
    {
        _config.DuplicateDisposal = disposal;
        var keeper = Write(Path.Combine("keeper", "A.epub"), new byte[1024]);
        var loser = Write(Path.Combine("books", "A.epub"), new byte[1024]);
        var book = Write(Path.Combine("books", "B.epub"), [1, 2]);
        var pdf = Write(Path.Combine("books", "C.pdf"), [3, 4]);
        var photo = Write(Path.Combine("books", "photo.jpg"), [5, 6]);
        var result = await NewFixer().FixAsync(new Issue { Type = IssueType.Duplicate, Path = loser, DetailsJson = BuildDupDetails(keeper, loser) }, null, default);
        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(loser));
        Assert.True(File.Exists(book));
        Assert.True(File.Exists(pdf));
        Assert.True(File.Exists(photo));
        Assert.True(Directory.Exists(Path.GetDirectoryName(loser)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Purge_NonPositiveRetention_PreservesRecentBatch(int days)
    {
        var source = Write("original.ass", [1, 2, 3]);
        var bin = NewRecycleBin();
        var recycled = bin.MoveToBin(source);
        bin.Purge(days);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(recycled));
    }

    [Fact]
    public void FailedRecycle_RemovesUnusedOwnedBatch()
    {
        var source = Write("source.ass", [1, 2, 3]);
        var bin = NewRecycleBin();
        var recycled = bin.MoveToBin(source);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(recycled))!;
        var before = Directory.EnumerateDirectories(root).Count();
        Assert.Throws<IOException>(() => bin.MoveToBin(Path.Combine(_root, "missing.ass")));
        Assert.Equal(before, Directory.EnumerateDirectories(root).Count());
    }

    [Fact]
    public async Task SubtitleFonts_RealRewrite_PreservesOriginalInBin()
    {
        var content = "[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,Noto Sans,20\n[Events]\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,Hello\n[Fonts]\nfontname: Unused_R0.ttf\naaaabbbbccccdddd\n";
        var original = System.Text.Encoding.UTF8.GetBytes(content);
        var path = Write("subtitle.ass", original);
        var fixer = new SubtitleFontFixer(_guard, NullLogger<SubtitleFontFixer>.Instance, NewRecycleBin());
        var result = await fixer.FixAsync(new Issue { Type = IssueType.SubtitleFonts, Path = path }, null, default);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.RecyclePath);
        Assert.Equal(original, File.ReadAllBytes(result.RecyclePath!));
        Assert.DoesNotContain("fontname:", File.ReadAllText(path));
    }

    [Fact]
    public async Task SubtitleFonts_NonUtf8_RefusesAndPreservesBytes()
    {
        byte[] original = [0x5B, 0x45, 0x76, 0x65, 0x6E, 0x74, 0x73, 0x5D, 0x0A, 0xE9];
        var path = Write("legacy.ass", original);
        var fixer = new SubtitleFontFixer(_guard, NullLogger<SubtitleFontFixer>.Instance, NewRecycleBin());
        var result = await fixer.FixAsync(new Issue { Type = IssueType.SubtitleFonts, Path = path }, null, default);
        Assert.Equal(FixReason.UnsupportedFormat, result.Reason);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("100", "20", false)]
    [InlineData("100", "100", true)]
    public async Task OutputVerifier_UnknownDuration_RequiresFrameEvidence(string? sourceFrames, string? outputFrames, bool accepted)
    {
        var output = Write("verified.mkv", [1, 2, 3]);
        var sourceProbe = new FfprobeData { Streams = [new() { CodecType = "video", NbFrames = sourceFrames }] };
        var outputProbe = new FfprobeData { Streams = [new() { CodecType = "video", NbFrames = outputFrames }], Format = new() { Duration = "1" } };
        var info = new FileInfo(output);
        _db.Probes.StoreProbe(output, info.Length, info.LastWriteTimeUtc.Ticks, JsonSerializer.Serialize(outputProbe));
        var probe = new FfprobeService(null!, _db.Probes, NullLogger<FfprobeService>.Instance);
        var reason = await new OutputVerifier(probe).VerifyAsync(sourceProbe, output, default);
        Assert.Equal(accepted, reason is null);
    }

    [Fact]
    public void ForceRestore_RestoreFails_PutsCurrentOccupantBack()
    {
        var path = Write("restore.mkv", [1, 2, 3]);
        var bin = NewRecycleBin();
        var recycled = bin.MoveToBin(path);
        _db.History.AddHistory(new HistoryEntry { Path = path, RecyclePath = recycled, Type = IssueType.Quality, Success = true, FixedAtUtc = DateTime.UtcNow });
        var id = Assert.Single(_db.GetHistory()).Id;
        File.WriteAllBytes(path, [4, 5, 6]);
        var service = new RestoreService(_db, _db.History, _db.RestoreOps, bin, _guard, _monitor,
            NullLogger<RestoreService>.Instance, restore: (source, destination) =>
            {
                if (source == recycled) throw new IOException("owned restore fault");
                bin.Restore(source, destination);
            });
        var result = service.RestoreFromHistory(id, force: true);
        Assert.False(result.Success);
        Assert.NotEqual(FixReason.RecoveryRequired, result.Reason);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(path));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(recycled));
    }

    [Fact]
    public async Task RepairRung_CancelledAtExecutor_DeletesOwnedTemporaryFile()
    {
        var path = Write("cancelled.mkv", [1, 2, 3]);
        var temp = TranscodeFixer.SidecarPath(path, "repair.tmp1", "mkv");
        File.WriteAllBytes(temp, [4, 5, 6]);
        var encoder = DispatchProxy.Create<MediaBrowser.Controller.MediaEncoding.IMediaEncoder, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)encoder).Call = (method, _) => method.Name == "get_EncoderPath" ? "unused-owned-test-encoder" : null;
        var probe = new FfprobeService(encoder, _db.Probes, NullLogger<FfprobeService>.Instance);
        var bin = NewRecycleBin();
        var fixer = new PlayabilityFixer(probe, new FfmpegExecutor(encoder, NullLogger<FfmpegExecutor>.Instance), new OutputVerifier(probe),
            _guard, bin, new VerifiedReplacementService(bin), new PluginConfigurationProvider(() => _config), _monitor, _manager, NullLogger<PlayabilityFixer>.Instance);
        var method = typeof(PlayabilityFixer).GetMethod("TryRung1Async", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var task = (Task<string?>)method.Invoke(fixer, [new Issue { Path = path }, new FfprobeData(), cts.Token])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.False(File.Exists(temp));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task PerStreamDecode_Timeout_LeavesRepairLadderAvailable()
    {
        var path = Write("decode-timeout.mkv", [1, 2, 3]);
        var encoder = DispatchProxy.Create<MediaBrowser.Controller.MediaEncoding.IMediaEncoder, OriginalLanguageFlowTests.CallProxy>();
        ((OriginalLanguageFlowTests.CallProxy)(object)encoder).Call = (_, _) => throw new TimeoutException("owned decode timeout");
        var probe = new FfprobeService(encoder, _db.Probes, NullLogger<FfprobeService>.Instance);
        var bin = NewRecycleBin();
        var fixer = new PlayabilityFixer(probe, null!, new OutputVerifier(probe), _guard, bin, new VerifiedReplacementService(bin),
            new PluginConfigurationProvider(() => _config), _monitor, _manager, NullLogger<PlayabilityFixer>.Instance);
        var method = typeof(PlayabilityFixer).GetMethod("IdentifyBrokenStreamsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<System.Collections.Generic.IReadOnlyList<int>>)method.Invoke(fixer,
            [new Issue { Path = path }, new FfprobeData { Streams = [new() { Index = 0, CodecType = "video" }] }, CancellationToken.None])!;
        Assert.Empty(await task);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void FolderFreeSpace_UsesCombinedInputSize()
    {
        var first = Write(Path.Combine("chapters", "01.mp3"), new byte[1024]);
        Write(Path.Combine("chapters", "02.mp3"), new byte[3072]);
        var folder = Path.GetDirectoryName(first)!;
        Assert.NotNull(FreeSpaceGuard.DescribeShortfall(folder, 2, availableBytes: 8191));
        Assert.Null(FreeSpaceGuard.DescribeShortfall(folder, 2, availableBytes: 8192));
    }

    private static bool CreateHardLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            proc!.WaitForExit();
            return proc.ExitCode == 0;
        }

        using var ln = Process.Start(new ProcessStartInfo("ln", $"\"{target}\" \"{link}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        ln!.WaitForExit();
        return ln.ExitCode == 0;
    }

    private static bool CreateSymlinkFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // On Windows this requires Developer Mode or Administrator for file symlinks. Skip
            // cleanly so the test is a known-skipped platform gap rather than a false failure.
            return false;
        }
    }

    private static string BuildDupDetails(string keeperPath, string thisPath)
    {
        Assert.True(PhysicalFileIdentity.TryRead(keeperPath, out var keeper));
        Assert.True(PhysicalFileIdentity.TryRead(thisPath, out var loser));
        return JsonSerializer.Serialize(new
        {
            keeperIdentity = new { vol = keeper.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture), id = keeper.FileId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            loserIdentity = new { vol = loser.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture), id = loser.FileId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            groupKey = "test",
            keeperPath,
            keeper = new { Resolution = "1920x1080", Codec = "h264", Size = (long)1, Bitrate = (long)1 },
            thisCopy = new { Resolution = "1920x1080", Codec = "h264", Size = (long)1, Bitrate = (long)1 },
            confidence = 1.0,
            signals = new { },
        });
    }

    [Fact]
    public void PhysicalFileIdentity_ReadsOwnedDirectoriesAndNamesWithSpaces()
    {
        var directory = Path.Combine(_root, "metadata with spaces");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, OperatingSystem.IsWindows() ? "poster with spaces.png" : "poster with \"quotes\" and \\slashes.png");
        File.WriteAllBytes(file, [1, 2, 3]);
        Assert.True(PhysicalFileIdentity.TryRead(directory, out var directoryIdentity));
        Assert.True(PhysicalFileIdentity.TryRead(file, out var fileIdentity));
        Assert.NotEqual(directoryIdentity, fileIdentity);
        Assert.False(directoryIdentity.IsReparsePoint);
        Assert.False(fileIdentity.IsReparsePoint);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
    }

    [Fact]
    public void PhysicalFileIdentity_PairOfDistinctFilesHasDifferentIdentity()
    {
        var a = Write("a.bin", [1, 2, 3]);
        var b = Write("b.bin", [1, 2, 3]);
        Assert.True(PhysicalFileIdentity.TryRead(a, out var av));
        Assert.True(PhysicalFileIdentity.TryRead(b, out var bv));
        Assert.NotEqual(av, bv);
        Assert.Equal(1u, av.LinkCount);
        Assert.False(av.IsReparsePoint);
    }

    [Fact]
    public void PhysicalFileIdentity_HardlinkReportsSameIdentityAndLinkCount2()
    {
        var target = Write("target.bin", [1, 2, 3, 4]);
        var link = Path.Combine(_root, "hardlink.bin");
        if (!CreateHardLink(link, target))
        {
            // Platform cannot create hardlink (readonly fs, cross-vol temp, etc). Report as skip.
            Assert.Fail("Could not create hardlink in test fixture — platform gap, not a repair regression.");
        }

        try
        {
            Assert.True(PhysicalFileIdentity.TryRead(target, out var tv));
            Assert.True(PhysicalFileIdentity.TryRead(link, out var lv));
            Assert.Equal(tv, lv);
            Assert.Equal(2u, tv.LinkCount);
            Assert.Equal(2u, lv.LinkCount);
            Assert.False(tv.IsReparsePoint);
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
        }
    }

    [Fact]
    public void PhysicalFileIdentity_SymlinkIsFlaggedAsReparsePoint()
    {
        var target = Write("real.bin", [9, 9, 9]);
        var link = Path.Combine(_root, "link.bin");
        if (!CreateSymlinkFile(link, target))
        {
            // Platform cannot create file symlink without privilege (Windows w/o Developer Mode).
            return;
        }

        try
        {
            Assert.True(PhysicalFileIdentity.TryRead(link, out var lv));
            Assert.True(lv.IsReparsePoint);
        }
        finally
        {
            try
            {
                File.Delete(link);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task DuplicateFixer_RefusesWhenLoserBecameHardlinkToKeeperBetweenScanAndFix()
    {
        var keeper = Write("keeper.mkv", [1, 2, 3]);
        var loser = Path.Combine(_root, "loser.mkv");
        if (!CreateHardLink(loser, keeper))
        {
            Assert.Fail("Could not create hardlink in test fixture — platform gap, not a repair regression.");
        }

        try
        {
            var issue = new Issue { Type = IssueType.Duplicate, Path = loser, DetailsJson = BuildDupDetails(keeper, loser) };
            var result = await NewFixer().FixAsync(issue, null, CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(File.Exists(loser), "the loser must survive the refusal — recycling would break the hardlink");
            Assert.True(File.Exists(keeper));
        }
        finally
        {
            if (File.Exists(loser)) File.Delete(loser);
        }
    }

    [Fact]
    public async Task DuplicateFixer_RefusesWhenLoserHasExternalHardlinkElsewhere()
    {
        var loser = Write("movie.mkv", [1, 2, 3]);
        var externalLink = Path.Combine(_root, "elsewhere.mkv");
        var keeper = Write("alsokept.mkv", [1, 2, 3]);
        if (!CreateHardLink(externalLink, loser))
        {
            Assert.Fail("Could not create hardlink in test fixture — platform gap.");
        }

        try
        {
            var issue = new Issue { Type = IssueType.Duplicate, Path = loser, DetailsJson = BuildDupDetails(keeper, loser) };
            var result = await NewFixer().FixAsync(issue, null, CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(File.Exists(loser));
            Assert.True(File.Exists(externalLink));
            Assert.True(File.Exists(keeper));
        }
        finally
        {
            if (File.Exists(externalLink)) File.Delete(externalLink);
        }
    }

    [Fact]
    public async Task DuplicateFixer_RefusesWhenLoserBecameSymlink()
    {
        var keeper = Write("keeper.mkv", [1, 2, 3]);
        var loserTarget = Write("something-else.mkv", [1, 2, 3]);
        var loser = Path.Combine(_root, "loser-link.mkv");
        if (!CreateSymlinkFile(loser, loserTarget))
        {
            return;
        }

        try
        {
            var issue = new Issue { Type = IssueType.Duplicate, Path = loser, DetailsJson = BuildDupDetails(keeper, loser) };
            var result = await NewFixer().FixAsync(issue, null, CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(File.Exists(loser));
            Assert.True(File.Exists(loserTarget));
        }
        finally
        {
            try
            {
                File.Delete(loser);
            }
            catch
            {
            }
        }
    }

    [Theory]
    [InlineData(false, 1024)]
    [InlineData(true, 1024)]
    [InlineData(true, 2048)]
    public async Task DuplicateFixer_RefusesReplacementOfApprovedPair(bool replaceKeeper, int replacementSize)
    {
        var keeper = Write("keeper.mkv", new byte[1024]);
        var loser = Write("loser.mkv", new byte[1024]);
        var issue = new Issue { Type = IssueType.Duplicate, Path = loser, DetailsJson = BuildDupDetails(keeper, loser) };
        var replacementBytes = System.Linq.Enumerable.Repeat((byte)73, replacementSize).ToArray();
        var replacement = Write("replacement.mkv", replacementBytes);
        var replacedPath = replaceKeeper ? keeper : loser;
        File.Move(replacement, replacedPath, overwrite: true);

        var result = await NewFixer().FixAsync(issue, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FixReason.SourceChanged, result.Reason);
        Assert.True(File.Exists(loser));
        Assert.Equal(replacementBytes, File.ReadAllBytes(replacedPath));
        Assert.Null(result.RecyclePath);
    }

    [Theory]
    [InlineData("{\"keeperIdentity\":null,\"loserIdentity\":null}")]
    [InlineData("{\"keeperIdentity\":{\"vol\":\"bad\",\"id\":\"1\"},\"loserIdentity\":{\"vol\":\"1\",\"id\":\"2\"}}")]
    [InlineData("{\"keeperIdentity\":{\"vol\":1,\"id\":2},\"loserIdentity\":[]}")]
    public async Task DuplicateFixer_RefusesMissingOrMalformedObservedIdentity(string identities)
    {
        var keeper = Write("keeper.mkv", new byte[1024]);
        var loser = Write("loser.mkv", new byte[1024]);
        using var identityDetails = JsonDocument.Parse(identities);
        var details = JsonSerializer.Serialize(new
        {
            keeperPath = keeper,
            keeperIdentity = identityDetails.RootElement.GetProperty("keeperIdentity"),
            loserIdentity = identityDetails.RootElement.GetProperty("loserIdentity"),
        });
        var result = await NewFixer().FixAsync(new Issue { Type = IssueType.Duplicate, Path = loser, DetailsJson = details }, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(FixReason.SourceChanged, result.Reason);
        Assert.True(File.Exists(loser));
        Assert.True(File.Exists(keeper));
    }

    [Fact]
    public async Task DuplicateFixer_RefusesLegacyApprovalWithoutObservedIdentity()
    {
        var keeper = Write("keeper.mkv", new byte[1024]);
        var loser = Write("loser.mkv", new byte[1024]);
        var details = JsonSerializer.Serialize(new { keeperPath = keeper });
        var result = await NewFixer().FixAsync(new Issue { Type = IssueType.Duplicate, Path = loser, DetailsJson = details }, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(FixReason.SourceChanged, result.Reason);
        Assert.True(File.Exists(loser));
        Assert.True(File.Exists(keeper));
    }

    [Fact]
    public async Task DuplicateFixer_IndependentCopyControl_StillRecyclesLoser()
    {
        // Independent-copy positive control: the fix path must still work for ordinary byte-identical
        // sibling files that are not linked, so the alias guards don't accidentally gate every
        // legitimate duplicate.
        var keeper = Write("keeper.mkv", new byte[1024]);
        var loser = Write("loser.mkv", new byte[1024]);
        var issue = new Issue { Type = IssueType.Duplicate, Path = loser, DetailsJson = BuildDupDetails(keeper, loser) };

        var result = await NewFixer().FixAsync(issue, null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(loser));
        Assert.True(File.Exists(keeper));
        Assert.NotNull(result.RecyclePath);
        Assert.Equal(new byte[1024], File.ReadAllBytes(result.RecyclePath));
        NewRecycleBin().Restore(result.RecyclePath, loser);
        Assert.Equal(new byte[1024], File.ReadAllBytes(loser));
        Assert.False(File.Exists(result.RecyclePath));
    }
}

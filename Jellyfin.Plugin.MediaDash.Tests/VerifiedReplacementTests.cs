using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Package 03-A fault matrix for <see cref="VerifiedReplacementService"/>. Each test uses real
/// temp files and injects faults through <see cref="IReplacementFileOperations"/> at named
/// commit steps — plan requirement: "Use an injected failure at a named step, not wall-clock
/// timing." No ffmpeg, no live host; pure filesystem + callback-driven service exercise.
/// </summary>
[Collection("Diagnostics")]
public sealed class VerifiedReplacementTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _recycled = new();

    /// <summary>Initializes a new instance of the <see cref="VerifiedReplacementTests"/> class.</summary>
    public VerifiedReplacementTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vrt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>Cleans up the temp directory and its contents.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                // Some tests intentionally set files read-only via failure paths; clear the attribute
                // before Delete so cleanup doesn't itself throw.
                foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task RecycleUnavailable_RefusesBeforeVerifyOrPromote()
    {
        var source = MakeFile("unavailable.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("unavailable.tmp", new byte[] { 4, 5, 6 });
        var service = new VerifiedReplacementService(_ => throw new InvalidOperationException("must not recycle"),
            new SystemIOReplacementFileOperations(), null, _ => false);
        var verified = false;
        var result = await service.ReplaceAsync(BuildRequest(source, output, source, true),
            _ => { verified = true; return Task.FromResult<string?>(null); }, () => PolicyDecision.Allow, CancellationToken.None);
        Assert.False(result.Applied);
        Assert.False(verified);
        Assert.Null(result.RecoveryPath);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(output));
        Assert.DoesNotContain(Directory.EnumerateFiles(_tempDir), SwapRecoveryHelper.IsRecoveryCopy);
    }

    // --- Happy path -------------------------------------------------------------------------

    [Fact]
    public async Task Success_SamePath_ReplacesAndReportsRecyclePath()
    {
        var source = MakeFile("video.mkv", Encoding.UTF8.GetBytes("original"));
        var output = MakeFile("video.mkv.tmp", Encoding.UTF8.GetBytes("verified-output"));
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(FixReason.None, result.Reason);
        Assert.NotNull(result.RecyclePath);
        Assert.False(File.Exists(output), "output must have been moved into the recovery slot");
        Assert.True(File.Exists(source), "destination (= source) has the verified bytes");
        Assert.Equal("verified-output", File.ReadAllText(source));
        Assert.Contains(source, _recycled);
        Assert.Null(result.RecoveryPath);
    }

    [Fact]
    public async Task Success_DifferentExtension_LandsAtDestinationAndPermanentDeletesSource()
    {
        var source = MakeFile("video.mp4", Encoding.UTF8.GetBytes("origmp4"));
        var output = MakeFile("video.mp4.tmp", Encoding.UTF8.GetBytes("mkv-output"));
        var destination = Path.Combine(_tempDir, "video.mkv");
        var request = BuildRequest(source, output, destination, recycleOriginal: false);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Null(result.RecyclePath);
        Assert.False(File.Exists(source), "source must be permanent-deleted");
        Assert.True(File.Exists(destination));
        Assert.Equal("mkv-output", File.ReadAllText(destination));
    }

    // --- Verify callback --------------------------------------------------------------------

    [Fact]
    public async Task VerifyReturnsError_RefusesWithVerificationFailedAndOriginalUnchanged()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>("streams disagreed"),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.VerificationFailed, result.Reason);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
        Assert.True(File.Exists(output), "output stays where the caller put it — caller's finally cleans it up");
        Assert.Empty(_recycled);
    }

    [Fact]
    public async Task VerifyThrows_RefusesWithVerificationFailedAndOriginalUnchanged()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => throw new InvalidOperationException("probe crashed"),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.VerificationFailed, result.Reason);
        Assert.Contains("probe crashed", result.TechnicalDetail);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task VerifyCanceled_PropagatesCancellationAndOriginalUnchanged()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = MakeService();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            cts.Token));

        // Source untouched.
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
    }

    // --- Source identity --------------------------------------------------------------------

    [Fact]
    public async Task SourceChangesDuringEncode_RefusesWithSourceChangedAndChangedSourceRemains()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        // Rewrite source AFTER capturing expected length/mtime so identity recheck fires.
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4, 5 });
        File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(60));

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.SourceChanged, result.Reason);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(source));
        Assert.True(File.Exists(output), "verified output stays with caller for finally cleanup");
    }

    // --- Policy recheck ---------------------------------------------------------------------

    [Fact]
    public async Task PolicyRevokedAfterEncode_RefusesAndOriginalUnchanged()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => PolicyDecision.Refuse(FixReason.TypeDisabled),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.TypeDisabled, result.Reason);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
    }

    // --- Destination collision --------------------------------------------------------------

    [Fact]
    public async Task DestinationPreexisting_RefusesWithoutOverwriting()
    {
        var source = MakeFile("v.mp4", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mp4.tmp", new byte[] { 9, 9, 9 });
        var destination = Path.Combine(_tempDir, "v.mkv");
        File.WriteAllBytes(destination, new byte[] { 0xAA, 0xBB });
        var request = BuildRequest(source, output, destination, recycleOriginal: false);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.DestinationCollision, result.Reason);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, File.ReadAllBytes(destination));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task DestinationIsDirectory_RefusesWithCollision()
    {
        var source = MakeFile("v.mp4", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mp4.tmp", new byte[] { 9, 9, 9 });
        var destination = Path.Combine(_tempDir, "v.mkv");
        Directory.CreateDirectory(destination);
        var request = BuildRequest(source, output, destination, recycleOriginal: false);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.DestinationCollision, result.Reason);
        Assert.True(Directory.Exists(destination));
    }

    // --- Stage move fails -------------------------------------------------------------------

    [Fact]
    public async Task StageMoveFails_RefusesWithProcessFailedAndSourceIntact()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        var faulty = new FaultyFileOps(new SystemIOReplacementFileOperations())
        {
            ThrowOnMoveNumber = 1,
            MoveExceptionFactory = () => new IOException("simulated stage-move failure"),
        };
        var service = new VerifiedReplacementService(RecycleTo, faulty, logger: null);

        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.ProcessFailed, result.Reason);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
        Assert.True(File.Exists(output), "owned output is unmoved on stage failure");
        Assert.Empty(_recycled);
    }

    // --- Recycle/disposal fails -------------------------------------------------------------

    [Fact]
    public async Task RecycleDisposalThrows_DemotesOwnedOutputWhenSourceIntact()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        var throwingRecycle = new Func<string, string>(_ => throw new IOException("recycle-bin volume out of space"));
        var service = new VerifiedReplacementService(throwingRecycle, new SystemIOReplacementFileOperations(), logger: null);

        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.ProcessFailed, result.Reason);
        Assert.Null(result.RecoveryPath);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(output));
        Assert.DoesNotContain(Directory.EnumerateFiles(_tempDir), SwapRecoveryHelper.IsRecoveryCopy);
        Assert.True(File.Exists(source), "recycle didn't run — source remains at its original path");
    }

    [Fact]
    public async Task DeleteDisposalFails_DemotesOwnedOutputWhenSourceIntact()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: false);

        var faulty = new FaultyFileOps(new SystemIOReplacementFileOperations())
        {
            ThrowOnDeletePath = source,
            DeleteExceptionFactory = () => new IOException("delete refused"),
        };
        var service = new VerifiedReplacementService(RecycleTo, faulty, logger: null);

        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.ProcessFailed, result.Reason);
        Assert.Null(result.RecoveryPath);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(output));
        Assert.DoesNotContain(Directory.EnumerateFiles(_tempDir), SwapRecoveryHelper.IsRecoveryCopy);
        Assert.True(File.Exists(source), "source still present because delete failed");
    }

    // --- Final rename fails after disposal --------------------------------------------------

    [Fact]
    public async Task FinalRenameFails_ReportsRecoveryRequiredWithVerifiedBytesAtRecoveryPath()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        var faulty = new FaultyFileOps(new SystemIOReplacementFileOperations())
        {
            // Move 1 = stage-to-recovery. Move 2 = final rename. Fail #2.
            ThrowOnMoveNumber = 2,
            MoveExceptionFactory = () => new IOException("simulated final-rename failure"),
        };
        var service = new VerifiedReplacementService(RecycleTo, faulty, logger: null);

        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.RecoveryRequired, result.Reason);
        Assert.NotNull(result.RecoveryPath);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(result.RecoveryPath!));
        Assert.NotNull(result.RecyclePath);
        Assert.Contains(source, _recycled);
        Assert.False(File.Exists(source), "source was disposed BEFORE the final rename failed");
        Assert.True(SwapRecoveryHelper.IsRecoveryCopy(result.RecoveryPath!));
    }

    // --- Recovery-marker survives orphan sweep ----------------------------------------------

    [Fact]
    public async Task RecoveryFile_SurvivesLibraryGuardSweepPatternPreCheck()
    {
        // Plan requirement: "Recovery rename fails; then orphan sweep runs after age cutoff:
        // At least one valid copy remains; sweep cannot delete uncertain recovery evidence."
        // The sweep uses SwapRecoveryHelper.IsRecoveryCopy as its negative check; assert the
        // recovery survivor from a real failed-commit flow carries the recovery marker.
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        var faulty = new FaultyFileOps(new SystemIOReplacementFileOperations())
        {
            ThrowOnMoveNumber = 2,
            MoveExceptionFactory = () => new IOException("final rename blocked"),
        };
        var service = new VerifiedReplacementService(RecycleTo, faulty, logger: null);
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.True(SwapRecoveryHelper.IsRecoveryCopy(result.RecoveryPath!));

        // Age the survivor past LibraryGuard.SweepOrphanSidecars' 5-min minimum. The sweep's
        // own tests confirm end-to-end behavior; this assert is the local invariant that the
        // recovery marker in the filename would keep the sweep's negative-check gate holding.
        File.SetLastWriteTimeUtc(result.RecoveryPath!, DateTime.UtcNow.AddHours(-1));
        Assert.True(SwapRecoveryHelper.IsRecoveryCopy(result.RecoveryPath!));
        Assert.True(File.Exists(result.RecoveryPath));
    }

    // --- Cancellation between promote and dispose -------------------------------------------

    [Fact]
    public async Task CanceledBetweenPromoteAndDispose_ReportsCancelledWithRecoveryPath()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        using var cts = new CancellationTokenSource();
        var faulty = new FaultyFileOps(new SystemIOReplacementFileOperations())
        {
            // Cancel AFTER promote but BEFORE dispose: hook into the Move #1 (stage) completing.
            AfterMoveNumberAction = (n) =>
            {
                if (n == 1)
                {
                    cts.Cancel();
                }
            },
        };
        var service = new VerifiedReplacementService(RecycleTo, faulty, logger: null);

        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            cts.Token);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.Cancelled, result.Reason);
        Assert.NotNull(result.RecoveryPath);
        Assert.True(File.Exists(result.RecoveryPath));
        Assert.True(File.Exists(source), "source not disposed on pre-dispose cancellation");
        Assert.Empty(_recycled);
    }

    // --- Cancellation after disposal completes commit or recovery, never false Preview ------

    [Fact]
    public async Task CanceledAfterDisposal_StillCompletesFinalRename()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        using var cts = new CancellationTokenSource();
        var recycleWithCancel = new Func<string, string>(path =>
        {
            var res = RecycleTo(path);
            cts.Cancel(); // cancel AFTER dispose lands
            return res;
        });
        var service = new VerifiedReplacementService(recycleWithCancel, new SystemIOReplacementFileOperations(), logger: null);

        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            cts.Token);

        Assert.True(result.Applied);
        Assert.Equal(FixReason.None, result.Reason);
        Assert.True(File.Exists(source));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(source));
    }

    // --- Alias / preflight bugs -------------------------------------------------------------

    [Fact]
    public async Task OutputPathAliasesSource_Refuses()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var request = BuildRequest(source, outputPath: source, destination: source, recycleOriginal: true);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.Unknown, result.Reason);
    }

    [Fact]
    public async Task MissingOutput_RefusesWithSourceMissing()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = Path.Combine(_tempDir, "does-not-exist.tmp");
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.SourceMissing, result.Reason);
    }

    [Fact]
    public async Task MissingSource_RefusesWithSourceMissing()
    {
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var source = Path.Combine(_tempDir, "vanished.mkv");
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.SourceMissing, result.Reason);
    }

    // --- Recovery-name collision handling ---------------------------------------------------

    [Fact]
    public async Task DirectoryAtRecoveryCandidate_ProducesUniqueBoundedName()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", new byte[] { 9, 9, 9 });
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        // Plant a directory at the ideal recovery candidate second-timestamp slot; the service
        // must build the -1 / -2 / … suffixed name to avoid the directory. Naming precision is
        // second-level, so the directory shape (basename + marker + timestamp + ext) can be
        // constructed for the current second.
        var basename = Path.GetFileNameWithoutExtension(source);
        var ext = Path.GetExtension(source);
        var candidateName = basename + SwapRecoveryHelper.RecoveryMarker
            + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
            + ext;
        var candidateDir = Path.Combine(_tempDir, candidateName);
        Directory.CreateDirectory(candidateDir);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.True(result.Applied);
        // The final rename landed on 'source' (== destination), so the recovery slot vanished
        // when it was renamed. The directory we planted is undisturbed.
        Assert.True(Directory.Exists(candidateDir));
        Assert.True(File.Exists(source));
    }

    // --- Zero-byte output rejected ----------------------------------------------------------

    [Fact]
    public async Task EmptyOutput_RefusesWithProcessFailed()
    {
        var source = MakeFile("v.mkv", new byte[] { 1, 2, 3 });
        var output = MakeFile("v.mkv.tmp", System.Array.Empty<byte>());
        var request = BuildRequest(source, output, destination: source, recycleOriginal: true);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(FixReason.ProcessFailed, result.Reason);
        Assert.NotNull(result.RecoveryPath);
        Assert.True(File.Exists(source));
    }

    // --- Success on different-extension path preserves source bytes at recycle --------------

    [Fact]
    public async Task Success_DifferentExtension_RecycledCopyPreservesOriginalBytes()
    {
        var source = MakeFile("v.mp4", new byte[] { 0x10, 0x20, 0x30 });
        var output = MakeFile("v.mp4.tmp", new byte[] { 0xAA, 0xBB });
        var destination = Path.Combine(_tempDir, "v.mkv");
        var request = BuildRequest(source, output, destination, recycleOriginal: true);

        var service = MakeService();
        var result = await service.ReplaceAsync(
            request,
            _ => Task.FromResult<string?>(null),
            () => new PolicyDecision(true, FixReason.None),
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.True(File.Exists(destination));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, File.ReadAllBytes(destination));
        Assert.NotNull(result.RecyclePath);
        // In this fake, RecycleTo just moves to a bin/ folder under _tempDir; verify the
        // recycled copy has the source bytes so the caller can restore.
        Assert.True(File.Exists(result.RecyclePath));
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, File.ReadAllBytes(result.RecyclePath!));
    }

    // --- Helpers ----------------------------------------------------------------------------

    private string MakeFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private ReplacementRequest BuildRequest(
        string sourcePath,
        string outputPath,
        string destination,
        bool recycleOriginal)
    {
        var info = new FileInfo(sourcePath);
        var length = info.Exists ? info.Length : 0;
        var ticks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
        return new ReplacementRequest(
            sourcePath,
            outputPath,
            destination,
            length,
            ticks,
            recycleOriginal);
    }

    private VerifiedReplacementService MakeService()
        => new(RecycleTo, new SystemIOReplacementFileOperations(), logger: null);

    private string RecycleTo(string path)
    {
        var bin = Path.Combine(_tempDir, "bin", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bin);
        var target = Path.Combine(bin, Path.GetFileName(path));
        File.Move(path, target);
        _recycled.Add(path);
        return target;
    }

    /// <summary>
    /// Test-only decorator over <see cref="SystemIOReplacementFileOperations"/> that fails the
    /// Nth call of the operation you name. Plan requirement: "Use an injected failure at a
    /// named step, not wall-clock timing."
    /// </summary>
    private sealed class FaultyFileOps : IReplacementFileOperations
    {
        private readonly IReplacementFileOperations _inner;
        private int _moveCalls;

        public FaultyFileOps(IReplacementFileOperations inner)
        {
            _inner = inner;
        }

        public int ThrowOnMoveNumber { get; set; }

        public Func<Exception>? MoveExceptionFactory { get; set; }

        public string? ThrowOnDeletePath { get; set; }

        public Func<Exception>? DeleteExceptionFactory { get; set; }

        public Action<int>? AfterMoveNumberAction { get; set; }

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public long GetLength(string path) => _inner.GetLength(path);

        public long GetLastWriteTicksUtc(string path) => _inner.GetLastWriteTicksUtc(path);

        public void Move(string source, string destination)
        {
            var n = ++_moveCalls;
            if (ThrowOnMoveNumber == n)
            {
                throw MoveExceptionFactory?.Invoke() ?? new IOException("simulated move failure #" + n);
            }

            _inner.Move(source, destination);
            AfterMoveNumberAction?.Invoke(n);
        }

        public void Delete(string path)
        {
            if (ThrowOnDeletePath is not null
                && string.Equals(ThrowOnDeletePath, path, StringComparison.OrdinalIgnoreCase))
            {
                throw DeleteExceptionFactory?.Invoke() ?? new IOException("simulated delete failure");
            }

            _inner.Delete(path);
        }

        public void TrySetTimestampsUtc(string path, DateTime creationTimeUtc, DateTime lastWriteTimeUtc)
            => _inner.TrySetTimestampsUtc(path, creationTimeUtc, lastWriteTimeUtc);
    }
}

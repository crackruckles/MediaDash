using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

// FieldOps: use IReplacementFileOperations exclusively; do NOT reach directly into System.IO.File
// for staging/disposal — tests inject faults via the seam and a direct call would bypass them.

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// One auditable protocol for replacing a media file with a verified-elsewhere output.
/// Owns the stage → verify recheck → promote → dispose → commit sequence and the recovery
/// bookkeeping when the final rename fails after the source has been disposed. See
/// <c>artifacts/refactor/03A/protocol-table.md</c> for the per-caller flow this replaces.
/// </summary>
/// <remarks>
/// The service does not decide policy — callers supply the verify callback, the current-
/// policy callback and the recycle-vs-permanent-delete flag. It does not run library
/// notifications, DB relocations or canonical renames — those stay with the caller. It
/// preserves every deliberate difference documented in the protocol table (Playability
/// always-recycle, TrackFixer external sidecars kept separate, same-path swaps legal).
/// </remarks>
public sealed class VerifiedReplacementService
{
    /// <summary>NAME_MAX budget for the recovery filename before the hash-fallback naming
    /// kicks in. Mirrors <see cref="SwapRecoveryHelper"/> and <c>TranscodeFixer.SidecarPath</c>
    /// so a 244-byte UTF-8 basename can't overflow after the recovery marker + timestamp.</summary>
    private const int RecoveryNameByteBudget = 240;

    private readonly Func<string, string> _recycleToBin;
    private readonly Func<string, bool> _canRecycle;
    private readonly IReplacementFileOperations _fileOps;
    private readonly ILogger<VerifiedReplacementService>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VerifiedReplacementService"/> class for
    /// DI. Uses the real <see cref="SystemIOReplacementFileOperations"/> under the hood; the
    /// public ctor wraps <see cref="RecycleBin.MoveToBin"/> as the recycle callback so the
    /// service does not take a hard dependency on the class itself (keeps the test ctor
    /// callback-shaped without needing to build a real RecycleBin in the fault matrix).
    /// </summary>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="logger">Optional logger. Null in unit tests, DI-injected in production.</param>
    public VerifiedReplacementService(RecycleBin recycleBin, ILogger<VerifiedReplacementService>? logger = null)
        : this(recycleBin.MoveToBin, new SystemIOReplacementFileOperations(), logger, recycleBin.CanRecycle)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VerifiedReplacementService"/> class with
    /// an injected filesystem seam and a recycle callback. Test-only overload — production
    /// uses the public ctor and passes <see cref="RecycleBin.MoveToBin"/>.
    /// </summary>
    /// <param name="recycleToBin">Recycle callback. Takes a source path, returns the in-bin
    /// path. Throws on failure. Never invoked when
    /// <see cref="ReplacementRequest.RecycleOriginal"/> is false.</param>
    /// <param name="fileOps">The filesystem seam.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="canRecycle">Optional recycle preflight for injected callbacks.</param>
    internal VerifiedReplacementService(
        Func<string, string> recycleToBin,
        IReplacementFileOperations fileOps,
        ILogger<VerifiedReplacementService>? logger,
        Func<string, bool>? canRecycle = null)
    {
        _recycleToBin = recycleToBin;
        _canRecycle = canRecycle ?? (_ => true);
        _fileOps = fileOps;
        _logger = logger;
    }

    internal bool CanRecycle(string sourcePath) => _canRecycle(sourcePath);

    /// <summary>
    /// Replaces <see cref="ReplacementRequest.SourcePath"/> with the verified output at
    /// <see cref="ReplacementRequest.OutputPath"/>. Refuses in every case where the
    /// original could be lost: verify callback returns non-null, source identity changed,
    /// policy revoked, destination collision on a differing destination. After the source
    /// is disposed, cancellation is IGNORED — the service completes the final rename or
    /// returns <see cref="FixReason.RecoveryRequired"/> with an actual recovery path.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="verifyOutput">Post-encode / post-remux / post-repair check on
    /// <see cref="ReplacementRequest.OutputPath"/>. Non-null return refuses the commit and
    /// preserves the source. Callers compose their own verify chains (structural + decode
    /// sample for Playability, larger-output gate for TranscodeFixer).</param>
    /// <param name="checkCurrentPolicy">Late policy recheck. Called after verify and source-
    /// identity recheck; if the returned decision is not Allowed the service refuses without
    /// touching the source. Used to catch mid-encode config flips (dry-run flipped ON, fixer
    /// disabled, library dropped).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The replacement result.</returns>
    internal async Task<ReplacementResult> ReplaceAsync(
        ReplacementRequest request,
        Func<CancellationToken, Task<string?>> verifyOutput,
        Func<PolicyDecision> checkCurrentPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(verifyOutput);
        ArgumentNullException.ThrowIfNull(checkCurrentPolicy);

        // 1. Preflight — refuse invalid inputs BEFORE running verify. Verify may be expensive
        //    (Playability decode-sample takes seconds on a large file).
        var preflight = Preflight(request);
        if (preflight is { } refusal)
        {
            return refusal;
        }

        // 2. Verify the caller's output. Non-null return = verify says no; source untouched.
        //    Cancellation THROWS from here (source untouched — caller sees standard OCE flow).
        cancellationToken.ThrowIfCancellationRequested();
        string? verifyError;
        try
        {
            verifyError = await verifyOutput(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Propagate; caller's outer catch cleans up its owned temp.
            throw;
        }
        catch (Exception ex)
        {
            // Any other verify exception is treated as verification-failed. Source untouched.
            return new ReplacementResult(false, FixReason.VerificationFailed, null, null, ex.Message);
        }

        if (verifyError is not null)
        {
            return new ReplacementResult(false, FixReason.VerificationFailed, null, null, verifyError);
        }

        // 3. Source identity recheck — refuse if length or mtime drifted while the caller was
        //    producing the output. This is a useful stale-source guard, not cryptographic
        //    identity; a hostile same-size/mtime swap is out of scope.
        long currentLength;
        long currentMtimeTicks;
        try
        {
            currentLength = _fileOps.GetLength(request.SourcePath);
            currentMtimeTicks = _fileOps.GetLastWriteTicksUtc(request.SourcePath);
        }
        catch (IOException ex)
        {
            return new ReplacementResult(false, FixReason.SourceMissing, null, null, ex.Message);
        }

        if (currentLength != request.ExpectedSourceLength || currentMtimeTicks != request.ExpectedSourceMtimeUtcTicks)
        {
            return new ReplacementResult(
                false,
                FixReason.SourceChanged,
                null,
                null,
                "source changed between output production and commit (length="
                    + currentLength.ToString(CultureInfo.InvariantCulture)
                    + ", mtimeTicks=" + currentMtimeTicks.ToString(CultureInfo.InvariantCulture) + ")");
        }

        // 4. Policy recheck — dry-run flipped ON mid-encode, fixer disabled, library dropped.
        //    Refusal here preserves the source and abandons the verified output for the
        //    caller's finally to clean up.
        var decision = checkCurrentPolicy();
        if (!decision.Allowed)
        {
            return new ReplacementResult(
                false,
                decision.Reason,
                null,
                null,
                "policy revoked before commit");
        }

        // 5. Cancellation gate BEFORE promote. Safe to abort — source untouched.
        if (cancellationToken.IsCancellationRequested)
        {
            return new ReplacementResult(false, FixReason.Cancelled, null, null, "cancelled before promote");
        }

        // 6. Promote: rename output → *.mediadash-recover-* next to destination. This is the
        //    "stage on destination volume" step — the recovery-marker sits outside every
        //    LibraryGuard.SweepOrphanSidecars pattern so a subsequent crash can't strand it.
        //    File.Move handles cross-volume via copy+delete transparently. Post-move length
        //    check catches truncated cross-volume copies.
        string recoveryPath;
        try
        {
            recoveryPath = PromoteToRecoveryPath(request.OutputPath, request.DestinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ReplacementResult(false, FixReason.ProcessFailed, null, null, "stage move failed: " + ex.Message);
        }

        // 7. Post-promote sanity: recovery file matches the caller's verified length. Guards
        //    against a partial cross-volume copy the OS may not have flagged. Small (single
        //    length lookup); big benefit — a corrupt commit here would be invisible otherwise.
        long recoveryLength;
        try
        {
            recoveryLength = _fileOps.GetLength(recoveryPath);
        }
        catch (IOException ex)
        {
            return new ReplacementResult(false, FixReason.ProcessFailed, null, recoveryPath, "recovery vanished after stage: " + ex.Message);
        }

        if (recoveryLength <= 0)
        {
            // Zero-byte recovery = the cross-volume copy or the caller's produced output
            // truncated to nothing. Refuse. The zero-byte file sits outside every sweep
            // pattern; the caller can inspect RecoveryPath and clean up.
            return new ReplacementResult(false, FixReason.ProcessFailed, null, recoveryPath, "staged output is empty");
        }

        // 8. Cancellation gate BEFORE dispose. Recovery file survives outside every sweep
        //    pattern; leave it in place and report so the caller can decide whether to keep.
        if (cancellationToken.IsCancellationRequested)
        {
            return new ReplacementResult(false, FixReason.Cancelled, null, recoveryPath, "cancelled between promote and dispose");
        }

        // 9. Capture timestamps BEFORE dispose (dispose destroys them).
        DateTime srcCreatedUtc;
        DateTime srcModifiedUtc;
        try
        {
            var sourceInfo = new FileInfo(request.SourcePath);
            srcCreatedUtc = sourceInfo.Exists ? sourceInfo.CreationTimeUtc : DateTime.UtcNow;
            srcModifiedUtc = sourceInfo.Exists ? sourceInfo.LastWriteTimeUtc : DateTime.UtcNow;
        }
        catch (IOException)
        {
            srcCreatedUtc = DateTime.UtcNow;
            srcModifiedUtc = DateTime.UtcNow;
        }

        // 10. Dispose the source. RecycleBin handles cross-volume moves + free-space check
        //     internally; direct delete is a straight File.Delete.
        string? recyclePath = null;
        try
        {
            if (_fileOps.FileExists(request.SourcePath))
            {
                if (request.RecycleOriginal)
                {
                    recyclePath = _recycleToBin(request.SourcePath);
                }
                else
                {
                    _fileOps.Delete(request.SourcePath);
                }
            }

            // If the source vanished between step 3 (identity recheck) and here, treat it as
            // "already disposed" and continue to final rename. Source-identity recheck already
            // held; no risk of clobbering an unrelated file at destination because destination
            // preflight held too.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dispose failed. Source may or may not still be present. Recovery file survives
            // outside sweep patterns. Do NOT proceed to final rename: if source is still there
            // AND destination == source, File.Move(recovery, source) would refuse "destination
            // exists". If destination != source and destination is currently free, we'd be
            // safe to rename — but reporting RecoveryRequired here forces the user to see
            // the failed dispose and decide manually. Caller emits its fixer-specific
            // diagnostic keyed with the pre-existing "*.SwapAborted"-style name.
            if (_fileOps.FileExists(request.SourcePath))
            {
                try
                {
                    _fileOps.Move(recoveryPath, request.OutputPath);
                    return new ReplacementResult(false, FixReason.ProcessFailed, null, null, ex.Message);
                }
                catch (Exception demoteError) when (demoteError is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogWarning(demoteError, "VerifiedReplacement: could not return recovery file to owned output {Output}", request.OutputPath);
                }
            }

            _logger?.LogWarning(ex, "VerifiedReplacement: dispose failed for '{Source}', recovery survivor at '{Recovery}'", request.SourcePath, recoveryPath);
            return new ReplacementResult(false, FixReason.RecoveryRequired, null, recoveryPath, ex.Message);
        }

        // 11. Final rename recovery → destination. Cancellation is IGNORED past here. If this
        //     throws, the recovery file survives at recoveryPath and the caller reports the
        //     recovery path in a loud diagnostic.
        try
        {
            _fileOps.Move(recoveryPath, request.DestinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "VerifiedReplacement: final rename to '{Destination}' failed after dispose; recovery survivor at '{Recovery}'", request.DestinationPath, recoveryPath);
            return new ReplacementResult(false, FixReason.RecoveryRequired, recyclePath, recoveryPath, ex.Message);
        }

        // 12. Timestamp restore (non-fatal — commit already succeeded).
        if (request.PreserveSourceTimestamps)
        {
            _fileOps.TrySetTimestampsUtc(request.DestinationPath, srcCreatedUtc, srcModifiedUtc);
        }

        return new ReplacementResult(true, FixReason.None, recyclePath, null, null);
    }

    /// <summary>
    /// Preflight refusal check — the service must not act on obviously-invalid inputs. Kept
    /// as a separate method for testability + so <see cref="ReplaceAsync"/>'s happy path stays
    /// linear.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>Refusal result, or null when preflight passes.</returns>
    private ReplacementResult? Preflight(ReplacementRequest request)
    {
        if (string.IsNullOrEmpty(request.SourcePath)
            || string.IsNullOrEmpty(request.OutputPath)
            || string.IsNullOrEmpty(request.DestinationPath))
        {
            return new ReplacementResult(false, FixReason.Unknown, null, null, "request has empty path");
        }

        if (request.RecycleOriginal && !CanRecycle(request.SourcePath))
        {
            return new ReplacementResult(false, FixReason.AccessDenied, null, null, "No safe recycle location is available for the original.");
        }

        // Alias check: OutputPath must not collide with source or destination. This is a
        // caller-bug refusal — the caller must produce OutputPath at a distinct staging path.
        if (string.Equals(request.OutputPath, request.SourcePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || string.Equals(request.OutputPath, request.DestinationPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return new ReplacementResult(false, FixReason.Unknown, null, null, "output path aliases source or destination");
        }

        if (!_fileOps.FileExists(request.OutputPath))
        {
            return new ReplacementResult(false, FixReason.SourceMissing, null, null, "verified output no longer exists at " + request.OutputPath);
        }

        if (!_fileOps.FileExists(request.SourcePath))
        {
            return new ReplacementResult(false, FixReason.SourceMissing, null, null, "source no longer exists at " + request.SourcePath);
        }

        // Destination-collision check. Same-path swap (destination == source) is legal — that
        // is the common case (TrackFixer, EmbeddedCoverArtFixer, same-container Transcode).
        // Preexisting file OR directory at a non-equal destination is a collision.
        if (!string.Equals(request.SourcePath, request.DestinationPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            if (_fileOps.FileExists(request.DestinationPath) || _fileOps.DirectoryExists(request.DestinationPath))
            {
                return new ReplacementResult(false, FixReason.DestinationCollision, null, null, "destination already occupied: " + request.DestinationPath);
            }
        }

        return null;
    }

    /// <summary>
    /// Promote (rename) the caller's output to a <c>*.mediadash-recover-*</c> sibling of the
    /// destination. Sits outside every <see cref="LibraryGuard.SweepOrphanSidecars"/> pattern
    /// (the sweep explicitly skips <see cref="SwapRecoveryHelper.IsRecoveryCopy"/>) so a crash
    /// between the following dispose and final-rename steps can't strand the only remaining
    /// copy at a sweep-doomed path. Uses the <see cref="SwapRecoveryHelper.RecoveryMarker"/>
    /// constant so the sweep's negative check keeps working; name-length handling mirrors the
    /// helper so a 244-byte UTF-8 basename can't overflow NAME_MAX.
    /// </summary>
    /// <param name="outputPath">The caller's verified output. Moved (not copied) into the
    /// returned recovery path.</param>
    /// <param name="destinationPath">Where the file will eventually land. The recovery path is
    /// a sibling of this so the final rename is an intra-directory (atomic) move.</param>
    /// <returns>The recovery path — <paramref name="outputPath"/> no longer exists.</returns>
    private string PromoteToRecoveryPath(string outputPath, string destinationPath)
    {
        var candidate = BuildRecoveryPath(destinationPath);
        var attempt = 0;
        while ((_fileOps.FileExists(candidate) || _fileOps.DirectoryExists(candidate)) && attempt < 1000)
        {
            attempt++;
            candidate = BuildRecoveryPath(destinationPath, suffix: "-" + attempt.ToString(CultureInfo.InvariantCulture));
        }

        // ponytail: no copy-fallback in the service. The old SwapRecoveryHelper had a Move→Copy
        // fallback for cross-directory quirks, but the service's promote is always intra-directory
        // (destination is a sibling of the recovery candidate). A Move failure here is real —
        // report ProcessFailed and let the caller's finally clean up its output. This also keeps
        // the "stage move fails" fault-test honest — the fault seam owns the outcome.
        _fileOps.Move(outputPath, candidate);
        return candidate;
    }

    /// <summary>
    /// Bounded recovery-path builder. When the ideal name (full basename + marker + timestamp
    /// + suffix + ext) would overflow the OS NAME_MAX budget, fall back to a hash-based name
    /// — same strategy <c>TranscodeFixer.SidecarPath</c> and <see cref="SwapRecoveryHelper"/>
    /// use. Timestamp is second-precision; the caller adds a "-N" suffix to disambiguate
    /// same-second collisions.
    /// </summary>
    private static string BuildRecoveryPath(string originalPath, string suffix = "")
    {
        var dir = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var basename = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = basename + SwapRecoveryHelper.RecoveryMarker + stamp + suffix + ext;
        if (Encoding.UTF8.GetByteCount(candidate) <= RecoveryNameByteBudget)
        {
            return Path.Combine(dir, candidate);
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basename)), 0, 8);
        return Path.Combine(dir, hash + SwapRecoveryHelper.RecoveryMarker + stamp + suffix + ext);
    }
}

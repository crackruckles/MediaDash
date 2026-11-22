using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Removes files that cannot be played — but only after re-verifying at fix time that the file is still broken.
/// A file that probes and decodes cleanly is never removed, whatever the scan said.
/// </summary>
public sealed class PlayabilityFixer : IFixer
{
    private static readonly TimeSpan RepairRungTimeout = TimeSpan.FromHours(6);

    private readonly FfprobeService _ffprobe;
    private readonly FfmpegExecutor _ffmpeg;
    private readonly OutputVerifier _verifier;
    private readonly LibraryGuard _guard;
    private readonly RecycleBin _recycleBin;
    private readonly VerifiedReplacementService _replacement;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILibraryManager _libraryManager;
    // External audit P1 #4: commit-time protection check so a Never-modify rule added during
    // a 30-minute repair aborts the swap. Nullable because older test fixtures construct the
    // fixer without protection wired; production DI always supplies it.
    private readonly ProtectionService? _protection;
    private readonly ILogger<PlayabilityFixer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayabilityFixer"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="ffmpeg">The ffmpeg executor (repair ladder).</param>
    /// <param name="verifier">The output verifier (repair ladder).</param>
    /// <param name="guard">The library path guard.</param>
    /// <param name="recycleBin">The recycle bin. Still used for the "no repair worked"
    /// delete-only path (no staged output); the repair-swap path routes through
    /// <see cref="VerifiedReplacementService"/>.</param>
    /// <param name="replacement">03-C: the shared verify → promote → dispose → commit protocol.
    /// Repair swaps ALWAYS pass <c>RecycleOriginal: true</c> regardless of the general failed-
    /// repair disposal setting — the always-recycle-repaired-original invariant is preserved.</param>
    /// <param name="configProvider">02-A snapshot provider. Used for the pre-commit policy
    /// recheck on repair-ladder commits so a dry-run flip mid-ffmpeg aborts before touching the
    /// source.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="libraryManager">Jellyfin library manager. Used to actively purge the Jellyfin
    /// item row when the fixer removes a file — <see cref="ILibraryMonitor.ReportFileSystemChanged"/>
    /// is async/debounced and its pending batch is lost on any Jellyfin restart, which leaves
    /// ghost items behind and causes MediaDash's next scan to flag the (now file-less) items as
    /// unplayable.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="protection">Protection service for commit-time re-checks (external audit P1 #4); DI auto-wires it.</param>
    public PlayabilityFixer(
        FfprobeService ffprobe,
        FfmpegExecutor ffmpeg,
        OutputVerifier verifier,
        LibraryGuard guard,
        RecycleBin recycleBin,
        VerifiedReplacementService replacement,
        IPluginConfigurationProvider configProvider,
        ILibraryMonitor libraryMonitor,
        ILibraryManager libraryManager,
        ILogger<PlayabilityFixer> logger,
        ProtectionService? protection = null)
    {
        _ffprobe = ffprobe;
        _ffmpeg = ffmpeg;
        _verifier = verifier;
        _guard = guard;
        _recycleBin = recycleBin;
        _replacement = replacement;
        _configProvider = configProvider;
        _libraryMonitor = libraryMonitor;
        _libraryManager = libraryManager;
        _protection = protection;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.Playability;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!File.Exists(issue.Path))
        {
            // The file was already removed (usually by an earlier MediaDash fix). Purge the ghost
            // Jellyfin library item so a rescan can't re-flag the same dead path — otherwise the
            // scanner walks Jellyfin's library items, finds no file on disk, and emits a fresh
            // Playability row that looks to the user like "everything came back". Reporting Applied
            // (bytes 0) so History reflects the real cleanup work performed.
            TryReapJellyfinItem(issue);
            _libraryMonitor.ReportFileSystemChanged(issue.Path);
            return new FixResult
            {
                Success = true,
                Outcome = FixOutcome.Applied,
                Message = "Removed dead library entry (file was already gone).",
                BytesFreed = 0
            };
        }

        if (!_guard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "The file is outside your library folders; MediaDash will not touch it.");
        }

        // 01-C: route the persisted details through the shared IssueDetailsReader boundary.
        // A malformed / non-object payload returns (false, null); we default fixReason to null
        // so the fixer falls into the safe legacy branch that treats an unknown reason as a
        // plain playability failure. Parsing success is a schema check, not authorization —
        // library and source-role guards below still fire on every path.
        IssueDetailsReader.TryReadPlayability(issue.DetailsJson, out var playabilityDetails);
        var fixReason = playabilityDetails?.Reason;

        // DRM-protected files: no repair path can succeed and we don't want to recycle a file
        // the user still values. Refuse cleanly BEFORE IsStillBrokenAsync — the default probe
        // check finds a video stream + duration on a DRM file and would return "not broken",
        // which would incorrectly mask this branch.
        if (string.Equals(fixReason, "drm-protected", StringComparison.Ordinal))
        {
            return FixResult.Refused(FixReason.DrmProtected, "This file is DRM-protected — MediaDash cannot decrypt or repair it. Use the Ignore action to hide the notification, or remove the file manually.");
        }

        // MD-010: revalidate is tri-state. Indeterminate ⇒ ffprobe couldn't run (missing exe,
        // timeout, process-start failure, etc.). Infrastructure failure is NOT evidence the file
        // is broken; failing closed here protects healthy files from irreversible disposal when
        // ffprobe temporarily disappears (Jellyfin update, container mount hiccup, PATH change).
        // Only Broken proceeds to repair/disposal; the probe object is reused across both to make
        // sure infrastructure state can't change between contradictory probes.
        var revalidation = await RevalidateAsync(issue, cancellationToken).ConfigureAwait(false);
        switch (revalidation.Verdict)
        {
            case RevalidationVerdict.Indeterminate:
                Api.Diagnostics.Record("PlayabilityFixer.RevalidateIndeterminate", "Refused fix for " + issue.Path + ": " + (revalidation.Diagnostic ?? "revalidation returned indeterminate."));
                // E2E audit E2E-06: previously every Indeterminate verdict rendered the generic
                // "ffprobe didn't respond" copy, which falsely blamed the FFmpeg install for files
                // that are simply not valid media containers. The RevalidationResult diagnostic now
                // carries a plain-language reason for each sub-case; surface it directly.
                return FixResult.Fail(FixReason.ProbeUnavailable, revalidation.Diagnostic ?? "Can't safely check this file right now. No fix was applied.");
            case RevalidationVerdict.Healthy:
                return FixResult.NoChange(FixReason.AlreadyHealthy, "The file plays fine now — nothing was removed. Re-scan to clear this issue.");
        }

        // Container-extension mismatch is a hygiene issue — rename the file, no repair, no delete.
        // Split out from the repair ladder because these files ARE playable, just misnamed.
        if (string.Equals(fixReason, "container-extension-mismatch", StringComparison.Ordinal))
        {
            return await RenameForMismatchAsync(issue, playabilityDetails?.RenameToExtension).ConfigureAwait(false);
        }

        // E2E audit E2E-08: broken books/comics (PDF, EPUB, CBZ, MOBI, CBR, CB7) are detected by
        // the Playability scanner because they're also indexed as media items, but the ffmpeg-
        // based repair ladder doesn't apply to document containers — every rung fails with the
        // same generic ffprobe refusal that misleads the user into thinking FFmpeg is broken.
        // Refuse cleanly with a document-aware message so the user knows the finding is real,
        // the file is preserved, and they can delete manually from Files or Dismiss to stop
        // reporting it. BookProbeService / ComicProbeService are the right eventual home for
        // dedicated book-corruption handling (follow-up).
        var ext = Path.GetExtension(issue.Path).ToLowerInvariant();
        if (Scanners.MediaFormats.Books.Contains(ext) || Scanners.MediaFormats.Comics.Contains(ext))
        {
            return FixResult.Fail(
                FixReason.ConverterUnavailable,
                "This looks like a damaged ebook / comic. MediaDash's media-repair pipeline is for audio and video; book/comic repair isn't implemented yet. The file is unchanged — delete it from Files, or Dismiss this issue to stop reporting it.");
        }

        // Audio stored far from its video: the file still plays from the start, so it must never
        // reach the delete path below. Rewrite with -max_interleave_delta 0 or leave it alone —
        // never dispose of a file that only fails after a seek.
        if (string.Equals(fixReason, InterleaveAnalyzer.IssueReason, StringComparison.Ordinal))
        {
            return await FixInterleaveAsync(issue, revalidation.Probe, cancellationToken).ConfigureAwait(false);
        }

        // A second approval authorises disposal only for the unchanged source that failed repair.
        if (!PlayabilityRepairState.IsCurrent(issue))
        {
            if (config.DryRun)
            {
                return FixResult.DryRun("Would attempt repair; if it fails, keep the file for delete approval.", 0);
            }

            if (revalidation.Probe is not null)
            {
                return await TryRepairAsync(issue, revalidation.Probe, progress, cancellationToken).ConfigureAwait(false);
            }

            return FixResult.Fail(FixReason.ProbeUnavailable, "Can't safely repair this file right now. The file is unchanged.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var currentConfig = _configProvider.GetSnapshot();
        if (currentConfig.DryRun)
        {
            return FixResult.DryRun("Would remove the file after failed repair.", new FileInfo(issue.Path).Length);
        }

        if (issue.Status != IssueStatus.Queued
            || !_guard.IsInsideLibrary(issue.Path)
            || !PlayabilityRepairState.IsCurrent(issue)
            || currentConfig.GetFixMode(IssueType.Playability) == FixMode.Off
            || (_protection is not null && _protection.IsFixerBlocked(issue.Path, IssueType.Playability)))
        {
            return FixResult.Refused(FixReason.ConsentChanged, "Deletion is no longer approved. The file is unchanged.");
        }

        var size = new FileInfo(issue.Path).Length;
        var disposal = currentConfig.GetDisposal(IssueType.Playability);
        var actionText = string.Format(
            CultureInfo.InvariantCulture,
            "removed unplayable file {0} ({1})",
            Path.GetFileName(issue.Path),
            disposal == DisposalMethod.RecycleBin ? "kept in recycle bin" : "permanently deleted");

        string? recyclePath = null;
        if (disposal == DisposalMethod.RecycleBin)
        {
            recyclePath = _recycleBin.MoveToBin(issue.Path);
        }
        else
        {
            File.Delete(issue.Path);
        }

        _libraryMonitor.ReportFileSystemChanged(issue.Path);
        // Actively purge the Jellyfin item row so a rescan can't re-flag the ghost as "file gone".
        // ReportFileSystemChanged above queues an async debounced update that gets lost across a
        // Jellyfin restart; a synchronous DeleteItem call reaps the row now. Best-effort — the fix
        // already succeeded on disk, so a failure to reap the item is a soft error we log and swallow.
        TryReapJellyfinItem(issue);
        _logger.LogInformation("Playability fix: {Action}", actionText);
        return new FixResult
        {
            Success = true,
            Outcome = FixOutcome.Applied,
            Message = actionText,
            BytesFreed = size,
            RecyclePath = recyclePath
        };
    }

    private void TryReapJellyfinItem(Issue issue)
    {
        if (issue.ItemId == Guid.Empty)
        {
            return;
        }

        try
        {
            var item = _libraryManager.GetItemById(issue.ItemId);
            if (item is null)
            {
                return;
            }

            // DeleteFileLocation = false: the fix already moved/recycled the file, so this call
            // only asks Jellyfin to drop the DB row. Sidecar / subtitle / trickplay cleanup on
            // disk was already handled by the fixer itself (or by the recycle-bin move).
            _libraryManager.DeleteItem(item, new MediaBrowser.Controller.Library.DeleteOptions
            {
                DeleteFileLocation = false,
                DeleteFromExternalProvider = false
            });
        }
        catch (Exception ex)
        {
            // Never fail the fix over a library-manager reap failure — Jellyfin's next library
            // refresh will still catch up. Log so a persistent failure pattern is visible.
            _logger.LogWarning(ex, "Failed to actively purge Jellyfin item {ItemId} for {Path} after fix", issue.ItemId, issue.Path);
        }
    }

    /// <summary>
    /// Rewrites a mis-interleaved file with <c>-max_interleave_delta 0</c> so audio sits next to
    /// its video. Pure muxer-level rebuild — no re-encode, so the output is bit-identical where it
    /// matters. On any failure (ffmpeg error, structural verify, decode verify, interleave re-check
    /// non-Clean) the temp output is dropped and the original is left exactly as it was. The usual
    /// disposal path is bypassed: a file that only fails on seek must never be recycled.
    /// </summary>
    private async Task<FixResult> FixInterleaveAsync(Issue issue, FfprobeData? revalidationProbe, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var fileName = Path.GetFileName(issue.Path);
        if (config.DryRun)
        {
            return FixResult.DryRun("would rewrite " + fileName + " with sound and picture stored side by side", 0);
        }

        var probe = revalidationProbe ?? await _ffprobe.ProbeAsync(issue.Path, cancellationToken).ConfigureAwait(false);
        var ext = Path.GetExtension(issue.Path).TrimStart('.');
        if (string.IsNullOrEmpty(ext) || probe is null)
        {
            return FixResult.Fail("The file could not be read; nothing was changed.");
        }

        if (!_replacement.CanRecycle(issue.Path))
        {
            return FixResult.Refused(FixReason.AccessDenied, "No safe recycle location is available for the original.");
        }

        var rewriteShortfall = FreeSpaceGuard.DescribeShortfall(issue.Path, 2);
        if (rewriteShortfall is not null)
        {
            return FixResult.Fail("Cannot rewrite the file — " + rewriteShortfall + "; nothing was changed.");
        }

        long expectedSourceLength = 0;
        long expectedSourceMtimeTicks = 0;
        try
        {
            var sourceInfo = new FileInfo(issue.Path);
            if (sourceInfo.Exists)
            {
                expectedSourceLength = sourceInfo.Length;
                expectedSourceMtimeTicks = sourceInfo.LastWriteTimeUtc.Ticks;
            }
        }
        catch (IOException)
        {
            // VRS refuses at commit time when identity is zero — safer than ignoring the check.
        }

        if (string.IsNullOrEmpty(TranscodeFixer.ContainerToFfmpegFormat(ext)))
        {
            return FixResult.Refused(FixReason.UnsupportedFormat, "This container cannot be safely rewritten.");
        }

        var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp5", ext);
        try
        {
            // -max_interleave_delta 0 holds packets in memory until every stream catches up instead of
            // giving up after 10 seconds and writing them in source order again. Memory grows with the
            // size of the misplaced stretch — a worst-case Blu-ray remux with 30 minutes of unpaired
            // audio can sit on hundreds of MB. ponytail: live with it, revisit if we hit an OOM report.
            var args = BuildInterleaveRepairArgs(issue.Path, tempPath, ext);

            var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken).ConfigureAwait(false);
            var verifyError = error ?? await VerifyRepairedAsync(probe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
            if (verifyError is null)
            {
                var outputProbe = await _ffprobe.ProbeAsync(tempPath, cancellationToken).ConfigureAwait(false);
                if (outputProbe is null)
                {
                    verifyError = "The rewritten file could not be re-probed to confirm the layout.";
                }
                else
                {
                    var recheck = await _ffprobe.InterleaveCheckAsync(tempPath, outputProbe, cancellationToken, useCache: false).ConfigureAwait(false);
                    // Only Clean is proof of a successful repair. Indeterminate is NOT clean — accepting
                    // it would promote an unverified replacement over the user's original. Report that
                    // explicitly so the user sees which side the uncertainty came from.
                    verifyError = recheck.Status switch
                    {
                        InterleaveStatus.Clean => null,
                        InterleaveStatus.Broken => "The rewritten file still has sound and picture stored apart.",
                        _ => "Could not verify the rewritten file — the layout check didn't complete. The original is unchanged."
                    };
                }
            }

            if (verifyError is not null)
            {
                TryDelete(tempPath);
                return FixResult.Fail("The file could not be rewritten; it was left untouched.", TranscodeFixer.Truncate(verifyError));
            }

            return await TrySwapRepairedAsync(
                    issue,
                    tempPath,
                    "sound and picture stored side by side",
                    extensionChanged: false,
                    probe,
                    expectedSourceLength,
                    expectedSourceMtimeTicks,
                    cancellationToken).ConfigureAwait(false)
                ?? FixResult.Fail("The file could not be rewritten; it was left untouched.");
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Attempts repair and preserves the source if no rung produces a verified replacement.
    /// Each rung is gated by its own config toggle; a rung that produces no verified output
    /// hands off to the next.
    /// </summary>
    private async Task<FixResult> TryRepairAsync(
        Issue issue,
        FfprobeData originalProbe,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(TranscodeFixer.ContainerToFfmpegFormat(Path.GetExtension(issue.Path).TrimStart('.'))))
        {
            return FixResult.Refused(FixReason.UnsupportedFormat, "This container cannot be safely repaired.");
        }

        var config = Plugin.Instance!.Configuration;

        // Disabled methods are not a failed repair attempt.
        if (!config.RepairAttemptRemux && !config.RepairAttemptDropStreams
            && !config.RepairAttemptContainerCoerce && !config.RepairAttemptAudioReencode
            && !config.RepairAttemptReencode)
        {
            return FixResult.Fail(FixReason.UnsupportedFormat, "All repair methods are disabled. The file is unchanged.");
        }

        // Pre-flight: enough free space on target volume for repair.
        // 3× source when full re-encode is on (temp + safety), 2× otherwise (temp swap only).
        // Rung 4a's temp is roughly source-sized (video copied), so 2× covers it.
        if (!_replacement.CanRecycle(issue.Path))
        {
            return FixResult.Refused(FixReason.AccessDenied, "No safe recycle location is available for the original.");
        }

        var repairMultiplier = config.RepairAttemptReencode ? 3 : 2;
        var repairShortfall = FreeSpaceGuard.DescribeShortfall(issue.Path, repairMultiplier);
        if (repairShortfall is not null)
        {
            _logger.LogInformation("Playability repair skipped for {Path}: {SpaceFailure}", issue.Path, repairShortfall);
            return FixResult.Fail(FixReason.InsufficientSpace, "Cannot attempt repair — " + repairShortfall + ". The file is unchanged.");
        }

        // Per-stream decode is expensive; memoise so rung 2 and rung 4a share one probe pass.
        var brokenLazy = new Lazy<Task<IReadOnlyList<int>>>(
            () => IdentifyBrokenStreamsAsync(issue, originalProbe, cancellationToken));

        // External audit P1 #5: capture source identity BEFORE any rung produces output. Previously
        // TrySwapRepairedAsync stat'd the source after the repair had finished — a mid-repair
        // import that replaced the source became the NEW expected identity, so the stale repaired
        // output then replaced the newly-imported file. TrackFixer and FormatCompatibilityFixer
        // already capture identity before conversion; align Playability with that pattern.
        long expectedSourceLength = 0;
        long expectedSourceMtimeTicks = 0;
        try
        {
            var preRepairInfo = new FileInfo(issue.Path);
            if (preRepairInfo.Exists)
            {
                expectedSourceLength = preRepairInfo.Length;
                expectedSourceMtimeTicks = preRepairInfo.LastWriteTimeUtc.Ticks;
            }
        }
        catch (IOException)
        {
            // Fall through — zero identity means VRS will refuse at commit time if the source
            // disappeared, which is the safer failure mode than ignoring the check.
        }

        FixResult? swap;
        string? outPath;
        if (config.RepairAttemptRemux && (outPath = await TryRung1Async(issue, originalProbe, cancellationToken).ConfigureAwait(false)) is not null
            && (swap = await TrySwapRepairedAsync(issue, outPath, "quick remux", extensionChanged: false, originalProbe, expectedSourceLength, expectedSourceMtimeTicks, cancellationToken).ConfigureAwait(false)) is not null)
        {
            return swap;
        }

        if (config.RepairAttemptDropStreams && (outPath = await TryRung2Async(issue, originalProbe, brokenLazy, cancellationToken).ConfigureAwait(false)) is not null
            && (swap = await TrySwapRepairedAsync(issue, outPath, "dropped broken streams", extensionChanged: false, originalProbe, expectedSourceLength, expectedSourceMtimeTicks, cancellationToken).ConfigureAwait(false)) is not null)
        {
            return swap;
        }

        if (config.RepairAttemptContainerCoerce && (outPath = await TryRung3Async(issue, originalProbe, cancellationToken).ConfigureAwait(false)) is not null
            && (swap = await TrySwapRepairedAsync(issue, outPath, "container changed to .mkv (Jellyfin watch history reset)", extensionChanged: true, originalProbe, expectedSourceLength, expectedSourceMtimeTicks, cancellationToken).ConfigureAwait(false)) is not null)
        {
            return swap;
        }

        // Rung 4a: audio-only re-encode. Fires only when the file has a single audio track and
        // that track is the sole broken stream (rung 2 refused because dropping would leave 0
        // audio). Video is copied, so this completes in minutes even on 4K sources. Same
        // container, so extensionChanged: false (watch history preserved).
        if (config.RepairAttemptAudioReencode && (outPath = await TryRung4aAsync(issue, originalProbe, brokenLazy, cancellationToken).ConfigureAwait(false)) is not null
            && (swap = await TrySwapRepairedAsync(issue, outPath, "audio re-encoded", extensionChanged: false, originalProbe, expectedSourceLength, expectedSourceMtimeTicks, cancellationToken).ConfigureAwait(false)) is not null)
        {
            return swap;
        }

        if (config.RepairAttemptReencode && (outPath = await TryRung4Async(issue, originalProbe, progress, cancellationToken).ConfigureAwait(false)) is not null
            && (swap = await TrySwapRepairedAsync(issue, outPath, "video re-encoded", extensionChanged: true, originalProbe, expectedSourceLength, expectedSourceMtimeTicks, cancellationToken).ConfigureAwait(false)) is not null)
        {
            return swap;
        }

        cancellationToken.ThrowIfCancellationRequested();
        PlayabilityRepairState.Apply(issue, expectedSourceLength, expectedSourceMtimeTicks);
        if (!PlayabilityRepairState.IsCurrent(issue))
        {
            return FixResult.Fail(FixReason.SourceChanged, "The source file changed during repair. The changed file is preserved.");
        }

        return FixResult.Fail(FixReason.RepairFailed, "Repair failed. The file is unchanged and is back in Issues for delete approval or Ignore.");
    }

    // Lazy-shared decode across rung 2 and rung 4a. Runs DecodeStreamAsync per stream; returns
    // the indexes that failed. Empty list means every stream decoded cleanly.
    private async Task<IReadOnlyList<int>> IdentifyBrokenStreamsAsync(
        Issue issue, FfprobeData originalProbe, CancellationToken cancellationToken)
    {
        if (originalProbe.Streams is null || originalProbe.Streams.Count == 0)
        {
            return Array.Empty<int>();
        }

        var brokenIndexes = new List<int>();
        foreach (var stream in originalProbe.Streams)
        {
            string? decodeError;
            try
            {
                decodeError = await _ffprobe.DecodeStreamAsync(issue.Path, stream.Index, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Per-stream decode inconclusive for {Path}; continuing repair ladder", issue.Path);
                return Array.Empty<int>();
            }

            if (decodeError is not null)
            {
                brokenIndexes.Add(stream.Index);
            }
        }

        return brokenIndexes;
    }

    // Rung 1: error-tolerant remux, same container. Recovers bad index, wrong duration,
    // missing moov atom, EOF truncation. Cheapest rung — always attempted first when enabled.
    // ponytail: single remux pass; add multi-attempt with -analyzeduration hints only if
    // real fixtures show recoverable files that this misses.
    private async Task<string?> TryRung1Async(Issue issue, FfprobeData originalProbe, CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(issue.Path).TrimStart('.');
        if (string.IsNullOrEmpty(ext))
        {
            return null;
        }

        var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp1", ext);
        var keepOutput = false;
        try
        {
            // +discardcorrupt is the difference between rung 1 producing a file that plays end-to-end vs
            // one that fails at the last packet. -c copy blindly copies every packet the demuxer emits,
            // including the partial packet at the truncation edge of a tail-truncated MP4 — the AAC
            // decoder chokes on the incomplete tail and strict verify (-xerror) rejects the whole file.
            // discardcorrupt drops that final partial packet at demux time so the remux writes a clean
            // container terminating at the last complete packet. No effect on MKV/MP4 files that were
            // whole to begin with; only matters on tail damage. Same flag on rungs 3 + 4 for symmetry.
            var args = new List<string>
            {
                "-err_detect", "ignore_err",
                "-fflags", "+genpts+igndts+discardcorrupt",
                "-i", issue.Path,
                "-map", "0",
            };

            // Bug-pattern audit round 3 (2026-09-19): same-container remux against a source whose
            // subtitle track carries a codec the target muxer refuses under -c copy used to abort
            // the whole rung — .ts with PGS is the common shape (mpegts muxer refuses PGS),
            // .mp4/.m4v with bitmap subs also hit this, .mkv with tx3g rarer. Rung 3 then coerced
            // to .mkv and recovered, but Jellyfin's watch history for the title reset with the
            // extension change. Fold in a negative-map for muxer-incompatible sub streams so
            // rung 1 succeeds in-place (drops the offending sub, keeps every other stream and
            // the original container). Skips silently for extensions with no known blacklist.
            foreach (var idx in TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(originalProbe, ext))
            {
                args.Add("-map");
                args.Add("-0:" + idx.ToString(CultureInfo.InvariantCulture));
            }

            // R09 (2026-09-20): tempPath tail is `.mediaDash.tmp` — pass -f matching source ext.
            args.AddRange(new[]
            {
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
                "-f", TranscodeFixer.ContainerToFfmpegFormat(ext),
                tempPath
            });

            var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                _logger.LogDebug("Rung 1 remux failed for {Path}: {Error}", issue.Path, TranscodeFixer.Truncate(error));
                TryDelete(tempPath);
                return null;
            }

            var verifyError = await VerifyRepairedAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
            if (verifyError is not null)
            {
                _logger.LogDebug("Rung 1 verify failed for {Path}: {Error}", issue.Path, verifyError);
                TryDelete(tempPath);
                return null;
            }

            keepOutput = true;
            return tempPath;
        }
        finally
        {
            if (!keepOutput)
            {
                TryDelete(tempPath);
            }
        }
    }

    // Rung 2: per-stream decode check via ffprobe, drop the streams that error.
    // Safety hard-stop: refuse if the drop would leave 0 audio or 0 video streams.
    private async Task<string?> TryRung2Async(Issue issue, FfprobeData originalProbe, Lazy<Task<IReadOnlyList<int>>> brokenLazy, CancellationToken cancellationToken)
    {
        if (originalProbe.Streams is null || originalProbe.Streams.Count == 0)
        {
            return null;
        }

        var brokenIndexes = await brokenLazy.Value.ConfigureAwait(false);
        if (brokenIndexes.Count == 0)
        {
            return null;
        }

        // Safety: never leave zero video or zero audio streams. Refuse the whole rung; caller falls to rung 3.
        var survivingVideo = originalProbe.Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase) && !brokenIndexes.Contains(s.Index));
        var survivingAudio = originalProbe.Streams.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase) && !brokenIndexes.Contains(s.Index));
        if (!survivingVideo || !survivingAudio)
        {
            _logger.LogDebug("Rung 2 refused for {Path}: dropping would leave 0 video or 0 audio streams.", issue.Path);
            return null;
        }

        var ext = Path.GetExtension(issue.Path).TrimStart('.');
        if (string.IsNullOrEmpty(ext))
        {
            return null;
        }

        var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp2", ext);
        var keepOutput = false;
        try
        {
            var args = new List<string>
            {
                "-err_detect", "ignore_err",
                "-i", issue.Path,
                "-map", "0",
            };
            foreach (var idx in brokenIndexes)
            {
                args.Add("-map");
                args.Add("-0:" + idx.ToString(CultureInfo.InvariantCulture));
            }

            // R09 (2026-09-20): tempPath tail is `.mediaDash.tmp` — pass -f matching source ext.
            args.AddRange(["-c", "copy", "-f", TranscodeFixer.ContainerToFfmpegFormat(ext), tempPath]);

            var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                _logger.LogDebug("Rung 2 remux failed for {Path}: {Error}", issue.Path, TranscodeFixer.Truncate(error));
                TryDelete(tempPath);
                return null;
            }

            var verifyError = await VerifyRepairedAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
            if (verifyError is not null)
            {
                _logger.LogDebug("Rung 2 verify failed for {Path}: {Error}", issue.Path, verifyError);
                TryDelete(tempPath);
                return null;
            }

            keepOutput = true;
            return tempPath;
        }
        finally
        {
            if (!keepOutput)
            {
                TryDelete(tempPath);
            }
        }
    }

    // Rung 3: repack into MKV (universal container). Changes the file extension —
    // Jellyfin re-indexes the file and watch history for the title resets.
    // SwapRepairedAsync uses extensionChanged: true so the output lands at
    // Path.ChangeExtension(original, ".mkv") and both paths are reported to the library monitor.
    private async Task<string?> TryRung3Async(Issue issue, FfprobeData originalProbe, CancellationToken cancellationToken)
    {
        // Same-container coerce is meaningless — that's rung 1's job.
        if (string.Equals(Path.GetExtension(issue.Path), ".mkv", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp3", "mkv");
        var keepOutput = false;
        try
        {
            // R09 (2026-09-20): tempPath tail is `.mediaDash.tmp` — pass -f matroska explicitly.
            var args = new List<string>
            {
                "-err_detect", "ignore_err",
                "-fflags", "+genpts+igndts+discardcorrupt",
                "-i", issue.Path,
                "-map", "0",
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
                "-f", "matroska",
                tempPath
            };

            var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                _logger.LogDebug("Rung 3 mkv-coerce failed for {Path}: {Error}", issue.Path, TranscodeFixer.Truncate(error));
                TryDelete(tempPath);
                return null;
            }

            var verifyError = await VerifyRepairedAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
            if (verifyError is not null)
            {
                _logger.LogDebug("Rung 3 verify failed for {Path}: {Error}", issue.Path, verifyError);
                TryDelete(tempPath);
                return null;
            }

            keepOutput = true;
            return tempPath;
        }
        finally
        {
            if (!keepOutput)
            {
                TryDelete(tempPath);
            }
        }
    }

    // Rung 4a: audio-only re-encode. Cheap middle rung between rung 3 (container coerce) and
    // rung 4 (full video re-encode). Fires only in the narrow-but-common case that rung 2
    // couldn't handle: a single-audio-track file whose only audio stream is broken. Video is
    // stream-copied so this takes minutes even on 4K sources.
    // Roadmap: i-bl-12-rung4a-audio-reencode.
    private async Task<string?> TryRung4aAsync(Issue issue, FfprobeData originalProbe, Lazy<Task<IReadOnlyList<int>>> brokenLazy, CancellationToken cancellationToken)
    {
        if (originalProbe.Streams is null || originalProbe.Streams.Count == 0)
        {
            return null;
        }

        var audioStreams = originalProbe.Streams.Where(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)).ToList();
        var videoStreams = originalProbe.Streams.Where(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)).ToList();

        // Precondition: exactly one audio track. Multi-track sources with one broken audio
        // already succeed via rung 2 (drop the bad one, keep the survivors). Zero audio =
        // nothing for this rung to do.
        if (audioStreams.Count != 1)
        {
            return null;
        }

        var brokenIndexes = await brokenLazy.Value.ConfigureAwait(false);
        if (brokenIndexes.Count == 0)
        {
            return null;
        }

        var brokenSet = brokenIndexes.ToHashSet();

        // The sole audio must be broken, and every video stream must decode cleanly.
        // If video is also broken, only rung 4's full re-encode can salvage it.
        if (!brokenSet.Contains(audioStreams[0].Index))
        {
            return null;
        }

        if (videoStreams.Any(v => brokenSet.Contains(v.Index)))
        {
            return null;
        }

        var ext = Path.GetExtension(issue.Path).TrimStart('.');
        if (string.IsNullOrEmpty(ext))
        {
            return null;
        }

        var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp4a", ext);
        var audioEncoder = ext.ToLowerInvariant() switch
        {
            "mp3" or "avi" or "divx" => "libmp3lame",
            "flac" => "flac",
            "wav" or "aif" or "aiff" or "aifc" when audioStreams[0].CodecName?.StartsWith("pcm_", StringComparison.Ordinal) == true => audioStreams[0].CodecName,
            "wav" => "pcm_s32le",
            "aif" or "aiff" or "aifc" => "pcm_s32be",
            "ogg" or "oga" or "ogm" or "ogv" => "libvorbis",
            "opus" or "webm" => "libopus",
            "wma" or "wmv" or "asf" => "wmav2",
            "mpeg" or "mpg" or "vob" => "mp2",
            "mkv" or "mka" or "mp4" or "m4v" or "m4a" or "m4b" or "mov"
                or "ts" or "m2ts" or "mts" or "m2t" or "flv" or "3gp" or "3g2" or "aac" => "aac",
            _ => null
        };
        if (audioEncoder is null)
        {
            return null;
        }

        var keepOutput = false;
        try
        {
            var args = new List<string>
            {
                "-err_detect", "ignore_err",
                "-fflags", "+genpts+igndts+discardcorrupt",
                "-i", issue.Path,
                "-map", "0",
                "-c:v", "copy",
                "-c:a", audioEncoder
            };
            if (audioEncoder != "flac" && !audioEncoder.StartsWith("pcm_", StringComparison.Ordinal))
            {
                args.AddRange(["-b:a", "192k"]);
            }

            args.AddRange([
                "-c:s", "copy",
                "-c:d", "copy",
                "-c:t", "copy",
                "-avoid_negative_ts", "make_zero",
                "-f", TranscodeFixer.ContainerToFfmpegFormat(ext),
                tempPath
            ]);

            var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                _logger.LogDebug("Rung 4a audio re-encode failed for {Path}: {Error}", issue.Path, TranscodeFixer.Truncate(error));
                TryDelete(tempPath);
                return null;
            }

            var verifyError = await VerifyRepairedAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
            if (verifyError is not null)
            {
                _logger.LogDebug("Rung 4a verify failed for {Path}: {Error}", issue.Path, verifyError);
                TryDelete(tempPath);
                return null;
            }

            keepOutput = true;
            return tempPath;
        }
        finally
        {
            if (!keepOutput)
            {
                TryDelete(tempPath);
            }
        }
    }

    // Rung 4: last-resort full re-encode into MKV. Slow (hours per file for 4K sources) and
    // only runs on the background scheduled scan. Uses conservative h264 + AAC targets so the
    // output plays on the widest range of clients; user isn't asked which codec because if
    // we're here the source is broken enough that survival trumps optimality.
    // ponytail: h264/aac hardcoded; add a config knob only if a user files a preference (i-bl-05).
    private async Task<string?> TryRung4Async(Issue issue, FfprobeData originalProbe, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // MEDIA-02 / issue #76: audio-only sources (FLAC / MP3 / M4A with embedded cover art) have
        // no "real" video to re-encode — rung 4's libx264 pass was being handed the JPEG cover and
        // aborting with "width not divisible by 2" on odd image dimensions. A failed rung is not
        // proof the source is broken, so skipping rung 4 for these preserves the fall-through to a
        // safe refusal instead of unjustified disposal. True audio repair isn't on the ladder today.
        if (IsAudioOnlySource(originalProbe))
        {
            _logger.LogInformation(
                "Rung 4 skipped for audio-only source {Path} — libx264 re-encode doesn't apply to attached cover art.",
                issue.Path);
            return null;
        }

        var tempPath = TranscodeFixer.SidecarPath(issue.Path, "repair.tmp4", "mkv");
        var keepOutput = false;
        try
        {
            var args = new List<string>
            {
                "-err_detect", "ignore_err",
                "-fflags", "+genpts+igndts+discardcorrupt",
                "-i", issue.Path,
                "-map", "0:v:0?",
                "-map", "0:a?",
                "-map", "0:s?",
            };

            // MEDIA-02: exclude attached pictures from the video mapping on mixed-content sources
            // (e.g. a video file with an album-art thumbnail attached). Mapping 0:v:0? picks the first
            // video stream, which can be the attached picture when it's first in container order. The
            // libx264 pass then fails on the picture's odd dimensions or pixel format.
            if (originalProbe.Streams is not null)
            {
                foreach (var stream in originalProbe.Streams)
                {
                    if (string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase) && stream.IsAttachedPicture)
                    {
                        args.Add("-map");
                        args.Add("-0:" + stream.Index.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }

            // Bug-pattern audit round 3 (2026-09-19): MKV target + tx3g / mov_text source sub
            // aborts under `-c:s copy` with "Subtitle codec tx3g not supported in Matroska". These
            // MP4-native timed-text subs have no MKV mapping — drop them via negative-map so the
            // last-resort re-encode isn't defeated by a stray MP4-source subtitle. Rare in the
            // wild but real (MP4 files hitting the repair ladder aren't hypothetical).
            foreach (var idx in TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(originalProbe, "mkv"))
            {
                args.Add("-map");
                args.Add("-0:" + idx.ToString(CultureInfo.InvariantCulture));
            }

            // R09 (2026-09-20): tempPath tail is `.mediaDash.tmp` — pass -f matroska explicitly.
            args.AddRange(new[]
            {
                "-c:v", "libx264",
                "-preset", "medium",
                "-crf", "20",
                "-c:a", "aac",
                "-b:a", "192k",
                "-c:s", "copy",
                "-map_chapters", "0",
                "-f", "matroska",
                tempPath
            });

            double duration = 0;
            if (double.TryParse(originalProbe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            {
                duration = d;
            }

            var error = await _ffmpeg.RunAsync(args, RepairRungTimeout, cancellationToken, progress, duration).ConfigureAwait(false);
            if (error is not null)
            {
                _logger.LogInformation("Rung 4 re-encode failed for {Path}: {Error}", issue.Path, TranscodeFixer.Truncate(error));
                TryDelete(tempPath);
                return null;
            }

            var verifyError = await VerifyRepairedAsync(originalProbe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
            if (verifyError is not null)
            {
                _logger.LogInformation("Rung 4 verify failed for {Path}: {Error}", issue.Path, verifyError);
                TryDelete(tempPath);
                return null;
            }

            keepOutput = true;
            return tempPath;
        }
        finally
        {
            if (!keepOutput)
            {
                TryDelete(tempPath);
            }
        }
    }

    // Verify a repair-rung output. OutputVerifier.VerifyAsync checks stream counts / duration /
    // frame-or-packet parity — but a permissive ffmpeg remux can produce a file that passes those
    // and STILL fails to decode (rung 1 remuxing HEVC-in-AVI back to HEVC-in-AVI, or copying
    // past bit-flipped packets). Decode-sample the output; if the same PlayabilityScanner check
    // that flagged the input would flag the output, this rung didn't actually repair anything.
    // Spec §4.2 mandates this second gate — the plan collapsed it into OutputVerifier by mistake.
    private async Task<string?> VerifyRepairedAsync(FfprobeData originalProbe, string originalPath, string outputPath, CancellationToken cancellationToken)
    {
        var structural = await _verifier.VerifyAsync(originalProbe, originalPath, outputPath, cancellationToken).ConfigureAwait(false);
        if (structural is not null)
        {
            return structural;
        }

        var outputProbe = await _ffprobe.ProbeAsync(outputPath, cancellationToken).ConfigureAwait(false);
        var duration = 0d;
        if (outputProbe?.Format?.Duration is not null)
        {
            _ = double.TryParse(outputProbe.Format.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        }

        // MEDIA-02: propagate audio-only probe shape so the verification doesn't false-positive on
        // repair outputs that preserved cover art (same reason the scanner's decode check needs it).
        var decodeError = await _ffprobe.DecodeCheckAsync(outputPath, duration, cancellationToken, IsAudioOnlySource(originalProbe)).ConfigureAwait(false);
        if (decodeError is not null || outputProbe is null)
        {
            return decodeError ?? "The rebuilt file could not be analyzed.";
        }

        var brokenAudio = await _ffprobe.FindBrokenAudioStreamAsync(outputPath, outputProbe, cancellationToken).ConfigureAwait(false);
        return brokenAudio?.Error;
    }

    // Kept for backwards-compatible test access; wraps the shared FreeSpaceGuard used by every
    // encode/remux/repair fixer. See FreeSpaceGuard.cs for the actual gate.
    internal static bool HasFreeSpace(string path, int multiplier) => FreeSpaceGuard.HasEnoughFreeSpace(path, multiplier);

    // Returns null when the swap is refused (target collision on extension change) — caller
    // falls through to the next rung. Non-null result means the swap committed and the fix is done.
    private async Task<FixResult?> TrySwapRepairedAsync(
        Issue issue,
        string repairedTempPath,
        string rungLabel,
        bool extensionChanged,
        FfprobeData originalProbe,
        long expectedSourceLength,
        long expectedSourceMtimeTicks,
        CancellationToken cancellationToken)
    {
        var finalPath = extensionChanged
            ? Path.ChangeExtension(issue.Path, ".mkv")
            : issue.Path;

        // Path.ChangeExtension is a no-op when the source is already .mkv, so rung 3 / rung 4
        // on an .mkv source land on the SAME path as the source. That's a same-file swap, not
        // a collision — treat it exactly like the same-extension case below (guaranteed present,
        // no clash) instead of tripping the collision guard against ourselves. Pre-fix: rung 3
        // and rung 4 always fell through to delete for every .mkv source (the most common shape
        // for bit-flip damage), quietly gutting the whole repair ladder for .mkv files.
        var pathActuallyChanged = extensionChanged
            && !string.Equals(finalPath, issue.Path, StringComparison.OrdinalIgnoreCase);

        // Collision guard: extension change would land on top of an unrelated existing file
        // (e.g. left over from an earlier repair run, or a user file that happens to share
        // the target name). Refuse BEFORE touching the original — falling through to the next
        // rung is safer than aborting the whole fix. The service also enforces this via
        // DestinationCollision refusal, but doing it here lets the caller fall through cleanly.
        if (pathActuallyChanged && File.Exists(finalPath))
        {
            _logger.LogInformation(
                "Playability repair refused for {Path}: target {Final} already exists — falling through to next rung.",
                issue.Path,
                finalPath);
            TryDelete(repairedTempPath);
            return null;
        }

        // 03-C: hand the verified repair rung's output to VerifiedReplacementService. This
        // preserves the always-recycle-repaired-original invariant (`RecycleOriginal: true`
        // unconditionally, regardless of general failed-repair disposal setting), and gains
        // the plan's uniform safety upgrade — promote-before-dispose so a crash between
        // recycle and final rename can't strand the only remaining copy at a sweep-doomed
        // path. Pre-migration, TrySwapRepairedAsync called `recycleBin.MoveToBin(source)`
        // directly and then `File.Move(temp, final, overwrite:false)` — a failure in the
        // final move left the temp sitting outside sweep patterns as a repair.tmpN sidecar
        // that had NO recovery-marker rename, so a subsequent orphan sweep could delete it.
        // External audit P1 #5: identity captured in TryRepairAsync BEFORE any rung produced
        // output, and now threaded through here. A mid-repair import that replaced the source
        // during a 30-minute encode no longer silently becomes the new "expected source"; VRS
        // refuses the swap, the repair is discarded, and the newly-imported source stays.
        var request = new ReplacementRequest(
            SourcePath: issue.Path,
            OutputPath: repairedTempPath,
            DestinationPath: finalPath,
            ExpectedSourceLength: expectedSourceLength,
            ExpectedSourceMtimeUtcTicks: expectedSourceMtimeTicks,
            RecycleOriginal: true,
            PreserveSourceTimestamps: true);

        var preserveRecovery = false;
        try
        {
            var replacementResult = await _replacement.ReplaceAsync(
                request,
                verifyOutput: _ => Task.FromResult<string?>(null),
                checkCurrentPolicy: () =>
                {
                    // External audit P1 #4: previously this only checked DryRun, so protection
                    // added mid-repair or a Playability fixer switched to Off during the long
                    // encode still committed. Honour the full policy stack at commit time.
                    var currentConfig = _configProvider.GetSnapshot();
                    if (currentConfig.DryRun)
                    {
                        return PolicyDecision.Refuse(FixReason.TypeDisabled);
                    }

                    var mode = currentConfig.GetFixMode(IssueType.Playability);
                    if (mode == FixMode.Off)
                    {
                        return PolicyDecision.Refuse(FixReason.TypeDisabled);
                    }

                    if (_protection is not null && _protection.IsFixerBlocked(issue.Path, IssueType.Playability))
                    {
                        return PolicyDecision.Refuse(FixReason.Protected);
                    }

                    return PolicyDecision.Allow;
                },
                cancellationToken).ConfigureAwait(false);

            preserveRecovery = replacementResult.RecoveryPath is not null;

            if (!replacementResult.Applied)
            {
                if (replacementResult.Reason == FixReason.RecoveryRequired && replacementResult.RecoveryPath is not null)
                {
                    Api.Diagnostics.Record(
                        "Playability.SwapAborted",
                        replacementResult.DescribeRecovery(issue.Path));
                    return FixResult.Fail(FixReason.RecoveryRequired, replacementResult.DescribeRecovery(issue.Path), replacementResult.TechnicalDetail);
                }

                if (replacementResult.Reason == FixReason.SourceChanged)
                {
                    return FixResult.Fail(FixReason.SourceChanged, "The source file changed during repair; the repair was discarded and the changed source was preserved.", replacementResult.TechnicalDetail);
                }

                if (replacementResult.Reason == FixReason.DestinationCollision)
                {
                    // Race with a concurrent process planting a file at the extension-changed
                    // destination between the guard above and the service call. Fall through to
                    // next rung the same way the pre-check does.
                    TryDelete(repairedTempPath);
                    return null;
                }

                if (replacementResult.Reason == FixReason.Cancelled)
                {
                    return FixResult.Fail(FixReason.Cancelled, "Playability repair was cancelled; the original is untouched.", replacementResult.TechnicalDetail);
                }

                return FixResult.Fail(replacementResult.Reason, "Playability repair could not be committed: " + (replacementResult.TechnicalDetail ?? replacementResult.Reason.ToString()), replacementResult.TechnicalDetail);
            }

            _libraryMonitor.ReportFileSystemChanged(issue.Path);
            if (pathActuallyChanged)
            {
                _libraryMonitor.ReportFileSystemChanged(finalPath);
            }

            var loss = await MeasureDurationLossAsync(originalProbe, finalPath, CancellationToken.None).ConfigureAwait(false);
            var message = loss is null
                ? $"repaired {Path.GetFileName(issue.Path)} ({rungLabel})"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "partially repaired {0} ({1}) — playable duration is {2:F1}s shorter than originally declared (declared {3:F1}s, playable {4:F1}s)",
                    Path.GetFileName(issue.Path),
                    rungLabel,
                    loss.Value.Lost,
                    loss.Value.DeclaredOriginal,
                    loss.Value.RepairedDuration);
            _logger.LogInformation("Playability repair: {Message}", message);
            return new FixResult
            {
                Success = true,
                Outcome = FixOutcome.Applied,
                Message = message,
                BytesFreed = 0,
                SavedBytes = expectedSourceLength,
                RecyclePath = replacementResult.RecyclePath
            };
        }
        finally
        {
            if (!preserveRecovery)
            {
                TryDelete(repairedTempPath);
            }
        }
    }

    // Declared-duration guardrail. A repair rung can salvage playable frames while the tail bytes
    // are physically gone (interrupted download, bit-flip at EOF): OutputVerifier's packet-count
    // Layer 3 legitimately accepts the file because both the source and the output only contain
    // the packets that actually exist — but the container's DECLARED duration on the source
    // (14.0s in the field report) versus the repaired output (8.0s) tells the user something they
    // need to see. Anything within the same slack OutputVerifier uses is treated as normal muxer
    // drift and left un-annotated so cosmetic sub-slack differences don't spam "partially
    // repaired" on healthy remuxes. Only fires when the loss crosses the same threshold the
    // verifier itself would care about.
    // ponytail: extra probe on the just-written file; skip if this shows up on the hot path.
    private async Task<(double DeclaredOriginal, double RepairedDuration, double Lost)?> MeasureDurationLossAsync(
        FfprobeData originalProbe,
        string repairedPath,
        CancellationToken cancellationToken)
    {
        if (!TryGetDuration(originalProbe, out var originalDuration) || originalDuration <= 0)
        {
            return null;
        }

        FfprobeData? repairedProbe;
        try
        {
            repairedProbe = await _ffprobe.ProbeAsync(repairedPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not measure duration after committed repair {Path}", repairedPath);
            return null;
        }

        if (repairedProbe is null || !TryGetDuration(repairedProbe, out var repairedDuration) || repairedDuration <= 0)
        {
            return null;
        }

        return EvaluateDurationLoss(originalDuration, repairedDuration);
    }

    /// <summary>
    /// Pure-math peer of <c>MeasureDurationLossAsync</c>: given the original's declared duration
    /// and the repaired file's declared duration, decide whether the loss crosses the same slack
    /// threshold <see cref="OutputVerifier"/> uses (max of 2s or 2% of original). Sub-slack losses
    /// are treated as normal muxer drift and swallowed; anything larger is reported to the caller
    /// so the user's success message can name the loss. Extracted for direct unit-testing without
    /// spinning up ffprobe / the fixer harness.
    /// </summary>
    /// <param name="originalDuration">The source's declared duration in seconds.</param>
    /// <param name="repairedDuration">The repaired file's declared duration in seconds.</param>
    /// <returns>Loss tuple when the gap is significant; null otherwise.</returns>
    internal static (double DeclaredOriginal, double RepairedDuration, double Lost)? EvaluateDurationLoss(
        double originalDuration,
        double repairedDuration)
    {
        if (originalDuration <= 0 || repairedDuration <= 0)
        {
            return null;
        }

        var lost = originalDuration - repairedDuration;
        var slack = Math.Max(2.0, originalDuration * 0.02);
        if (lost <= slack)
        {
            return null;
        }

        return (originalDuration, repairedDuration, lost);
    }

    internal static List<string> BuildInterleaveRepairArgs(string sourcePath, string outputPath, string ext)
        => ["-i", sourcePath, "-map", "0", "-c", "copy", "-max_interleave_delta", "0", "-f", TranscodeFixer.ContainerToFfmpegFormat(ext), outputPath];

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; leftover sidecars get swept next FfmpegExecutor.RunAsync call.
        }
    }

    /// <summary>
    /// Tri-state playability revalidation (MD-010). Distinguishes "probe couldn't run at all"
    /// (Indeterminate — infrastructure failure, must not proceed to disposal) from "probe ran and
    /// says still broken" (Broken — safe to repair/dispose) and "probe ran and says healthy"
    /// (Healthy — issue is stale, do nothing). Returns the probe payload for callers to reuse so
    /// the repair ladder doesn't race against a second probe that might disagree.
    /// </summary>
    private async Task<RevalidationResult> RevalidateAsync(Issue issue, CancellationToken cancellationToken)
    {
        // Re-verify only the *specific* condition the scanner flagged. Older logic tested "no video
        // stream" for every issue, which recycled healthy audio-kind files flagged with reason "no-audio".
        var reason = TryGetReason(issue.DetailsJson);

        // 0-byte guard runs BEFORE ffprobe so an empty file is always Broken regardless of whether
        // this build's ffprobe returns null vs an error-shaped stub. Field-report 2026-09-25:
        // ffprobe on Windows returns non-null with Error set + no FormatName for 0-byte files,
        // which hit the "Indeterminate" branch below and blocked the disposal path — user had a
        // stuck 0-byte file with no way to remove it. Only the definite 0-byte case is claimed
        // here; anything larger falls through to the normal probe + Indeterminate protections.
        try
        {
            if (File.Exists(issue.Path) && new FileInfo(issue.Path).Length == 0)
            {
                return RevalidationResult.Broken(new FfprobeData());
            }
        }
        catch (IOException)
        {
            // Fall through — if we can't stat the file, the file might not exist; the normal
            // probe branch below will reach Indeterminate for the null case.
        }

        var probe = await _ffprobe.ProbeAsync(issue.Path, cancellationToken).ConfigureAwait(false);
        if (probe is null || FfprobeService.IsInfrastructureError(probe.Error?.Message))
        {
            // MD-010: ffprobe couldn't produce data (missing binary, process-start failure, timeout,
            // parse failure, unauthorized access, etc.). Every one of those is an infrastructure
            // problem — the file itself might be fine. Refuse rather than assume the worst.
            // E2E audit E2E-06: diagnostic text now flows through to the user; keep it accurate.
            return RevalidationResult.Indeterminate(
                "Can't safely check this file right now — ffprobe didn't respond (missing, hung, or errored). No fix was applied. Try again once ffprobe is working (Dashboard → Playback → FFmpeg settings).");
        }

        // A probe that reports an internal error and doesn't identify a container at all is
        // ambiguous — ffprobe hit trouble before it could tell us anything about the file. Bias
        // toward Indeterminate; the alternative is silently recycling a file whose "brokenness"
        // is really "ffprobe couldn't parse this build's output." E2E audit E2E-06: previously
        // every variant of this branch surfaced "ffprobe didn't respond" to the user, which
        // falsely blamed the FFmpeg install for files that are simply not valid media. Make
        // the user-facing diagnostic reflect the actual condition.
        if (probe.Error is not null && string.IsNullOrEmpty(probe.Format?.FormatName))
        {
            return RevalidationResult.Indeterminate(
                "This file isn't a valid media container — ffprobe read it but couldn't identify a format. The file is unchanged; delete it from Files or Dismiss this issue to stop reporting it.");
        }

        if (probe.Streams is null || probe.Streams.Count == 0)
        {
            // No streams but ffprobe DID identify a container — the file is genuinely readable
            // enough to open but has nothing to play. That's still broken.
            return RevalidationResult.Broken(probe);
        }

        // Interleave check requires its own async packet walk, kept separate from ReasonStillHolds
        // which is a sync fast-path. Fresh measurement (useCache: false) so a stale cache entry
        // can't let a known-broken file through as healthy. Indeterminate maps to Broken on
        // purpose: if we can't confirm the file is still mis-laid, the safe assumption is that it
        // still is — the issue stays live and the user can retry.
        if (string.Equals(reason, InterleaveAnalyzer.IssueReason, StringComparison.Ordinal))
        {
            var result = await _ffprobe.InterleaveCheckAsync(issue.Path, probe, cancellationToken, useCache: false).ConfigureAwait(false);
            return result.Status == InterleaveStatus.Clean
                ? RevalidationResult.Healthy(probe)
                : RevalidationResult.Broken(probe);
        }

        var stillBroken = ReasonStillHolds(reason, probe, issue);
        if (stillBroken is null)
        {
            // MEDIA-02 / issue #76: pass isAudioOnly derived from the probe so the decode check
            // skips attached cover-art pictures. Without this, revalidation inherits the same
            // false-positive the scanner originally produced and keeps the issue Broken even
            // after the classifier has been corrected.
            var isAudioOnly = IsAudioOnlySource(probe);
            // decode-error requires an async DecodeCheck; branch out to keep this method sync-flat.
            try
            {
                var decodeError = await _ffprobe.DecodeCheckAsync(issue.Path, DurationOrZero(probe), cancellationToken, isAudioOnly).ConfigureAwait(false);
                var brokenAudio = decodeError is null
                    ? await _ffprobe.FindBrokenAudioStreamAsync(issue.Path, probe, cancellationToken).ConfigureAwait(false)
                    : null;
                return decodeError is not null || brokenAudio is not null
                    ? RevalidationResult.Broken(probe)
                    : RevalidationResult.Healthy(probe);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return RevalidationResult.Indeterminate("Can't safely decode this file right now: " + ex.Message + " No fix was applied.");
            }
        }

        return stillBroken.Value
            ? RevalidationResult.Broken(probe)
            : RevalidationResult.Healthy(probe);
    }

    private static double DurationOrZero(FfprobeData probe)
        => TryGetDuration(probe, out var d) ? d : 0;

    // MEDIA-02 / issue #76: true when the file has no "real" video stream — attached_pic streams
    // (JPEG cover art embedded in a FLAC / MP3 / M4A) count as video for codec_type purposes but
    // aren't playable content. Passing isAudioOnly=true to DecodeCheckAsync for these makes it
    // route through -vn so ffmpeg's default best-stream picker doesn't grab the cover art and
    // report its 0-second decode as truncation.
    private static bool IsAudioOnlySource(FfprobeData probe)
    {
        if (probe.Streams is null || probe.Streams.Count == 0)
        {
            return false;
        }

        var hasRealVideo = probe.Streams.Any(s =>
            string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)
            && !s.IsAttachedPicture);
        var hasAudio = probe.Streams.Any(s =>
            string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
        return hasAudio && !hasRealVideo;
    }

    // Returns null when the caller needs to do an async follow-up (decode-error branch).
    private static bool? ReasonStillHolds(string? reason, FfprobeData probe, Issue issue)
    {
        switch (reason)
        {
            case "no-audio":
                return !probe.Streams!.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));

            case "no-video":
                return !probe.Streams!.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));

            case "no-duration":
                return TryGetDuration(probe, out _) ? false : null;

            case "size-truncated":
                // Old bitrate/size findings must pass a decode check before any destructive action.
                return null;

            case "decode-error":
                // Signal the caller to await DecodeCheckAsync; can't do async here without turning
                // this whole helper into a Task-returning method for every reason.
                return null;

            case "container-extension-mismatch":
                {
                    var ext = Path.GetExtension(issue.Path).ToLowerInvariant().TrimStart('.');
                    var formatName = probe.Format?.FormatName?.ToLowerInvariant();
                    if (string.IsNullOrEmpty(ext) || string.IsNullOrEmpty(formatName))
                    {
                        return false;
                    }

                    var demuxers = formatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var extAliases = ExtensionAliasesFor(ext);
                    if (extAliases.Length == 0)
                    {
                        return false;
                    }

                    return !extAliases.Any(a => demuxers.Contains(a));
                }

            case "unreadable":
            case "book-or-comic-corrupt":
                return false;

            default:
                if (!probe.Streams!.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                return !TryGetDuration(probe, out var legacyDuration) || legacyDuration <= 0;
        }
    }

    private Task<FixResult> RenameForMismatchAsync(Issue issue, string? renameToExt) => Task.FromResult(RenameForMismatch(issue, renameToExt));

    private FixResult RenameForMismatch(Issue issue, string? renameToExt)
    {
        var config = Plugin.Instance!.Configuration;
        var targetExt = renameToExt;
        if (string.IsNullOrWhiteSpace(targetExt))
        {
            return FixResult.Fail(FixReason.SourceChanged, "Scanner didn't record a target extension for the rename. Re-scan to refresh the issue.");
        }

        var srcPath = issue.Path;
        var srcName = Path.GetFileName(srcPath);
        var targetPath = Path.ChangeExtension(srcPath, "." + targetExt);
        var targetName = Path.GetFileName(targetPath);

        if (string.Equals(srcPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return FixResult.NoChange(FixReason.NothingToRemove, "Source and target names are identical — nothing to rename.");
        }

        var actionText = string.Format(
            CultureInfo.InvariantCulture,
            "renamed {0} to {1} (container was {2})",
            srcName,
            targetName,
            targetExt);

        if (config.DryRun)
        {
            return FixResult.DryRun(actionText, 0);
        }

        if (File.Exists(targetPath))
        {
            return FixResult.Refused(FixReason.DestinationCollision, string.Format(
                CultureInfo.InvariantCulture,
                "Can't rename {0} to {1} — a file already exists at that name. Resolve the collision manually and re-scan.",
                srcName,
                targetName));
        }

        try
        {
            _libraryMonitor.ReportFileSystemChangeBeginning(srcPath);
            _libraryMonitor.ReportFileSystemChangeBeginning(targetPath);
            File.Move(srcPath, targetPath);
            _libraryMonitor.ReportFileSystemChanged(srcPath);
            _libraryMonitor.ReportFileSystemChanged(targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FixResult.Fail(FixReason.ProcessFailed, "Rename failed: " + ex.Message);
        }
        finally
        {
            _libraryMonitor.ReportFileSystemChangeComplete(srcPath, refreshPath: false);
            _libraryMonitor.ReportFileSystemChangeComplete(targetPath, refreshPath: true);
        }

        _logger.LogInformation("Playability fix (rename): {Action}", actionText);
        return new FixResult
        {
            Success = true,
            Outcome = FixOutcome.Applied,
            Message = actionText,
            BytesFreed = 0,
            RecyclePath = null
        };
    }

    private static string[] ExtensionAliasesFor(string ext) => ext switch
    {
        "mkv" or "webm" => new[] { "matroska", "webm" },
        "mp4" or "m4v" or "m4a" or "mov" or "3gp" or "3g2" => new[] { "mp4", "mov", "m4a", "3gp", "3g2", "mj2" },
        "avi" => new[] { "avi" },
        "wmv" or "asf" => new[] { "asf" },
        "flv" => new[] { "flv" },
        "ts" or "mts" or "m2ts" => new[] { "mpegts" },
        "mpg" or "mpeg" or "vob" => new[] { "mpeg", "mpegvideo", "mpegps" },
        "ogv" or "ogg" or "ogm" or "opus" => new[] { "ogg" },
        "mp3" => new[] { "mp3" },
        "aac" => new[] { "aac" },
        "flac" => new[] { "flac" },
        "wav" => new[] { "wav" },
        "ac3" => new[] { "ac3" },
        "dts" => new[] { "dts" },
        "wma" => new[] { "asf" },
        "aif" or "aiff" => new[] { "aiff" },
        _ => Array.Empty<string>()
    };

    // 01-C: routes through IssueDetailsReader so the same schema check that guards the
    // destructive dispatch above also guards the probe re-verification here. Returns null on
    // any malformed shape — the F-015 guard is preserved by the reader's TryReadPlayability
    // returning false for non-object roots / wrong-typed reason.
    private static string? TryGetReason(string detailsJson)
        => IssueDetailsReader.TryReadPlayability(detailsJson, out var d) ? d?.Reason : null;

    /// <summary>Test-only wrapper around <c>TryGetReason</c> so the F-015 guard is directly pinnable without spinning up a fixer instance.</summary>
    /// <param name="detailsJson">The issue DetailsJson.</param>
    /// <returns>The reason, or null on any malformed shape.</returns>
    internal static string? TryGetReasonForTest(string detailsJson) => TryGetReason(detailsJson);

    /// <summary>Reads the friendly one-liner the scanner wrote into <c>detail</c>. Returns null when absent, malformed, or blank. Public/internal for tests.</summary>
    /// <param name="detailsJson">The issue DetailsJson.</param>
    /// <returns>The trimmed detail string, trailing period stripped, or null.</returns>
    internal static string? TryGetDetail(string detailsJson)
    {
        var raw = TryGetString(detailsJson, "detail");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        return trimmed.EndsWith('.') ? trimmed[..^1] : trimmed;
    }

    private static string? TryGetString(string detailsJson, string property)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using var details = JsonDocument.Parse(detailsJson);
            // F-015: guard root shape + property shape. TryGetProperty on non-object and
            // GetString on non-string both throw InvalidOperationException, which
            // catch (JsonException) doesn't match.
            if (details.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!details.RootElement.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return el.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetDuration(FfprobeData probe, out double duration)
        => probe.TryGetDuration(out duration);
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Re-encodes oversized files down to the configured quality ceiling.
/// Riskiest fixer: full temp → verify → swap flow, with free-space check and a larger-output bailout.
/// </summary>
public sealed class TranscodeFixer : IFixer
{
    /// <summary>R09 (2026-09-20): all in-flight sidecars end with this non-media tail so
    /// Jellyfin's real-time monitor and library scan skip them mid-encode. Preserves the
    /// existing <c>.mediadash.tmp</c> / <c>.new</c> / <c>.swap</c> / <c>.strip</c> middle
    /// infix so <see cref="LibraryGuard.SweepOrphanSidecars"/> patterns still match.</summary>
    public const string InflightTail = ".mediadash.tmp";

    private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromHours(6);

    // Codecs known to be broadly HW-decodable across modern (2016+) GPUs on every current
    // encoder family. Whitelist match doesn't guarantee THIS specific GPU has the decoder;
    // ffmpeg's init will fail and the existing full-SW retry (see FixAsync line ~163-183)
    // catches the miss with a full-software second pass.
    // ponytail: mpeg4 (DivX/XviD) stays off — decode wins are tiny on small SD files and
    // ffmpeg's mpeg4 HW decoder is spotty across families. Add if a user reports it.
    private static readonly HashSet<string> HwDecodeCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "hevc", "vp9", "av1", "mpeg2video", "vc1"
    };

    private readonly FfprobeService _ffprobe;
    private readonly FfmpegExecutor _ffmpeg;
    private readonly OutputVerifier _verifier;
    private readonly LibraryGuard _guard;
    private readonly VerifiedReplacementService _replacement;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly IssueStore _issues;
    private readonly ProtectionService _protection;
    private readonly ILogger<TranscodeFixer> _logger;
    private readonly MediaMovePlanner? _movePlanner;
    private readonly MediaMoveService? _moves;
    private readonly HistoryStore? _moveHistory;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeFixer"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="ffmpeg">The ffmpeg executor.</param>
    /// <param name="verifier">The output verifier.</param>
    /// <param name="guard">The library path guard.</param>
    /// <param name="replacement">03-B: the shared verify → promote → dispose → commit protocol.
    /// Takes ownership of the source recycle (or permanent delete) as part of its commit.</param>
    /// <param name="configProvider">02-A snapshot provider. Used for the pre-commit policy
    /// recheck callback so a dry-run flip mid-encode aborts before the commit.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface, used to resolve the item behind an issue for post-encode renaming.</param>
    /// <param name="serverConfig">Instance of the <see cref="IServerConfigurationManager"/> interface, used to read the server's hardware acceleration type.</param>
    /// <param name="issues">Issue-table store (05-C.2), used to spot same-path queued siblings and re-point sibling issues after a container change or canonical rename.</param>
    /// <param name="protection">Never-modify protection service (roadmap i-110-protected-media). Re-consulted at commit time so protection added during a long encode still aborts the replacement.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="movePlanner">Complete optional-rename previews.</param>
    /// <param name="moves">Durable optional-rename execution.</param>
    /// <param name="moveHistory">Separate rename History, whose Undo preserves the re-encode.</param>
    public TranscodeFixer(
        FfprobeService ffprobe,
        FfmpegExecutor ffmpeg,
        OutputVerifier verifier,
        LibraryGuard guard,
        VerifiedReplacementService replacement,
        IPluginConfigurationProvider configProvider,
        ILibraryMonitor libraryMonitor,
        ILibraryManager libraryManager,
        IServerConfigurationManager serverConfig,
        IssueStore issues,
        ProtectionService protection,
        ILogger<TranscodeFixer> logger,
        MediaMovePlanner? movePlanner = null,
        MediaMoveService? moves = null,
        HistoryStore? moveHistory = null)
    {
        _ffprobe = ffprobe;
        _ffmpeg = ffmpeg;
        _verifier = verifier;
        _guard = guard;
        _replacement = replacement;
        _configProvider = configProvider;
        _libraryMonitor = libraryMonitor;
        _libraryManager = libraryManager;
        _serverConfig = serverConfig;
        _issues = issues;
        _protection = protection;
        _logger = logger;
        _movePlanner = movePlanner;
        _moves = moves;
        _moveHistory = moveHistory;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type)
        => type == IssueType.Quality
        || type == IssueType.HeavyTranscode
        || type == IssueType.FailedTranscode;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!File.Exists(issue.Path))
        {
            return FixResult.Fail(FixReason.SourceMissing, "The file no longer exists; re-scan to refresh the list.");
        }

        if (!_guard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "The file is outside your library folders; MediaDash will not touch it.");
        }

        var probe = await _ffprobe.ProbeAsync(issue.Path, cancellationToken).ConfigureAwait(false);
        // Encoder-quirks audit Q14: skip attached-pic streams so a yt-dlp thumbnail at
        // stream 0 doesn't get selected and re-encoded as the movie.
        var video = probe?.Streams?.FirstOrDefault(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase) && !s.IsAttachedPicture);
        if (probe is null || probe.Error is not null || video is null)
        {
            return FixResult.Fail(FixReason.ProbeUnavailable, "The file could not be analyzed; it may be broken.");
        }

        var originalSize = new FileInfo(issue.Path).Length;
        var targetContainer = string.IsNullOrWhiteSpace(config.TargetContainer) ? "mkv" : config.TargetContainer.TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ContainerToFfmpegFormat(targetContainer)))
        {
            return FixResult.Refused(FixReason.UnsupportedFormat, "The requested output container is unsupported: " + targetContainer);
        }

        var targetPath = Path.ChangeExtension(issue.Path, "." + targetContainer);
        var needsDownscale = video.Height is > 0 && video.Height.Value > config.MaxResolutionHeight;
        var disposal = config.GetDisposal(issue.Type);

        // MP4-family target + source audio the mp4 muxer refuses under `-c copy`. BuildArgs hard-
        // codes `-c:a copy` for every Quality/HeavyTranscode/FailedTranscode re-encode; on a 4K
        // Blu-ray remux with TrueHD / DTS-HD MA / lossless PCM audio (or a FLAC audiobook remux),
        // ffmpeg gets past the video re-encode and then aborts at the mux with "codec not
        // currently supported in container". The whole re-encode fails, the temp is cleaned up,
        // the issue stays Queued, next scheduled fix retries and hits the same wall — retry storm
        // that only ends when the user notices and switches Settings → Quality → target container
        // to MKV. Refuse cleanly here instead: the message names the offending codec + points at
        // the exact setting to change, and the typed FixReason.IncompatibleContainer routes to
        // KeepQueued via FixResultPolicy so the refusal stays reviewable. Once the user changes
        // the target container the approved issue can run with the new setting.
        // ponytail: refusal is the shortest safe fix. A future round can add either a blocking-
        // warning consent gate (matches the bitmap-sub pattern — user Approve = accept audio
        // transcode to AAC) or an automatic AAC fallback when a preference lands.
        var mp4IncompatibleAudio = TrackFixer.FirstMp4IncompatibleAudioCodec(probe, targetContainer);
        if (mp4IncompatibleAudio is not null)
        {
            return FixResult.Refused(FixReason.IncompatibleContainer, string.Format(
                CultureInfo.InvariantCulture,
                "Can't re-encode '{0}' with target container .{1}: source contains {2} audio, and the MP4 muxer refuses that codec under -c copy — this fix requires re-encoding the audio, which MediaDash won't do silently on a Quality run. Change Settings → Quality → target container to MKV (accepts every audio codec), or leave the file at its current container.",
                Path.GetFileName(issue.Path),
                targetContainer,
                mp4IncompatibleAudio.ToUpperInvariant()));
        }

        var actionText = string.Format(
            CultureInfo.InvariantCulture,
            "re-encoded {0} to {1}p {2} ({3}), {4}",
            Path.GetFileName(issue.Path),
            needsDownscale ? config.MaxResolutionHeight : video.Height,
            config.PreferredCodec.ToUpperInvariant(),
            targetContainer.ToUpperInvariant(),
            disposal == DisposalMethod.RecycleBin ? "original kept in recycle bin" : "original permanently deleted");

        if (config.DryRun)
        {
            return FixResult.DryRun(actionText, issue.SizeSavings);
        }

        // CLAUDE.md safety invariant 5: transcode fixers require at least 2× source-size free
        // before starting. The temp file lives alongside the original during the encode and is
        // aborted if it ever reaches originalSize; the original is only removed after the swap.
        // Worst case we hold source + full-size temp simultaneously = 2× source. Fails closed if
        // the target drive can't be resolved.
        if (disposal == DisposalMethod.RecycleBin && !_replacement.CanRecycle(issue.Path))
        {
            return FixResult.Refused(FixReason.AccessDenied, "No safe recycle location is available for the original.");
        }

        var transcodeShortfall = FreeSpaceGuard.DescribeShortfall(issue.Path, multiplier: 2);
        if (transcodeShortfall is not null)
        {
            return FixResult.Fail(FixReason.InsufficientSpace, "Cannot re-encode this file — " + transcodeShortfall + ".");
        }

        var durationSeconds = double.TryParse(probe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

        // Per-run GUID on both sidecars so a hot-reload leaving an old ffmpeg mid-run (past the sweep's
        // 5-min age guard) can't collide with a fresh run on the same target — the fresh run would
        // otherwise File.Delete the old run's about-to-commit swap and truncate the user's file.
        // The GUID still contains the "mediadash.tmp" / "mediadash.new" substring so the sweep and the
        // library-guard sidecar patterns still match for cleanup.
        var runToken = Guid.NewGuid().ToString("N")[..8];
        var tempPath = SidecarPath(issue.Path, "tmp." + runToken, targetContainer);
        // 03-B: identity snapshot captured BEFORE ffmpeg runs so the pre-commit source recheck
        // (inside VerifiedReplacementService) can refuse a stale source mutation. Length +
        // mtime is the stale-source guard, not cryptographic identity.
        var srcInfo = new FileInfo(issue.Path);
        var expectedSourceLength = srcInfo.Exists ? srcInfo.Length : 0;
        var expectedSourceMtimeTicks = srcInfo.Exists ? srcInfo.LastWriteTimeUtc.Ticks : 0;
        // 02-B: compute per-path companion consent BEFORE launching ffmpeg. `ManualApprove` mode
        // requires a same-path AudioLanguage / SubtitleLanguage issue to actually be queued
        // (or Detected and up for approval by the caller). A nonempty language allow-list is
        // NOT approval. `Automatic` mode ignores this flag inside BuildArgs.
        var samePathQueued = _issues.GetIssues(status: IssueStatus.Queued)
            .Where(i => string.Equals(i.Path, issue.Path, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Type)
            .ToHashSet();
        var hasApprovedAudioCompanion = samePathQueued.Contains(IssueType.AudioLanguage);
        var hasApprovedSubtitleCompanion = samePathQueued.Contains(IssueType.SubtitleLanguage);
        var item = issue.ItemId == Guid.Empty ? null : _libraryManager.GetItemById(issue.ItemId);
        var retainedTracks = GetRetainedTracks(probe, config, targetContainer, item, hasApprovedAudioCompanion, hasApprovedSubtitleCompanion);
        var removedTracks = probe.Streams!.Where(s => (s.CodecType is "audio" or "subtitle") && !retainedTracks.Contains(s)).Select(s => s.Index).ToArray();

        try
        {
            string? vaapiDevice = null;
            var hwEncoder = config.UseHardwareEncoder ? GetHardwareEncoder(config.PreferredCodec, out vaapiDevice) : null;
            string? error;
            if (hwEncoder is not null)
            {
                var hwArgs = BuildArgs(issue.Path, tempPath, probe, video, config, needsDownscale, targetContainer, hwEncoder, vaapiDevice, hasApprovedAudioCompanion, hasApprovedSubtitleCompanion, item);
                error = await _ffmpeg.RunAsync(hwArgs, TranscodeTimeout, cancellationToken, progress, durationSeconds).ConfigureAwait(false);
                if (error is not null)
                {
                    _logger.LogWarning("Hardware encoder {Encoder} failed on {Path}; retrying with software. Details: {Error}", hwEncoder, issue.Path, Truncate(error));
                    actionText += " Hardware encoding failed; used software encoding.";
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }

                    var swArgs = BuildArgs(issue.Path, tempPath, probe, video, config, needsDownscale, targetContainer, null, null, hasApprovedAudioCompanion, hasApprovedSubtitleCompanion, item);
                    error = await _ffmpeg.RunAsync(swArgs, TranscodeTimeout, cancellationToken, progress, durationSeconds).ConfigureAwait(false);
                }
            }
            else
            {
                var args = BuildArgs(issue.Path, tempPath, probe, video, config, needsDownscale, targetContainer, null, null, hasApprovedAudioCompanion, hasApprovedSubtitleCompanion, item);
                error = await _ffmpeg.RunAsync(args, TranscodeTimeout, cancellationToken, progress, durationSeconds).ConfigureAwait(false);
            }

            if (error is not null)
            {
                return FixResult.Fail(FixReason.ProcessFailed, "Re-encoding failed; the original is untouched. Details: " + Truncate(error));
            }

            var verifyError = await _verifier.VerifyAsync(probe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
            if (verifyError is not null)
            {
                return FixResult.Fail(FixReason.VerificationFailed, "The re-encoded file failed verification; the original is untouched. Details: " + verifyError);
            }

            var outputProbe = await _ffprobe.ProbeAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (!TrackFixer.RetainedTracksMatch(probe, outputProbe, removedTracks, allowAudioCodecChange: true))
            {
                return FixResult.Fail(FixReason.VerificationFailed, "The re-encoded file did not preserve the expected audio and subtitle tracks; the original is untouched.");
            }

            var newSize = new FileInfo(tempPath).Length;
            // Size gate applies only to Quality fixes — the whole point of Quality is disk reclaim,
            // so a re-encode that ends up bigger defeats the purpose. HeavyTranscode / FailedTranscode
            // are about direct-play compatibility: even a slightly larger HEVC output is a win because
            // future plays skip live transcoding. Cap the tolerance at 30% to guard against pathological
            // re-encodes (misconfigured bitrate, wrong preset) but otherwise accept the swap.
            if (issue.Type == IssueType.Quality)
            {
                var savings = originalSize - newSize;
                var savingsPct = originalSize > 0 ? (savings * 100.0 / originalSize) : 0.0;
                var minPct = Math.Clamp(config.QualityMinSavingsPercent, 0, 95);
                var minBytes = Math.Max(0L, config.QualityMinSavingsBytes);

                // Existing larger-output rejection (savings <= 0) plus the roadmap i-110-minimum-savings
                // threshold check. Both use InsufficientSavings so the queue treats them as terminal:
                // an identical re-encode on the next run would produce the same output.
                if (savings <= 0)
                {
                    return FixResult.Fail(FixReason.InsufficientSavings, "The re-encoded file would not be smaller than the original, so the original was kept.");
                }

                if ((minPct > 0 && savingsPct < minPct) || (minBytes > 0 && savings < minBytes))
                {
                    var savedMb = savings / (1024.0 * 1024.0);
                    var msg = "The re-encode saved only " + savedMb.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                        + " MB (" + savingsPct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                        + "%) — under the configured minimum. Original kept. Adjust Settings → Files wasting space → Minimum savings if you want smaller wins accepted.";
                    return FixResult.Fail(FixReason.InsufficientSavings, msg);
                }
            }
            else if (newSize > originalSize * 1.3)
            {
                var overheadPct = (int)((newSize - originalSize) * 100.0 / originalSize);
                return FixResult.Fail(FixReason.Unknown, "The re-encoded file is " + overheadPct + "% larger than the original — something's off with the encode settings, so the original was kept. Try a lower bitrate ceiling or a different codec in Settings → Files wasting space.");
            }

            // 03-B: hand the verified ffmpeg output to VerifiedReplacementService. It owns the
            // stage → promote → dispose → commit sequence, the recovery-marker naming and the
            // sweep-safety guarantees. Verify callback is null (already checked above — repeating
            // it would double the probe cost); policy recheck consults the current config
            // snapshot so a dry-run flip mid-encode aborts the commit under the current setting,
            // not the one the fixer captured at the top of FixAsync.
            var request = new ReplacementRequest(
                SourcePath: issue.Path,
                OutputPath: tempPath,
                DestinationPath: targetPath,
                ExpectedSourceLength: expectedSourceLength,
                ExpectedSourceMtimeUtcTicks: expectedSourceMtimeTicks,
                RecycleOriginal: disposal == DisposalMethod.RecycleBin,
                PreserveSourceTimestamps: true);

            var replacementResult = await _replacement.ReplaceAsync(
                request,
                verifyOutput: _ => Task.FromResult<string?>(null),
                checkCurrentPolicy: () =>
                {
                    var currentConfig = _configProvider.GetSnapshot();
                    var policy = EvaluateCommitPolicy(currentConfig, _protection, issue.Path, issue.Type);
                    if (!policy.Allowed)
                    {
                        return policy;
                    }

                    var currentItem = issue.ItemId == Guid.Empty ? null : _libraryManager.GetItemById(issue.ItemId);
                    var currentQueued = _issues.GetIssues(status: IssueStatus.Queued)
                        .Where(i => string.Equals(i.Path, issue.Path, StringComparison.OrdinalIgnoreCase)).Select(i => i.Type).ToHashSet();
                    var currentTracks = GetRetainedTracks(probe, currentConfig, targetContainer, currentItem, currentQueued.Contains(IssueType.AudioLanguage), currentQueued.Contains(IssueType.SubtitleLanguage));
                    if (currentTracks.Any(s => removedTracks.Contains(s.Index))
                        || (removedTracks.Any(i => probe.Streams!.Any(s => s.Index == i && s.CodecType == "audio")) && _protection.IsFixerBlocked(issue.Path, IssueType.AudioLanguage))
                        || (removedTracks.Any(i => probe.Streams!.Any(s => s.Index == i && s.CodecType == "subtitle")) && _protection.IsFixerBlocked(issue.Path, IssueType.SubtitleLanguage)))
                    {
                        return PolicyDecision.Refuse(FixReason.ConsentChanged);
                    }

                    return PolicyDecision.Allow;
                },
                cancellationToken).ConfigureAwait(false);

            if (!replacementResult.Applied)
            {
                if (replacementResult.Reason == FixReason.RecoveryRequired && replacementResult.RecoveryPath is not null)
                {
                    // Preserve the pre-existing "Transcode.SwapAborted" diagnostic key so the
                    // dashboard, CHANGELOG search anchors and audit trail keep working after
                    // the service takes ownership of the promote+commit protocol. Service also
                    // logs via ILogger; the diagnostic here is the user-facing signal.
                    Api.Diagnostics.Record(
                        "Transcode.SwapAborted",
                        replacementResult.DescribeRecovery(issue.Path));
                    return FixResult.Fail(FixReason.RecoveryRequired, replacementResult.DescribeRecovery(issue.Path), replacementResult.TechnicalDetail);
                }

                if (replacementResult.Reason == FixReason.SourceChanged)
                {
                    return FixResult.Fail(FixReason.SourceChanged, "The source file changed while it was being re-encoded; the encode was discarded and the changed source was preserved.", replacementResult.TechnicalDetail);
                }

                if (replacementResult.Reason == FixReason.DestinationCollision)
                {
                    return FixResult.Refused(FixReason.DestinationCollision, "The target path '" + targetPath + "' is already occupied by a different file; the original is untouched.", replacementResult.TechnicalDetail);
                }

                if (replacementResult.Reason == FixReason.Cancelled)
                {
                    return FixResult.Fail(FixReason.Cancelled, "Re-encode was cancelled; the original is untouched.", replacementResult.TechnicalDetail);
                }

                // Policy refusal (dry-run flipped mid-encode), verification failure surfaced by
                // the service, or any other typed refusal. Preserve the service's typed reason
                // and fold the technical detail into the message.
                return FixResult.Fail(replacementResult.Reason, "Re-encode could not be committed: " + (replacementResult.TechnicalDetail ?? replacementResult.Reason.ToString()), replacementResult.TechnicalDetail);
            }

            // Commit succeeded. `targetPath` now holds the verified encode; the source is
            // recycled (or permanent-deleted) per the request; timestamps are restored by the
            // service. Downstream extras below stay in the caller: canonical rename, library
            // notification, DB path relocation.
            var recyclePath = replacementResult.RecyclePath;
            var finalPath = targetPath;
            if (config.RenameAfterTranscode)
            {
                // Rename has its own complete journal and Undo. A stopped rename remains visible
                // with its recorded recovery locations; it never reverses the verified re-encode.
                var rename = await TryRenameToCanonicalAsync(targetPath, issue, needsDownscale ? config.MaxResolutionHeight : video.Height ?? 0, targetContainer, cancellationToken).ConfigureAwait(false);
                if (rename.Path is not null)
                {
                    finalPath = rename.Path;
                    actionText += " (renamed to " + Path.GetFileName(rename.Path) + ")";
                }

                if (rename.Warning is not null)
                {
                    actionText += " " + rename.Warning;
                }
            }

            _libraryMonitor.ReportFileSystemChanged(issue.Path);
            _libraryMonitor.ReportFileSystemChanged(finalPath);
            // Re-point any other queued issues on this file (audio-language cleanup, sorter, grouper, …)
            // at the new location so they don't fail with "no longer exists" on the next fix run.
            _issues.RelocateIssuePaths(issue.Path, finalPath);
            _logger.LogInformation("Transcode fix: {Action}", actionText);
            return new FixResult
            {
                Success = true,
                Outcome = FixOutcome.Applied,
                Message = actionText,
                BytesFreed = originalSize - newSize,
                RecyclePath = recyclePath
            };
        }
        finally
        {
            // Caller only owns tempPath — the ffmpeg output. On the happy path the service
            // moves tempPath into a recovery-marker sibling of targetPath; on any pre-promote
            // refusal tempPath stays where ffmpeg wrote it and this finally cleans it up.
            // Post-promote failures leave the recovery marker outside every sweep pattern
            // (the service returns RecoveryPath for the caller to surface).
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not delete temp transcode file {Path}", tempPath);
                }
            }
        }
    }

    private string? GetHardwareEncoder(string preferredCodec, out string? vaapiDevice)
    {
        vaapiDevice = null;
        var enc = _serverConfig.GetConfiguration<EncodingOptions>("encoding");
        var accel = enc.HardwareAccelerationType.ToString().ToLowerInvariant();
        var suffix = accel switch
        {
            "amf" => "_amf",
            "nvenc" => "_nvenc",
            "qsv" => "_qsv",
            "videotoolbox" => "_videotoolbox",
            "vaapi" => "_vaapi",
            _ => AutodetectSuffix()
        };
        if (suffix is null)
        {
            return null;
        }

        var codec = preferredCodec.ToLowerInvariant() switch
        {
            "hevc" or "h265" => "hevc",
            "h264" => "h264",
            "av1" => "av1",
            "vp9" => "vp9",
            _ => "hevc"
        };
        // VP9 hardware encoding is Intel-QSV-only in the wild: NVENC/AMF/VideoToolbox/VAAPI all lack a
        // reliable VP9 encoder path. Fall through to software rather than emitting one that won't load.
        if (codec == "vp9" && suffix != "_qsv")
        {
            return null;
        }

        if (suffix == "_vaapi")
        {
            vaapiDevice = ResolveVaapiDevice(enc);
        }

        return codec + suffix;
    }

    // Prefer dedicated (NVIDIA) → any GPU on Linux (VAAPI covers Intel/AMD iGPU + dGPU uniformly)
    // → any GPU on Windows (AMF is broadest; QSV needs Intel-specific plumbing users can pin via
    // Jellyfin's HardwareAccelerationType) → VideoToolbox on macOS → software. Returns null only
    // when no usable GPU is present.
    private static string? AutodetectSuffix()
    {
        try
        {
            var gpus = SystemStats.Sample().Gpus;
            if (gpus.Any(g => string.Equals(g.Source, "NVIDIA", StringComparison.Ordinal)))
            {
                return "_nvenc";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && gpus.Count > 0)
            {
                return "_vaapi";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && gpus.Count > 0)
            {
                // PDH doesn't expose vendor; guess Intel iGPU by name so QSV wins over AMF for Intel-only
                // boxes. Everything else defaults to AMF, which the software-fallback branch covers if wrong.
                return gpus[0].Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "_qsv" : "_amf";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "_videotoolbox";
            }
        }
        catch (Exception)
        {
            // Best-effort probe; any failure means we fall through to software rather than crash the fix.
        }

        return null;
    }

    private static string ResolveVaapiDevice(EncodingOptions enc)
    {
        if (!string.IsNullOrWhiteSpace(enc.VaapiDevice))
        {
            return enc.VaapiDevice;
        }

        // Common convention: renderD128 is the first DRM render node. Prefer whatever's actually
        // there so a box with only renderD129 still works.
        try
        {
            if (Directory.Exists("/dev/dri"))
            {
                var first = Directory.EnumerateFiles("/dev/dri", "renderD*")
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (first is not null)
                {
                    return first;
                }
            }
        }
        catch (IOException)
        {
            // Enumeration failed; fall through to the standard default.
        }

        return "/dev/dri/renderD128";
    }

    // Returns (hwaccel type, hwaccel_output_format), or (null, null) when the family should not
    // get HW decode at all. Only families with a GPU-resident scale filter (scale_cuda, scale_qsv,
    // scale_vaapi) benefit — the rest have to hwdownload frames for the scale step, which costs a
    // GPU↔CPU copy the SW-decode path doesn't pay. On any downscale-bound workload (i.e. most Quality
    // fixes) the extra copy exceeds the CPU savings — bench on 4K HEVC → 720p AMF, 2026-09-05, showed
    // -hwaccel d3d11va + h264_amf was 8% slower than plain SW-decode + h264_amf on this build.
    // ponytail: revisit AMF + VideoToolbox when ffmpeg ships scale_d3d11 / scale_vt as stable filters.
    private static (string? HwaccelType, string? OutputFormat) HwDecodeSpec(string hardwareEncoder)
    {
        if (hardwareEncoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            return ("cuda", "cuda");
        }

        if (hardwareEncoder.EndsWith("_qsv", StringComparison.Ordinal))
        {
            return ("qsv", "qsv");
        }

        if (hardwareEncoder.EndsWith("_vaapi", StringComparison.Ordinal))
        {
            return ("vaapi", "vaapi");
        }

        if (hardwareEncoder.EndsWith("_amf", StringComparison.Ordinal)
            || hardwareEncoder.EndsWith("_videotoolbox", StringComparison.Ordinal))
        {
            // No GPU-resident scale on this pipeline → HW decode is a net loss on any downscale.
            // Keep today's HW-encode-only behavior (SW decode fed to the HW encoder).
            return (null, null);
        }

        throw new ArgumentException("Unknown hardware encoder family: " + hardwareEncoder, nameof(hardwareEncoder));
    }

    internal static List<FfprobeStreamInfo> GetRetainedTracks(
        FfprobeData probe,
        PluginConfiguration config,
        string targetContainer,
        BaseItem? item,
        bool hasApprovedAudioCompanion,
        bool hasApprovedSubtitleCompanion)
    {
        var removed = new HashSet<int>();
        if (IsTypeAuthorized(config, IssueType.AudioLanguage)
            && (config.AudioFixMode == FixMode.Automatic || hasApprovedAudioCompanion))
        {
            removed.UnionWith(TrackFixer.ComputeRemovableIndexes(probe, IssueType.AudioLanguage, config, item));
        }

        if (IsTypeAuthorized(config, IssueType.SubtitleLanguage)
            && (config.SubtitleFixMode == FixMode.Automatic || hasApprovedSubtitleCompanion))
        {
            removed.UnionWith(TrackFixer.ComputeRemovableIndexes(probe, IssueType.SubtitleLanguage, config, item));
        }

        removed.UnionWith(TrackFixer.ComputeMuxerIncompatibleSubtitleIndexes(probe, targetContainer));
        return probe.Streams!.Where(s => !removed.Contains(s.Index)
            && (s.CodecType == "audio" || (s.CodecType == "subtitle" && targetContainer == "mkv"))).ToList();
    }

    internal static List<string> BuildArgs(
        string inputPath,
        string tempPath,
        FfprobeData probe,
        FfprobeStreamInfo video,
        PluginConfiguration config,
        bool needsDownscale,
        string targetContainer,
        string? hardwareEncoder,
        string? vaapiDevice,
        bool hasApprovedAudioCompanion = true,
        bool hasApprovedSubtitleCompanion = true,
        BaseItem? item = null)
    {
        var encoder = hardwareEncoder ?? config.PreferredCodec.ToLowerInvariant() switch
        {
            "hevc" or "h265" => "libx265",
            "h264" => "libx264",
            "av1" => "libsvtav1",
            "vp9" => "libvpx-vp9",
            _ => "libx265"
        };

        // Full-HW pipeline only when the input codec is on the whitelist AND we have a HW encoder
        // selected. Anything else runs SW decode + (HW or SW) encode as before — the whitelist is
        // the only gate; the retry ladder in FixAsync catches per-GPU capability misses.
        var useHwDecode = hardwareEncoder is not null && !string.IsNullOrEmpty(video.CodecName)
            && HwDecodeCodecs.Contains(video.CodecName);

        // hwOutputFormat is non-null only when the family keeps frames GPU-resident through filter+encode.
        // AMF and VideoToolbox use HW decode but land frames in system memory (see HwDecodeSpec), so
        // downstream code that treats "on GPU" specially must check hwOutputFormat, not useHwDecode.
        string? hwaccelType = null;
        string? hwOutputFormat = null;
        if (useHwDecode)
        {
            (hwaccelType, hwOutputFormat) = HwDecodeSpec(hardwareEncoder!);
        }

        var args = new List<string>();
        if (vaapiDevice is not null)
        {
            // Global VAAPI device init must come before -i so decode, filter chain, and encode share the device.
            args.AddRange(["-vaapi_device", vaapiDevice]);
        }

        if (hwaccelType is not null)
        {
            // -hwaccel alone would let ffmpeg decode on GPU then download every frame to system memory
            // (same throughput as pure CPU decode). -hwaccel_output_format keeps frames GPU-resident
            // when the family supports it.
            args.AddRange(["-hwaccel", hwaccelType]);
            if (hwOutputFormat is not null)
            {
                args.AddRange(["-hwaccel_output_format", hwOutputFormat]);
            }
        }

        args.AddRange(["-i", inputPath, "-map", "0:" + video.Index.ToString(CultureInfo.InvariantCulture)]);

        var retainedTracks = GetRetainedTracks(probe, config, targetContainer, item, hasApprovedAudioCompanion, hasApprovedSubtitleCompanion);
        foreach (var stream in retainedTracks)
        {
            args.Add("-map");
            args.Add("0:" + stream.Index.ToString(CultureInfo.InvariantCulture));
        }

        if (targetContainer == "mkv")
        {
            args.AddRange(["-c:s", "copy"]);
        }

        args.AddRange(["-c:v", encoder]);
        if (hardwareEncoder is not null && config.PreferredGpuIndex is int gpuIndex && gpuIndex >= 0)
        {
            // Encoder-family-specific device selection flag. NVENC and AMF take the index as an encoder option;
            // QSV and VideoToolbox don't expose a per-encoder index this way, but a Jellyfin server-wide
            // hardware acceleration device setting covers those. We only send the flag when we know it applies.
            var gpuStr = gpuIndex.ToString(CultureInfo.InvariantCulture);
            if (hardwareEncoder.EndsWith("_nvenc", StringComparison.Ordinal))
            {
                args.AddRange(["-gpu", gpuStr]);
            }
            else if (hardwareEncoder.EndsWith("_amf", StringComparison.Ordinal))
            {
                args.AddRange(["-gpu", gpuStr]);
            }
        }

        if (hardwareEncoder is not null && hardwareEncoder.EndsWith("_vaapi", StringComparison.Ordinal))
        {
            // Mesa VAAPI defaults to CQP for hevc/h264, which ignores -b:v/-maxrate and picks a QP that
            // routinely produces output LARGER than the source. Force VBR so the rate targets actually apply.
            args.AddRange(["-rc_mode", "VBR"]);
        }

        if (hardwareEncoder is not null)
        {
            // Hardware encoders don't support CRF; target the configured bitrate ceiling scaled to the output resolution,
            // but never spend more bits per pixel than the source did (a downscale reduces the needed bitrate too).
            var height = video.Height ?? config.MaxResolutionHeight;
            var targetHeight = Math.Min(height, config.MaxResolutionHeight);
            var pixels = (double)(video.Width ?? 0) * height;
            var scale = height > 0 ? (double)targetHeight / height : 1;
            var targetPixels = pixels > 0 ? pixels * scale * scale : 1920.0 * 1080.0;
            var targetBits = (long)(config.MaxBitrateMbpsAt1080p * 1_000_000 * (targetPixels / (1920.0 * 1080.0)));
            var sourceBits = long.TryParse(video.BitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vb) && vb > 0
                ? vb
                : long.TryParse(probe.Format?.BitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fb) && fb > 0 ? fb : 0;
            if (sourceBits > 0)
            {
                targetBits = Math.Min(targetBits, (long)(sourceBits * scale * scale));
            }

            targetBits = Math.Max(targetBits, 500_000);
            args.AddRange([
                "-b:v", targetBits.ToString(CultureInfo.InvariantCulture),
                "-maxrate", (targetBits * 3 / 2).ToString(CultureInfo.InvariantCulture),
                "-bufsize", (targetBits * 2).ToString(CultureInfo.InvariantCulture)
            ]);
            if (encoder.StartsWith("h264", StringComparison.Ordinal) && vaapiDevice is null && hwOutputFormat is null)
            {
                // h264 hardware encoders are 8-bit; normalize input so 10-bit sources don't abort the encoder.
                // Skipped for VAAPI (frames are already nv12-on-GPU via the hwupload filter below), and for
                // GPU-resident full-HW paths (scale_cuda / scale_qsv handle format on GPU — a CPU -pix_fmt
                // would force an unwanted download). AMF and VideoToolbox still need the CPU -pix_fmt: they
                // use HW decode but their frames land in system memory.
                args.AddRange(["-pix_fmt", "yuv420p"]);
            }
        }
        else
        {
            var (crf, preset, av1Crf, av1Preset, vp9Crf, vp9Cpu) = config.SoftwareEncodePreset switch
            {
                EncodePreset.Faster => ("25", "fast", "32", "6", "35", "4"),
                EncodePreset.Best => ("20", "slow", "28", "10", "28", "0"),
                _ => ("23", "medium", "30", "8", "31", "2")
            };
            if (encoder == "libsvtav1")
            {
                args.AddRange(["-crf", av1Crf, "-preset", av1Preset]);
            }
            else if (encoder == "libvpx-vp9")
            {
                // VP9 uses a two-arg constant-quality mode: -b:v 0 tells the encoder to honour -crf.
                // -cpu-used trades speed for compression like x264/x265 presets.
                args.AddRange(["-crf", vp9Crf, "-b:v", "0", "-cpu-used", vp9Cpu, "-row-mt", "1"]);
            }
            else
            {
                args.AddRange(["-crf", crf, "-preset", preset]);
            }
        }

        if (useHwDecode)
        {
            // Full-HW pipeline: frames arrive as GPU surfaces (via -hwaccel_output_format), stay on
            // GPU through the family-specific scale filter, and go straight to the HW encoder.
            var w = needsDownscale ? "-2" : "iw";
            var h = needsDownscale ? config.MaxResolutionHeight.ToString(CultureInfo.InvariantCulture) : "ih";
            var pixFmt = encoder.StartsWith("h264", StringComparison.Ordinal) ? "yuv420p" : "nv12";

            if (hardwareEncoder!.EndsWith("_nvenc", StringComparison.Ordinal))
            {
                args.AddRange(["-vf", "scale_cuda=w=" + w + ":h=" + h + ":format=" + pixFmt]);
            }
            else if (hardwareEncoder.EndsWith("_qsv", StringComparison.Ordinal))
            {
                // QSV rejects -2; calculate an even width from the decoded frame dimensions.
                w = needsDownscale ? "trunc(iw*oh/ih/2)*2" : w;
                args.AddRange(["-vf", "scale_qsv=w=" + w + ":h=" + h + ":format=" + pixFmt]);
            }
            else if (hardwareEncoder.EndsWith("_vaapi", StringComparison.Ordinal))
            {
                // VAAPI encoders take nv12 for both h264 and hevc; keep format explicit to avoid driver defaults.
                args.AddRange(["-vf", "scale_vaapi=w=" + w + ":h=" + h + ":format=nv12"]);
            }
            else if (needsDownscale)
            {
                // AMF / VideoToolbox: HW decode without GPU-resident frames — implicit hwdownload happens
                // between decode and this CPU scale filter. Skip the filter entirely on no-downscale
                // (nothing needs converting when the frame is already in system memory at target size).
                args.AddRange(["-vf", "scale=-2:" + config.MaxResolutionHeight.ToString(CultureInfo.InvariantCulture)]);
            }
        }
        else if (vaapiDevice is not null)
        {
            // SW decode → VAAPI encode: frames arrive in system memory, so upload then GPU-scale.
            // format=nv12 normalises 10-bit sources for hwupload.
            var vf = needsDownscale
                ? "format=nv12,hwupload,scale_vaapi=w=-2:h=" + config.MaxResolutionHeight.ToString(CultureInfo.InvariantCulture)
                : "format=nv12,hwupload";
            args.AddRange(["-vf", vf]);
        }
        else if (needsDownscale)
        {
            args.AddRange(["-vf", "scale=-2:" + config.MaxResolutionHeight.ToString(CultureInfo.InvariantCulture)]);
        }

        // R09 (2026-09-20): sidecar output tail is `.mediaDash.tmp` so ffmpeg can't infer the
        // muxer from the extension; pass -f explicitly.
        args.AddRange(["-c:a", "copy", "-map_chapters", "0", "-f", ContainerToFfmpegFormat(targetContainer), tempPath]);
        return args;
    }

    // ffmpeg spends most of its output on stream/encoder banners; the actual failure line
    // is always at the end (e.g. "No space left on device", "Invalid argument", codec-specific errors).
    // Show the tail so the user can see the real reason instead of x265's greeting card.
    internal static string Truncate(string text)
    {
        const int Max = 600;
        if (string.IsNullOrEmpty(text) || text.Length <= Max)
        {
            return text;
        }

        return "… " + text[^Max..];
    }

    /// <summary>
    /// F-206 dry-run recheck + roadmap i-110-protected-media commit-time protection recheck,
    /// packaged for testability. The lambda in <c>FixAsync</c> delegates here so the branch stays
    /// unit-testable without invoking a full encode. Called at commit time by
    /// <c>VerifiedReplacementService</c>; a Refuse aborts the swap and preserves the source.
    /// </summary>
    /// <param name="currentConfig">Fresh config snapshot taken at commit time (not the one captured at FixAsync start).</param>
    /// <param name="protection">Never-modify service; consulted with the exact path + type the fixer is about to modify.</param>
    /// <param name="path">The source file being replaced.</param>
    /// <param name="type">The issue type driving the fix (Quality / HeavyTranscode / FailedTranscode).</param>
    /// <returns>Allow when current settings and protection permit the fix; otherwise refuse.</returns>
    internal static PolicyDecision EvaluateCommitPolicy(PluginConfiguration currentConfig, ProtectionService protection, string path, IssueType type)
    {
        if (currentConfig.DryRun || !IsTypeAuthorized(currentConfig, type))
        {
            return PolicyDecision.Refuse(FixReason.TypeDisabled);
        }

        if (protection.IsFixerBlocked(path, type))
        {
            return PolicyDecision.Refuse(FixReason.Protected);
        }

        return PolicyDecision.Allow;
    }

    /// <summary>
    /// P-01 gate helper: an incidental language-track cleanup during a Quality/HeavyTranscode/
    /// FailedTranscode re-encode requires the SAME per-type authorization as a standalone
    /// AudioLanguage or SubtitleLanguage fix. That means the type's <see cref="FixMode"/> must
    /// not be <c>Off</c>, and the type must not be on the per-fixer disable list (Settings →
    /// Fix order → visibility toggle). A nonempty language allow-list is NOT authorization on
    /// its own — it just says "these languages exist" — so the pre-fix filter was silently
    /// running the cleanup regardless of user consent. Bug-pattern audit round 4 (2026-09-19).
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <param name="type">The cleanup type being considered (AudioLanguage / SubtitleLanguage).</param>
    /// <returns>True when the type is authorized to run and the caller may apply its filter.</returns>
    internal static bool IsTypeAuthorized(PluginConfiguration config, IssueType type)
    {
        if (config.GetFixMode(type) == FixMode.Off)
        {
            return false;
        }

        var disabled = config.FixerDisabled;
        if (disabled is null)
        {
            return true;
        }

        for (var i = 0; i < disabled.Length; i++)
        {
            if (string.Equals(disabled[i], type.ToString(), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // Linux's NAME_MAX is 255 bytes. Appending our ".mediadash.tmp.<ext>" suffix to a file whose
    // own basename is already close to that limit (common with Cyrillic/CJK filenames — each
    // char is 2-3 UTF-8 bytes) blows past it and ffmpeg fails with "Error opening output".
    // Fall back to a short, stable hash-based sidecar name in the same directory.
    // Exposed internal for direct testing without spinning up ffmpeg.
    internal static string SidecarPath(string sibling, string marker, string extension)
    {
        var dir = Path.GetDirectoryName(sibling) ?? string.Empty;
        var name = Path.GetFileName(sibling);
        var extSuffix = string.IsNullOrEmpty(extension) ? string.Empty : "." + extension;
        // R09 (2026-09-20): append `.mediaDash.tmp` tail so Jellyfin's video-extension list
        // (checked on the LAST file extension) doesn't treat our staging file as media and
        // index it mid-encode. Ffmpeg no longer infers output format from the tail — every
        // call site now passes `-f <container>` explicitly (see ContainerToFfmpegFormat).
        var candidate = name + ".mediadash." + marker + extSuffix + InflightTail;
        if (Encoding.UTF8.GetByteCount(candidate) <= 240)
        {
            return Path.Combine(dir, candidate);
        }

        // 16 hex chars = 8 bytes of SHA-256; collision-proof for a per-directory sidecar.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0, 8);
        return Path.Combine(dir, "mediadash." + marker + "." + hash + extSuffix + InflightTail);
    }

    /// <summary>Maps a MediaDash container name (e.g. "mkv", "mp3", "png") to the ffmpeg
    /// <c>-f</c> muxer name. R09 (2026-09-20): every sidecar output now ends in
    /// <see cref="InflightTail"/> so ffmpeg can't infer format from the extension — callers
    /// pass this value via <c>-f</c> to the output-args block. Unknown containers return an
    /// empty string so fixers refuse them before starting ffmpeg.</summary>
    /// <param name="container">The MediaDash container name (no leading dot).</param>
    /// <returns>The ffmpeg muxer name.</returns>
    internal static string ContainerToFfmpegFormat(string container)
    {
        return container.ToLowerInvariant() switch
        {
            "mkv" or "mka" => "matroska",
            "mp4" or "m4v" => "mp4",
            "m4a" or "m4b" => "ipod",
            "mov" => "mov",
            "avi" or "divx" => "avi",
            "webm" => "webm",
            "ts" or "m2ts" or "mts" or "m2t" => "mpegts",
            "mpeg" or "mpg" => "mpeg",
            "flv" => "flv",
            "ogg" or "oga" or "ogm" => "ogg",
            "opus" => "opus",
            "mp3" => "mp3",
            "flac" => "flac",
            "wav" => "wav",
            "aac" => "adts",
            "aif" or "aiff" or "aifc" => "aiff",
            "wma" or "wmv" => "asf",
            "webp" => "webp",
            "png" or "jpg" or "jpeg" or "bmp" => "image2",
            "asf" => "asf",
            "ogv" => "ogg",
            "3gp" => "3gp",
            "3g2" => "3g2",
            "vob" => "vob",
            "mxf" => "mxf",
            "rm" or "rmvb" => "rm",
            _ => string.Empty
        };
    }

    internal async Task<(string? Path, string? Warning)> TryRenameToCanonicalAsync(string currentPath, Issue issue, int height, string extension, CancellationToken cancellationToken)
    {
        if (!_configProvider.GetSnapshot().RenameAfterTranscode)
        {
            return (null, null);
        }

        try
        {
            if (_movePlanner is null || _moves is null || _moveHistory is null)
            {
                return (null, "Optional rename skipped because durable move recovery is unavailable. The verified encode stays at its current name.");
            }

            var item = issue.ItemId == Guid.Empty ? null : _libraryManager.GetItemById(issue.ItemId);
            var config = _configProvider.GetSnapshot();
            var canonical = item is null ? null : RenameTemplate.Build(item, height, extension, issue.Path, config.PreserveExternalIdInFilename);
            if (canonical is null)
            {
                return (null, "Optional rename skipped because the item's naming metadata is incomplete. The verified encode stays at its current name.");
            }

            var target = Path.Combine(Path.GetDirectoryName(currentPath)!, canonical);
            if (string.Equals(target, currentPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return (null, null);
            }

            var peers = _libraryManager.GetItemList(MediaMovePlanner.CatalogQuery());
            var plan = await _movePlanner.BuildExplicitAsync([(currentPath, target)], peers, config, cancellationToken).ConfigureAwait(false);
            // The verified replacement may have a new extension before Jellyfin updates its Path.
            // This catalog binding is built only here, after replacement, never from a Files request.
            plan = (plan with { ItemId = item!.Id, ItemPath = currentPath, CatalogPath = item.Path, Identity = MediaMovePlanner.Identity(item), IndependentFiles = false }).Seal();
            var declared = item is Video video ? video.LocalAlternateVersions.Concat(video.AdditionalParts).Concat(video.AudioFiles).Concat(video.SubtitleFiles) : [];
            if (declared.Any(path => !string.Equals(path, item.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && !plan.Entries.Any(entry => string.Equals(entry.Source, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))))
            {
                return (null, "Optional rename skipped because a declared version, part or external stream is outside the complete rename preview. The verified encode stays at its current name.");
            }

            var result = await _moves.ExecuteExplicitAsync(plan, plan.Fingerprint, null, cancellationToken, issue.Id, authorize: () =>
            {
                var currentConfig = _configProvider.GetSnapshot();
                BaseItem? owner;
                try
                {
                    owner = _libraryManager.GetItemById(issue.ItemId);
                }
                catch (InvalidOperationException error)
                {
                    throw new IOException("Jellyfin's current metadata could not authorize the optional rename. Preserve the recorded locations for review.", error);
                }

                if (!currentConfig.RenameAfterTranscode || owner is null || MediaMovePlanner.Identity(owner, plan) != plan.Identity
                    || !FixPolicyEvaluator.Evaluate(currentConfig, issue.Type, IssueStatus.Queued, FixIntent.Approved, true, false, false).Allowed
                    || (issue.Id > 0 && _issues.GetIssueStatus(issue.Id) != IssueStatus.Queued)
                    || _protection.IsFixerBlocked(currentPath, issue.Type) || _protection.IsFixerBlocked(issue.Path, issue.Type))
                {
                    throw new IOException("The optional rename's consent, metadata or protection changed. Preserve the recorded move locations for review.");
                }
            }).ConfigureAwait(false);
            if (!result.Success)
            {
                return (null, "The re-encode completed, but its optional rename stopped. " + result.Message);
            }

            if (result.WasDryRun)
            {
                return (null, "The optional rename was a dry run. The verified encode stays at its current name.");
            }

            string? warning = null;
            try
            {
                _moveHistory.AddHistory(new HistoryEntry
                {
                    IssueId = issue.Id, Type = IssueType.ManualMove, Path = currentPath, Success = true, FixedAtUtc = DateTime.UtcNow,
                    Action = "Renamed the verified encode and its companions. Undo reverses only this rename and keeps the re-encode.", MoveOperationId = result.MoveOperationId
                });
            }
            catch (Microsoft.Data.Sqlite.SqliteException error)
            {
                warning = "The rename completed. Its durable move record remains available even though its separate History entry could not be saved.";
                Api.Diagnostics.Record("Transcode.RenameHistory", "The file was renamed after re-encoding, but the rename could not be added to the History tab. Undo is still available for the move. Details: " + error.Message);
            }

            return (target, warning);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _logger.LogWarning(error, "Optional canonical rename stopped for {Path}; preserve recorded recovery locations", currentPath);
            Api.Diagnostics.Record("Transcode.RenameFailed", "The re-encode finished, but renaming the file afterwards stopped part-way, so it may still have its old name. The move record lists where each file is; check those locations before changing anything. Details: " + error.Message);
            return (null, "The re-encode completed, but the optional rename stopped. Review its recorded move locations before continuing.");
        }
    }
}

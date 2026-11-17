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
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Removes unwanted audio and subtitle tracks by lossless remux (<c>-c copy</c>).
/// Track lists are recomputed from a fresh probe at fix time — never trusted from stale scan data —
/// and the remux never drops the last audio track (safety invariant #2).
/// </summary>
public sealed class TrackFixer : IFixer
{
    private static readonly TimeSpan RemuxTimeout = TimeSpan.FromMinutes(30);

    // Retry ceiling for slow disks / big files: if the 30-min first pass hits the timeout, run
    // one more attempt with a 5-hour cap before giving up. Real bugs still fail fast (bad codec,
    // permissions, disk full) since those don't trigger the timeout branch.
    private static readonly TimeSpan RemuxRetryTimeout = TimeSpan.FromHours(5);

    // MP4-family container extensions (ipod muxer). The mp4/ipod muxer refuses bitmap subtitle
    // codecs under -c copy and any remux to the same container drops those tracks. Kept as a
    // constant so both scanners (data-loss pre-flight warning) and the fixer (negative-map
    // fold-in via ComputeMuxerIncompatibleSubtitleIndexes in RunTrackRemuxAsync) share one
    // definition. The scanner attaches a blocking warning on affected files and the FixTask
    // consent gate keeps them out of Queued until the user manually approves; only then does
    // the fold-in drop the bitmap tracks at remux time.
    private static readonly HashSet<string> Mp4FamilyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "m4a", "mov"
    };

    // Subtitle codecs the mp4/ipod muxer accepts under -c copy. Whitelist rather than blacklist:
    // a blacklist misses aliases (dvdsub for dvd_subtitle, pgssub for hdmv_pgs_subtitle) and
    // never-heard-of encoder outputs, which caused GitHub #56 to resurface on 1.0.7.7. HandBrake
    // .m4v with a VobSub track can report the sub as codec_name=dvdsub or with an empty
    // codec_tag_string (fadern's file: [0][0][0][0]) — the whitelist catches both by leaving
    // them out of the allowed set. Only text-shaped subtitles fit MP4/ipod natively:
    //   * mov_text — the native MP4 timed-text codec (tag 'text' in the STSD box)
    //   * tx3g — 3GPP timed text (older MP4 variant, still valid)
    //   * webvtt — supported on some ffmpeg builds with the right movflags
    // Everything else — dvd_subtitle, hdmv_pgs_subtitle, dvb_subtitle, dvdsub, pgssub, dvbsub,
    // xsub, subrip/srt (needs -c:s mov_text conversion), ass, ssa — is dropped on remux so the
    // ipod muxer never sees a codec it can't tag. Empty / null codec_name also drops (safer to
    // remove an unclassifiable stream than to let ffmpeg refuse the whole file).
    private static readonly HashSet<string> Mp4CompatibleSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "mov_text", "tx3g", "webvtt"
    };

    // Audio codecs the mp4/mov/ipod muxer accepts under -c copy. Same whitelist rationale as the
    // subtitle set above — a blacklist misses vendor-specific codec-name aliases and future ffmpeg
    // codec additions. Bug-pattern audit round 2 (2026-09-19): TranscodeFixer's Quality re-encode
    // targets whatever container the user picks in Settings; when target=MP4 and source audio is
    // TrueHD / DTS-HD MA / lossless PCM / bit-perfect FLAC (standard on 4K Blu-ray remuxes),
    // `-c:a copy` under the mp4 muxer aborts with "codec not currently supported in container"
    // and the whole re-encode fails. Zero safety net before this list existed — the fix silently
    // failed every 30 min forever until the user gave up and switched target to MKV.
    // Whitelist covers the codecs the mp4/ipod muxer handles cleanly across current ffmpeg
    // releases (7.0+) — aac (universal), mp3, ac3/eac3 (Dolby Digital / +),  alac (Apple lossless
    // stored natively in mp4), and opus (Jellyfin 10.9+, ffmpeg 4.4+). Everything else — truehd,
    // dts family, dts-hd-ma, flac (limited support that keeps changing across ffmpeg builds),
    // pcm_bluray, vorbis, speex, wmav*, ralf — is treated as incompatible.

    // Subtitle codecs the Matroska (mkv/webm) muxer refuses under -c copy. Bug-pattern audit
    // round 3 (2026-09-19): MKV is permissive — it accepts SRT, ASS/SSA, PGS, DVB, DVD subs — so
    // this is a short blacklist rather than a whitelist. The two codecs listed are MP4-native
    // timed-text formats: tx3g is 3GPP text (MP4/mov container native) and mov_text is MP4's
    // internal timed-text format. MKV has no equivalent tag mapping and ffmpeg's matroska muxer
    // aborts with "Subtitle codec <name> not supported in Matroska" under -c copy. Used by both
    // TrackFixer's fold-in for MKV targets AND PlayabilityFixer rung 4's MKV re-encode, so
    // sub-tracks in that shape drop cleanly instead of failing the whole remux.
    private static readonly HashSet<string> MkvIncompatibleSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "tx3g", "mov_text"
    };

    private static readonly HashSet<string> MkvFamilyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "webm"
    };

    // MPEG-TS family. Bug-pattern audit round 3 (2026-09-19): .ts / .m2ts / .mts sources hit
    // PlayabilityFixer rung 1's `-map 0 -c copy` and abort with "Subtitle codec 17 is not
    // supported" when a Blu-ray rip carries `hdmv_pgs_subtitle`. Rung 3's MKV coerce recovers
    // but resets Jellyfin's watch history for the title. Adding the mpegts-family fold-in lets
    // rung 1 succeed for the common .ts + PGS case (drop the PGS, keep the container) so the
    // user's watch position survives the repair.
    private static readonly HashSet<string> MpegTsFamilyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ts", "m2ts", "mts", "mpegts"
    };

    // Subtitle codecs the mpegts muxer refuses under -c copy. Small blacklist: mpegts natively
    // supports dvb_subtitle / dvb_teletext only. Everything else (PGS, DVD, tx3g, mov_text,
    // subrip, ass, ssa) requires transcoding to a supported codec — which `-c copy` won't do,
    // so we drop them at fold-in time. Bitmap subs (PGS especially) are the common offender.
    private static readonly HashSet<string> MpegTsIncompatibleSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub",
        "dvd_subtitle", "dvdsub",
        "hdmv_text_subtitle",
        "mov_text", "tx3g",
        "subrip", "srt",
        "ass", "ssa",
        "webvtt",
        "xsub",
        string.Empty
    };

    /// <summary>The audio codecs the mp4/mov/ipod muxer accepts under <c>-c copy</c>. See the
    /// leading comment for the field for full rationale (bug-pattern audit round 2).</summary>
    public static readonly HashSet<string> Mp4CompatibleAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "mp3", "ac3", "eac3", "alac", "opus"
    };

    private readonly FfprobeService _ffprobe;
    private readonly FfmpegExecutor _ffmpeg;
    private readonly OutputVerifier _verifier;
    private readonly LibraryGuard _guard;
    private readonly RecycleBin _recycleBin;
    private readonly VerifiedReplacementService _replacement;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILibraryManager? _libraryManager;
    // External audit P1 #4: commit-time protection check so a Never-modify rule added during
    // a multi-hour remux aborts the swap. Nullable — the test ctors below don't inject it, and
    // production DI auto-wires it via the primary ctor.
    private readonly ProtectionService? _protection;
    private readonly ILogger<TrackFixer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackFixer"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="ffmpeg">The ffmpeg executor.</param>
    /// <param name="verifier">The output verifier.</param>
    /// <param name="guard">The library path guard.</param>
    /// <param name="recycleBin">The recycle bin. Still used for the external subtitle sidecar
    /// pass (separate effect, produces `AdditionalRecycled` rows for restore); the primary
    /// source recycle now flows through <see cref="VerifiedReplacementService"/>.</param>
    /// <param name="replacement">03-C: the shared verify → promote → dispose → commit protocol.</param>
    /// <param name="configProvider">02-A snapshot provider. Used for the pre-commit policy
    /// recheck so a dry-run flip mid-remux aborts the commit before touching the source.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="protection">Protection service for commit-time re-checks (external audit P1 #4); DI auto-wires it.</param>
    /// <param name="libraryManager">Resolves current metadata and external subtitle languages.</param>
    public TrackFixer(
        FfprobeService ffprobe,
        FfmpegExecutor ffmpeg,
        OutputVerifier verifier,
        LibraryGuard guard,
        RecycleBin recycleBin,
        VerifiedReplacementService replacement,
        IPluginConfigurationProvider configProvider,
        ILibraryMonitor libraryMonitor,
        ILogger<TrackFixer> logger,
        ProtectionService? protection = null,
        ILibraryManager? libraryManager = null)
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
    public bool CanFix(IssueType type) => type is IssueType.AudioLanguage or IssueType.SubtitleLanguage;

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
        if (probe?.Streams is null || probe.Error is not null)
        {
            return FixResult.Fail(FixReason.ProbeUnavailable, "The file could not be analyzed; it may be broken.");
        }

        var item = issue.ItemId == Guid.Empty ? null : _libraryManager?.GetItemById(issue.ItemId);
        var removeIndexes = ComputeRemovableIndexes(probe, issue.Type, config, item);
        var externalFiles = issue.Type == IssueType.SubtitleLanguage ? GetCurrentExternalFiles(issue, item, probe, config) : [];

        if (removeIndexes.Count == 0 && externalFiles.Count == 0)
        {
            return FixResult.NoChange(FixReason.NothingToRemove, "Nothing to remove any more — the file may have changed since the scan. Re-scan to refresh.");
        }

        var originalSize = new FileInfo(issue.Path).Length;
        var disposal = config.GetDisposal(issue.Type);
        var actionText = BuildActionText(issue, removeIndexes.Count, externalFiles.Count, disposal);
        return await RunTrackRemuxAsync(issue, probe, removeIndexes, externalFiles, disposal, actionText, originalSize, config, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the combined case where the SAME file has both an AudioLanguage AND a SubtitleLanguage
    /// issue queued at the same time. Instead of two ffmpeg remuxes back-to-back (each reading the
    /// source in full, each writing an intermediate, each moving the previous copy to the bin), this
    /// path runs ONE remux that drops both categories in one shot. Cuts wall time roughly in half on
    /// TV episodes and by 60–70 % on Blu-ray remuxes.
    /// <para>
    /// Returns a single <see cref="FixResult"/> covering both issues. The caller is responsible for
    /// marking both issues Fixed and recording per-issue history rows (typically one Fix history row
    /// per issue, both pointing at the same bin entry, so both show a Restore button).
    /// </para>
    /// </summary>
    /// <param name="audioIssue">The AudioLanguage issue.</param>
    /// <param name="subtitleIssue">The SubtitleLanguage issue on the same path.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="isStillApproved">Rechecks both queued approvals before replacing the source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A single result covering both fixes.</returns>
    public async Task<FixResult> FixCombinedAsync(Issue audioIssue, Issue subtitleIssue, IProgress<double>? progress, Func<bool> isStillApproved, CancellationToken cancellationToken)
    {
        if (audioIssue.Type != IssueType.AudioLanguage || subtitleIssue.Type != IssueType.SubtitleLanguage)
        {
            return FixResult.Refused(FixReason.InvalidCombinedRequest, "Combined pass requires exactly one AudioLanguage + one SubtitleLanguage issue.");
        }

        if (!string.Equals(audioIssue.Path, subtitleIssue.Path, StringComparison.OrdinalIgnoreCase))
        {
            return FixResult.Refused(FixReason.InvalidCombinedRequest, "Combined pass requires both issues to point at the same file.");
        }

        var config = _configProvider.GetSnapshot();
        var policy = EvaluateCombinedPolicy(audioIssue, subtitleIssue, config, isStillApproved);
        if (!policy.Allowed)
        {
            return FixResult.Refused(policy.Reason, "Combined track cleanup is no longer authorized; the original is untouched.");
        }

        if (!File.Exists(audioIssue.Path))
        {
            return FixResult.Fail(FixReason.SourceMissing, "The file no longer exists; re-scan to refresh the list.");
        }

        if (!_guard.IsInsideLibrary(audioIssue.Path))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "The file is outside your library folders; MediaDash will not touch it.");
        }

        var probe = await _ffprobe.ProbeAsync(audioIssue.Path, cancellationToken).ConfigureAwait(false);
        if (probe?.Streams is null || probe.Error is not null)
        {
            return FixResult.Fail(FixReason.ProbeUnavailable, "The file could not be analyzed; it may be broken.");
        }

        // Union of both categories' removeIndexes. Duplicates shouldn't happen (audio and subtitle
        // streams live in disjoint index ranges) but DE-dupe as belt-and-braces.
        var item = audioIssue.ItemId == Guid.Empty ? null : _libraryManager?.GetItemById(audioIssue.ItemId);
        var audioRemoves = ComputeRemovableIndexes(probe, IssueType.AudioLanguage, config, item);
        var subtitleRemoves = ComputeRemovableIndexes(probe, IssueType.SubtitleLanguage, config, item);
        var removeIndexes = audioRemoves.Concat(subtitleRemoves).Distinct().ToList();
        var externalFiles = GetCurrentExternalFiles(subtitleIssue, item, probe, config);

        if (removeIndexes.Count == 0 && externalFiles.Count == 0)
        {
            return FixResult.NoChange(FixReason.NothingToRemove, "Nothing to remove any more — the file may have changed since the scan. Re-scan to refresh.");
        }

        var originalSize = new FileInfo(audioIssue.Path).Length;
        // Both issues' disposals should be the same for a same-file combined pass; if they diverge,
        // prefer the more conservative (RecycleBin over PermanentDelete). The audio disposal drives.
        var audioDisposal = config.GetDisposal(IssueType.AudioLanguage);
        var subtitleDisposal = config.GetDisposal(IssueType.SubtitleLanguage);
        var disposal = audioDisposal == DisposalMethod.RecycleBin || subtitleDisposal == DisposalMethod.RecycleBin
            ? DisposalMethod.RecycleBin
            : audioDisposal;

        var actionText = string.Format(
            CultureInfo.InvariantCulture,
            "Removed {0} unwanted audio track{1} and {2} unwanted subtitle track{3} in one pass{4}{5}.",
            audioRemoves.Count,
            audioRemoves.Count == 1 ? string.Empty : "s",
            subtitleRemoves.Count,
            subtitleRemoves.Count == 1 ? string.Empty : "s",
            externalFiles.Count > 0 ? " + " + externalFiles.Count + " external subtitle sidecar" + (externalFiles.Count == 1 ? string.Empty : "s") : string.Empty,
            disposal == DisposalMethod.RecycleBin ? " (originals recycled)" : " (originals deleted)");
        return await RunTrackRemuxAsync(audioIssue, probe, removeIndexes, externalFiles, disposal, actionText, originalSize, config, progress, cancellationToken, subtitleIssue, isStillApproved).ConfigureAwait(false);
    }

    internal PolicyDecision EvaluateCombinedPolicy(Issue audio, Issue subtitle, PluginConfiguration config, Func<bool> isStillApproved)
    {
        if (!isStillApproved())
        {
            return PolicyDecision.Refuse(FixReason.ConsentChanged);
        }

        if (config.DryRun || !TranscodeFixer.IsTypeAuthorized(config, audio.Type) || !TranscodeFixer.IsTypeAuthorized(config, subtitle.Type))
        {
            return PolicyDecision.Refuse(FixReason.TypeDisabled);
        }

        return _protection?.IsFixerBlocked(audio.Path, audio.Type) == true || _protection?.IsFixerBlocked(subtitle.Path, subtitle.Type) == true
            ? PolicyDecision.Refuse(FixReason.Protected)
            : PolicyDecision.Allow;
    }

    private async Task<FixResult> RunTrackRemuxAsync(
        Issue issue,
        FfprobeData probe,
        List<int> removeIndexes,
        List<string> externalFiles,
        DisposalMethod disposal,
        string actionText,
        long originalSize,
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        Issue? combinedSubtitle = null,
        Func<bool>? isStillApproved = null)
    {
        if (config.DryRun)
        {
            return FixResult.DryRun(actionText, issue.SizeSavings);
        }

        string? recyclePath = null;
        long freed = 0;
        var languageRemoves = removeIndexes.ToArray();

        var ext = Path.GetExtension(issue.Path).TrimStart('.');
        if (removeIndexes.Count > 0 && string.IsNullOrEmpty(TranscodeFixer.ContainerToFfmpegFormat(ext)))
        {
            return FixResult.Refused(FixReason.UnsupportedFormat, "This container cannot be safely remuxed: " + ext);
        }

        // MP4-family source with bitmap subtitles present: the ipod muxer refuses dvd_subtitle /
        // hdmv_pgs_subtitle / dvb_subtitle under -c copy, and any remux against those streams
        // aborts with "Could not find tag for codec ... in stream #0:N, codec not currently
        // supported in container". Scanners have already attached a blocking warning
        // (data-loss consent gate) for these files, so we only reach this fold-in AFTER the
        // user has manually approved from the Issues tab. Fold the incompatible sub indexes
        // into removeIndexes so the negative-map list drops them at remux time — the remux
        // now succeeds instead of the ipod muxer refusing the whole file. HashSet-dedup avoids
        // double-mapping if the language filter already included the same subtitle stream.
        var droppedIncompatibleCount = 0;
        var incompatibleIndexes = ComputeMuxerIncompatibleSubtitleIndexes(probe, ext);
        if (incompatibleIndexes.Count > 0)
        {
            var existing = new HashSet<int>(removeIndexes);
            foreach (var idx in incompatibleIndexes)
            {
                if (existing.Add(idx))
                {
                    removeIndexes.Add(idx);
                    droppedIncompatibleCount++;
                }
            }
        }

        if (removeIndexes.Count > 0)
        {
            // Per-run GUID on both sidecars, matching TranscodeFixer post-P1: a hot-reload leaving
            // an old ffmpeg mid-remux (past SweepOrphanSidecars' 5-min age guard) mustn't collide
            // with a fresh run on the same target. Without the token, the fresh run's finally
            // block below unconditionally File.Delete's tempPath — and if the old run had already
            // promoted temp→swap and disposed the original, the delete lands on the user's only
            // remaining copy. Substring keeps "mediadash.tmp" / "mediadash.new" so the sweep and
            // LibraryGuard sidecar patterns still match for orphan cleanup.
            var runToken = Guid.NewGuid().ToString("N")[..8];
            var tempPath = TranscodeFixer.SidecarPath(issue.Path, "tmp." + runToken, ext);
            // CLAUDE.md safety invariant 5: rebuild fixers require 2× source-size free. Same
            // temp-alongside-original pattern as TranscodeFixer; fails closed if the drive can't
            // be resolved.
            if (disposal == DisposalMethod.RecycleBin && !_replacement.CanRecycle(issue.Path))
            {
                return FixResult.Refused(FixReason.AccessDenied, "No safe recycle location is available for the original.");
            }

            var rebuildShortfall = FreeSpaceGuard.DescribeShortfall(issue.Path, multiplier: 2);
            if (rebuildShortfall is not null)
            {
                return FixResult.Fail(FixReason.InsufficientSpace, "Cannot rebuild this file — " + rebuildShortfall + ".");
            }

            // Issue #39: users on multi-language Blu-ray remuxes (Breaking Bad 3xRus / Ger dubs,
            // Transporter .m2ts) hit two related failure modes on -c copy:
            //   - "sample rate not set" / "Could not write header" — the mpegts / .m2ts muxer
            //     refuses to write a header when the input demuxer hasn't populated per-stream
            //     start times.
            //   - Duration mismatch of ~30 s on a 45-min episode — the remux drops leading
            //     negative PTS packets, and the container duration comes out short.
            // -avoid_negative_ts make_zero shifts every packet so t=0 is the first-frame PTS,
            // which lets the muxer write a valid header and stops the duration drift.
            // -fflags +genpts regenerates missing PTS on demux, covering broken source files
            // where individual streams have gaps.
            var args = new List<string> { "-fflags", "+genpts", "-i", issue.Path, "-map", "0" };
            foreach (var index in removeIndexes)
            {
                args.Add("-map");
                args.Add(string.Format(CultureInfo.InvariantCulture, "-0:{0}", index));
            }

            // Encoder-quirks audit Q16: DaVinci Resolve MP4/MOV exports carry a codec_type=data
            // codec_tag=tmcd timecode track. Under -map 0 -c copy some ipod/mp4 muxer combos
            // abort with "Could not find tag for codec bin_data in stream #0:N". MKV and MPEG-TS
            // muxers accept the data stream fine — restrict the negative-map to MP4-family
            // targets so we don't strip timecodes users might want on other containers.
            if (Mp4FamilyExtensions.Contains(ext))
            {
                args.Add("-map");
                args.Add("-0:d");
            }

            // R09 (2026-09-20): sidecar output tail is `.mediaDash.tmp` — pass -f explicitly.
            // Track remux keeps the source container, so use the source ext as target format.
            args.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero", "-f", TranscodeFixer.ContainerToFfmpegFormat(ext), tempPath]);

            // 03-C: capture source identity BEFORE ffmpeg runs so the pre-commit recheck can
            // refuse a stale-source mutation. Length + mtime, not cryptographic identity.
            var srcInfoForRequest = new FileInfo(issue.Path);
            var expectedSourceLength = srcInfoForRequest.Exists ? srcInfoForRequest.Length : 0;
            var expectedSourceMtimeTicks = srcInfoForRequest.Exists ? srcInfoForRequest.LastWriteTimeUtc.Ticks : 0;

            try
            {
                // First pass suppresses the Ffmpeg.Timeout diagnostic: hitting the 30-min cap on a large
                // Blu-ray remux is expected and the retry usually finishes it. Only the retry's failure
                // (a genuine 5-hour miss) surfaces to the Errors tab. Track.RemuxRetry itself was removed —
                // a happy-path recovery event was noise in Errors, not signal.
                var error = await _ffmpeg.RunAsync(args, RemuxTimeout, cancellationToken, recordDiagnosticOnTimeout: false).ConfigureAwait(false);
                if (error is not null && FfmpegExecutor.IsTimeoutError(error))
                {
                    _logger.LogInformation("Track remux hit the {InitialTimeout} limit on '{Path}'; retrying with {RetryTimeout}.", RemuxTimeout, issue.Path, RemuxRetryTimeout);
                    error = await _ffmpeg.RunAsync(args, RemuxRetryTimeout, cancellationToken).ConfigureAwait(false);
                }

                if (error is not null)
                {
                    return FixResult.Fail(FixReason.ProcessFailed, "Rebuilding the file failed; the original is untouched. Details: " + TranscodeFixer.Truncate(error));
                }

                var verifyError = await _verifier.VerifyAsync(probe, issue.Path, tempPath, cancellationToken).ConfigureAwait(false);
                if (verifyError is not null)
                {
                    return FixResult.Fail(FixReason.VerificationFailed, "The rebuilt file failed verification; the original is untouched. Details: " + verifyError);
                }

                var outputProbe = await _ffprobe.ProbeAsync(tempPath, cancellationToken).ConfigureAwait(false);
                if (!RetainedTracksMatch(probe, outputProbe, removeIndexes))
                {
                    return FixResult.Fail(FixReason.VerificationFailed, "The rebuilt file did not preserve the expected audio and subtitle tracks; the original is untouched.");
                }

                // 03-C: hand the verified remux to VerifiedReplacementService. Same-path swap
                // (destination == source) — TrackFixer only ever rebuilds in place. External
                // sidecar loop below is a separate effect (own `AdditionalRecycled` bookkeeping).
                var request = new ReplacementRequest(
                    SourcePath: issue.Path,
                    OutputPath: tempPath,
                    DestinationPath: issue.Path,
                    ExpectedSourceLength: expectedSourceLength,
                    ExpectedSourceMtimeUtcTicks: expectedSourceMtimeTicks,
                    RecycleOriginal: disposal == DisposalMethod.RecycleBin,
                    PreserveSourceTimestamps: true);

                var replacementResult = await _replacement.ReplaceAsync(
                    request,
                    verifyOutput: _ => Task.FromResult<string?>(null),
                    checkCurrentPolicy: () =>
                    {
                        // External audit P1 #4: previously this only checked DryRun. Honour the
                        // full policy stack — a protection rule or fixer-off switch flipped during
                        // a multi-hour remux must now abort the swap.
                        var currentConfig = _configProvider.GetSnapshot();
                        if (combinedSubtitle is not null)
                        {
                            var combinedPolicy = EvaluateCombinedPolicy(issue, combinedSubtitle, currentConfig, isStillApproved!);
                            if (!combinedPolicy.Allowed)
                            {
                                return combinedPolicy;
                            }
                        }

                        if (currentConfig.DryRun)
                        {
                            return PolicyDecision.Refuse(FixReason.TypeDisabled);
                        }

                        if (!TranscodeFixer.IsTypeAuthorized(currentConfig, issue.Type))
                        {
                            return PolicyDecision.Refuse(FixReason.TypeDisabled);
                        }

                        if (_protection is not null && _protection.IsFixerBlocked(issue.Path, issue.Type))
                        {
                            return PolicyDecision.Refuse(FixReason.Protected);
                        }

                        var currentItem = issue.ItemId == Guid.Empty ? null : _libraryManager?.GetItemById(issue.ItemId);
                        var stillRemovable = ComputeRemovableIndexes(probe, IssueType.AudioLanguage, currentConfig, currentItem)
                            .Concat(ComputeRemovableIndexes(probe, IssueType.SubtitleLanguage, currentConfig, currentItem)).ToHashSet();
                        if (languageRemoves.Any(i => !stillRemovable.Contains(i)))
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
                        Api.Diagnostics.Record(
                            "Track.SwapAborted",
                            replacementResult.DescribeRecovery(issue.Path));
                        return FixResult.Fail(FixReason.RecoveryRequired, replacementResult.DescribeRecovery(issue.Path), replacementResult.TechnicalDetail);
                    }

                    if (replacementResult.Reason == FixReason.SourceChanged)
                    {
                        return FixResult.Fail(FixReason.SourceChanged, "The source file changed while it was being rebuilt; the rebuilt copy was discarded and the changed source was preserved.", replacementResult.TechnicalDetail);
                    }

                    if (replacementResult.Reason == FixReason.Cancelled)
                    {
                        return FixResult.Fail(FixReason.Cancelled, "Track rebuild was cancelled; the original is untouched.", replacementResult.TechnicalDetail);
                    }

                    return FixResult.Fail(replacementResult.Reason, "Track rebuild could not be committed: " + (replacementResult.TechnicalDetail ?? replacementResult.Reason.ToString()), replacementResult.TechnicalDetail);
                }

                recyclePath = replacementResult.RecyclePath;
                freed += originalSize - new FileInfo(issue.Path).Length;
            }
            finally
            {
                // Service takes ownership of tempPath once it promotes; on any pre-promote refusal
                // tempPath stays where ffmpeg wrote it and this finally cleans it up.
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "Could not delete temp remux file {Path}", tempPath);
                    }
                }
            }
        }

        var additionalRecycled = new List<RecycledSidecar>();
        foreach (var externalFile in externalFiles)
        {
            try
            {
                var currentConfig = _configProvider.GetSnapshot();
                var currentItem = issue.ItemId == Guid.Empty ? null : _libraryManager?.GetItemById(issue.ItemId);
                if (currentConfig.DryRun || !TranscodeFixer.IsTypeAuthorized(currentConfig, IssueType.SubtitleLanguage)
                    || (combinedSubtitle is not null && !EvaluateCombinedPolicy(issue, combinedSubtitle, currentConfig, isStillApproved!).Allowed)
                    || !GetCurrentExternalFiles(issue, currentItem, probe, currentConfig, externalFiles).Contains(externalFile)
                    || (_protection is not null && (_protection.IsFixerBlocked(issue.Path, IssueType.SubtitleLanguage)
                        || _protection.IsFixerBlocked(externalFile, IssueType.SubtitleLanguage))))
                {
                    continue;
                }

                // Defence-in-depth: refuse to touch the video path itself. If a stale DetailsJson (written by
                // an older scanner build) lists the video as an external sidecar, we'd otherwise recycle the
                // freshly-remuxed video and credit its size to freed.
                if (IsSelfReferentialSubtitle(externalFile, issue.Path))
                {
                    Api.Diagnostics.Record(
                        "Track.ExternalPathCollision",
                        "Refused to recycle '" + externalFile + "' as an external subtitle — path matches the video being fixed. Re-scan to rebuild the issue with the current scanner.");
                    continue;
                }

                if (!File.Exists(externalFile) || !_guard.IsInsideLibrary(externalFile))
                {
                    continue;
                }

                var sidecarSize = new FileInfo(externalFile).Length;
                if (disposal == DisposalMethod.RecycleBin)
                {
                    var sidecarBinPath = _recycleBin.MoveToBin(externalFile);
                    // A per-sidecar history row is what makes the Recycle Bin tab's Restore button appear.
                    // Without this row the file lives in the bin but shows "no history" — user report class.
                    additionalRecycled.Add(new RecycledSidecar
                    {
                        OriginalPath = externalFile,
                        RecyclePath = sidecarBinPath,
                        Action = "Recycled external subtitle sidecar during track fix of " + Path.GetFileName(issue.Path) + "."
                    });
                }
                else
                {
                    File.Delete(externalFile);
                }

                freed += sidecarSize;
                _libraryMonitor.ReportFileSystemChanged(externalFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove external subtitle {Path}; keeping the committed track result", externalFile);
            }
        }

        _libraryMonitor.ReportFileSystemChanged(issue.Path);
        _logger.LogInformation("Track fix: {Action}", actionText);
        if (droppedIncompatibleCount > 0)
        {
            _logger.LogInformation(
                "Track fix: dropped {Count} bitmap subtitle track(s) that the target MP4-family container cannot hold under -c copy (user pre-approved via blocking-warning consent gate).",
                droppedIncompatibleCount);
        }

        return new FixResult
        {
            Success = true,
            Outcome = FixOutcome.Applied,
            Message = actionText,
            BytesFreed = Math.Max(0, freed),
            RecyclePath = recyclePath,
            AdditionalRecycled = additionalRecycled
        };
    }

    internal static bool RetainedTracksMatch(FfprobeData source, FfprobeData? output, IReadOnlyCollection<int> removed, bool allowAudioCodecChange = false)
    {
        if (output?.Streams is null || output.Error is not null)
        {
            return false;
        }

        var expected = source.Streams!.Where(s => !removed.Contains(s.Index));
        IEnumerable<(string?, string?, string, bool, bool)> Tracks(IEnumerable<FfprobeStreamInfo> streams)
            => streams.Where(s => s.CodecType is "audio" or "subtitle")
                .Select(s => (s.CodecType, allowAudioCodecChange && s.CodecType == "audio" ? null : s.CodecName, LanguageHelper.Normalize(s.Language), s.IsOriginal, s.IsHearingImpaired));
        return Tracks(expected).SequenceEqual(Tracks(output.Streams));
    }

    private static List<string> GetCurrentExternalFiles(Issue issue, BaseItem? item, FfprobeData probe, PluginConfiguration config, IReadOnlyList<string>? plannedFiles = null)
    {
        var allowed = LanguageHelper.ResolveLanguages(config.AllowedSubtitleLanguages, item, probe);
        if (item is null || !string.Equals(item.Path, issue.Path, StringComparison.OrdinalIgnoreCase) || allowed is null || allowed.Length == 0)
        {
            return [];
        }

        var candidates = (plannedFiles ?? GetExternalFiles(issue.DetailsJson)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return item.GetMediaStreams()
            .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !string.IsNullOrEmpty(s.Path)
                && candidates.Contains(s.Path) && !IsSelfReferentialSubtitle(s.Path, issue.Path)
                && !LanguageHelper.IsAllowed(s.Language, allowed) && !(config.SubtitleHearingImpairedMode && s.IsHearingImpaired))
            .Select(s => s.Path!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static List<int> ComputeRemovableIndexes(FfprobeData probe, IssueType type, PluginConfiguration config, BaseItem? item = null)
    {
        var allowed = LanguageHelper.ResolveLanguages(type == IssueType.AudioLanguage ? config.AllowedAudioLanguages : config.AllowedSubtitleLanguages, item, probe);
        if (allowed is null || allowed.Length == 0 || probe.Streams is null)
        {
            return [];
        }

        if (type == IssueType.AudioLanguage)
        {
            var audio = probe.Streams!.Where(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)).ToList();
            var keep = audio.Where(t => LanguageHelper.IsAllowed(t.Language, allowed)).ToList();
            if (audio.Count <= 1 || keep.Count == 0)
            {
                // Safety invariant: never remove the last audio track or all allowed tracks.
                return [];
            }

            return audio.Where(t => !LanguageHelper.IsAllowed(t.Language, allowed)).Select(t => t.Index).ToList();
        }

        return probe.Streams!
            .Where(s => string.Equals(s.CodecType, "subtitle", StringComparison.OrdinalIgnoreCase)
                && !LanguageHelper.IsAllowed(s.Language, allowed)
                && !(config.SubtitleHearingImpairedMode && s.IsHearingImpaired))
            .Select(s => s.Index)
            .ToList();
    }

    /// <summary>
    /// True when an "external subtitle" path from the scanner's DetailsJson is actually the video file
    /// being fixed. Jellyfin can report embedded PGS/Bluray tracks with IsExternal=true and Path pointing
    /// at the container itself; without this guard the remuxed video would be recycled and its size
    /// double-counted into freed bytes. Exposed internal for direct unit testing.
    /// </summary>
    /// <param name="externalPath">Path from the scanner's externalFiles list.</param>
    /// <param name="videoPath">The Issue.Path (the video being fixed).</param>
    /// <returns>True when the two paths refer to the same file.</returns>
    internal static bool IsSelfReferentialSubtitle(string externalPath, string videoPath)
    {
        if (string.IsNullOrEmpty(externalPath) || string.IsNullOrEmpty(videoPath))
        {
            return false;
        }

        // Normalise both sides — legacy scanner rows can store relative paths, trailing separators,
        // or Windows 8.3-form paths that would slip past a raw string comparison and let the
        // just-remuxed video get recycled by the fixer.
        try
        {
            var externalFull = Path.GetFullPath(externalPath);
            var videoFull = Path.GetFullPath(videoPath);
            return string.Equals(externalFull, videoFull, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // Fall back to the strict compare on unusable paths — the guard should refuse deletion
            // rather than incorrectly say "not the video".
            return string.Equals(externalPath, videoPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static List<string> GetExternalFilesForTest(string detailsJson) => GetExternalFiles(detailsJson);

    /// <summary>
    /// Classifier: returns the subtitle streams from <paramref name="probe"/> that the target
    /// MP4-family container cannot hold under a copy-only remux. Empty for non-MP4-family
    /// containers or when no bitmap subs are present. Used by scanners to attach a data-loss
    /// warning before the FixTask promotes the issue to Queued.
    /// </summary>
    /// <param name="probe">The ffprobe result.</param>
    /// <param name="ext">The target extension (with or without leading dot).</param>
    /// <returns>The streams the muxer will drop.</returns>
    public static IReadOnlyList<FfprobeStreamInfo> ComputeMuxerIncompatibleSubtitleStreams(FfprobeData probe, string ext)
    {
        var normalized = (ext ?? string.Empty).TrimStart('.');
        if (probe.Streams is null)
        {
            return Array.Empty<FfprobeStreamInfo>();
        }

        // Bug-pattern audit round 3 (2026-09-19): MKV target used to fall through here as
        // "no incompatible subs" and PlayabilityFixer rung 4 + TrackFixer both hit ffmpeg's
        // "Subtitle codec tx3g not supported in Matroska" mid-encode. MP4 stays on the
        // whitelist (short list of what works); MKV uses the blacklist (short list of what
        // doesn't) — different shapes, same predicate contract.
        if (Mp4FamilyExtensions.Contains(normalized))
        {
            return probe.Streams
                .Where(s => string.Equals(s.CodecType, "subtitle", StringComparison.OrdinalIgnoreCase)
                    && !Mp4CompatibleSubtitleCodecs.Contains(s.CodecName ?? string.Empty))
                .ToList();
        }

        if (MkvFamilyExtensions.Contains(normalized))
        {
            // MEDIA-01 / #75: a subtitle stream whose codec_name ffprobe can't classify (e.g.
            // Matroska CodecID S_TEXT/WEBVTT, which FFmpeg's Matroska demuxer doesn't tag — only
            // D_WEBVTT/* is recognised) renders as `codec_name = ""`. Previously that empty value
            // slipped past the small blacklist and the Matroska muxer aborted the whole remux with
            // "Subtitle codec none (0) is not supported." Treat null/empty codec names as unknown
            // and therefore unsafe to copy — same "safer to drop an unclassifiable stream" rule
            // the MP4 whitelist already enforces by construction.
            return probe.Streams
                .Where(s => string.Equals(s.CodecType, "subtitle", StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(s.CodecName)
                        || MkvIncompatibleSubtitleCodecs.Contains(s.CodecName!)))
                .ToList();
        }

        if (MpegTsFamilyExtensions.Contains(normalized))
        {
            return probe.Streams
                .Where(s => string.Equals(s.CodecType, "subtitle", StringComparison.OrdinalIgnoreCase)
                    && MpegTsIncompatibleSubtitleCodecs.Contains(s.CodecName ?? string.Empty))
                .ToList();
        }

        return Array.Empty<FfprobeStreamInfo>();
    }

    /// <summary>
    /// Index-only view of <see cref="ComputeMuxerIncompatibleSubtitleStreams"/>. Used by tests
    /// and by the fixer's future negative-map fold-in.
    /// </summary>
    /// <param name="probe">The ffprobe result.</param>
    /// <param name="ext">The target extension (with or without leading dot).</param>
    /// <returns>The stream indexes the muxer will drop.</returns>
    public static IReadOnlyList<int> ComputeMuxerIncompatibleSubtitleIndexes(FfprobeData probe, string ext)
        => ComputeMuxerIncompatibleSubtitleStreams(probe, ext).Select(s => s.Index).ToList();

    /// <summary>
    /// Returns the first audio-codec name the mp4/mov/ipod muxer would reject under <c>-c copy</c>
    /// for the given target extension. Callers use this as a pre-flight so TranscodeFixer can
    /// refuse a Quality re-encode to MP4 cleanly BEFORE ffmpeg is invoked, instead of failing
    /// mid-encode with a raw muxer error. Returns null for non-MP4 targets and for MP4 targets
    /// where every audio codec is on the compatible list. Bug-pattern audit round 2 (2026-09-19).
    /// </summary>
    /// <param name="probe">The ffprobe result.</param>
    /// <param name="ext">The target extension (with or without leading dot).</param>
    /// <returns>The offending codec name (e.g. "truehd", "dts", "flac"), or null when the target
    /// isn't MP4-family or all audio codecs would survive <c>-c copy</c>.</returns>
    public static string? FirstMp4IncompatibleAudioCodec(FfprobeData probe, string ext)
    {
        var normalized = (ext ?? string.Empty).TrimStart('.');
        if (!Mp4FamilyExtensions.Contains(normalized) || probe.Streams is null)
        {
            return null;
        }

        foreach (var stream in probe.Streams)
        {
            if (string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase)
                && !Mp4CompatibleAudioCodecs.Contains(stream.CodecName ?? string.Empty))
            {
                return string.IsNullOrEmpty(stream.CodecName) ? "unknown" : stream.CodecName;
            }
        }

        return null;
    }

    /// <summary>Exposes the MP4-family extension set so sibling fixers can gate MP4-only logic
    /// without duplicating the constant. Bug-pattern audit round 2 (2026-09-19).</summary>
    /// <param name="ext">The target extension (with or without leading dot).</param>
    /// <returns>True when the extension is one of the mp4/mov/ipod-family containers.</returns>
    public static bool IsMp4FamilyExtension(string ext)
        => Mp4FamilyExtensions.Contains((ext ?? string.Empty).TrimStart('.'));

    // 01-C: routes through IssueDetailsReader so a non-object root or wrong-typed
    // externalFiles field returns an empty list rather than partially parsing. Parsing
    // success is not authorization — every downstream external-file operation still runs
    // through IsSelfReferentialSubtitle + LibraryGuard.
    private static List<string> GetExternalFiles(string detailsJson)
    {
        return IssueDetailsReader.TryReadSubtitleCleanup(detailsJson, out var details) && details is not null
            ? new List<string>(details.ExternalFiles)
            : new List<string>();
    }

    private static string BuildActionText(Issue issue, int embeddedCount, int externalCount, DisposalMethod disposal)
    {
        var what = issue.Type == IssueType.AudioLanguage
            ? string.Format(CultureInfo.InvariantCulture, "removed {0} audio track(s)", embeddedCount)
            : string.Format(CultureInfo.InvariantCulture, "removed {0} subtitle track(s) and {1} subtitle file(s)", embeddedCount, externalCount);
        // Field report A9: users read the old "original kept in recycle bin" and thought MediaDash
        // was moving their media there for no reason. The truth is that track removal is a remux —
        // ffmpeg writes a new file next to the source, then the original file is swapped out. The
        // "original in recycle bin" is a safety copy of the pre-edit file, not a delete of the
        // media itself. Spell that out so the note reads as "the file you asked me to edit is fine,
        // your pre-edit copy is recoverable" instead of "your media just went to the bin".
        var keep = disposal == DisposalMethod.RecycleBin
            ? "file rebuilt at the same path; the pre-edit copy is in the recycle bin as a safety net"
            : "file rebuilt at the same path; the pre-edit copy was deleted permanently";
        return string.Format(CultureInfo.InvariantCulture, "{0} from {1} — {2}", what, Path.GetFileName(issue.Path), keep);
    }
}

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Flags unreadable media, missing required streams, and confirmed decode or interleave damage.
/// </summary>
public sealed class PlayabilityScanner : ProbingScannerBase
{
    private readonly Probing.BookProbeService _bookProbe;
    private readonly Probing.ComicProbeService _comicProbe;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayabilityScanner"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="bookProbe">The book probe service.</param>
    /// <param name="comicProbe">The comic probe service.</param>
    /// <param name="logger">The logger.</param>
    public PlayabilityScanner(FfprobeService ffprobe, Probing.BookProbeService bookProbe, Probing.ComicProbeService comicProbe, ILogger<PlayabilityScanner> logger)
        : base(ffprobe, logger)
    {
        _bookProbe = bookProbe;
        _comicProbe = comicProbe;
    }

    /// <inheritdoc />
    public override IssueType Type => IssueType.Playability;

    /// <inheritdoc />
    /// <remarks>Each file's checks are independent ffmpeg/ffprobe runs plus per-path cache rows.</remarks>
    protected override bool SupportsParallelFiles => true;

    /// <inheritdoc />
    protected override async Task<Issue?> EvaluateAsync(BaseItem item, string path, FfprobeData? probe, CancellationToken cancellationToken)
    {
        string? reason = null;
        string? detail = null;

        // technical: raw ffmpeg / ffprobe output (multi-line, decoder-speak). Kept separate
        // from `detail` so the History / Recycle bin / Issues UI can render the friendly line
        // as primary text and hide the raw dump behind a "Technical detail" disclosure.
        string? technical = null;

        if (!System.IO.File.Exists(path))
        {
            // Folder-based Jellyfin library entries — BDMV / VIDEO_TS / DVD rips, mounted ISO
            // trees, some Blu-ray "BDRemux" layouts — expose a directory path, not a file path.
            // File.Exists returns false but the item is fine and Jellyfin plays it (or, in the
            // DRM-protected case, correctly refuses to). Skip playability checks entirely for
            // these; the plugin can never usefully "repair" a folder tree, and reporting them
            // as missing files with a "run a library scan" hint is user-hostile (2026-09-14).
            if (System.IO.Directory.Exists(path))
            {
                return null;
            }

            return new Issue
            {
                DetailsJson = JsonSerializer.Serialize(new { reason = "missing", detail = "The library entry points to a file that no longer exists." }),
                SuggestedFix = "The file is gone but Jellyfin still lists it. Approve this fix to remove the dead library entry — or restore the file first if the deletion was accidental.",
                SizeSavings = 0
            };
        }

        // .strm files are text pointers to remote/network streams; ffprobe on the local text file
        // would always flag them as unreadable. Skip playability checks for them.
        if (string.Equals(System.IO.Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (item.GetBaseItemKind() == BaseItemKind.Book)
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            bool ok;
            string? probeReason;
            if (ext is ".cbz" or ".cbr" or ".cb7")
            {
                var cp = await _comicProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
                ok = cp.Ok;
                probeReason = cp.Reason;
            }
            else
            {
                var bp = await _bookProbe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
                ok = bp.Ok;
                probeReason = bp.Reason;
            }

            if (ok)
            {
                return null;
            }

            long bookSize = 0;
            try
            {
                bookSize = new System.IO.FileInfo(path).Length;
            }
            catch (System.IO.IOException)
            {
            }

            // Reason-prefix routing (ebook-comic-quirks-audit Q4/Q5/Q19/Q20/Q22):
            //   drm:<message>                   → informational-only Issue, no repair, no delete
            //   mismatch:<ext>:<message>        → rename fix (same shape as F-213 for video)
            //   anything else                   → today's book-or-comic-corrupt repair-or-delete flow
            if (probeReason is not null && probeReason.StartsWith("drm:", StringComparison.Ordinal))
            {
                return new Issue
                {
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        reason = "drm-protected",
                        detail = probeReason.Substring("drm:".Length)
                    }),
                    SuggestedFix = "This file is DRM-protected — MediaDash cannot repair it. Ignore this to hide the notification, or remove the file manually.",
                    SizeSavings = bookSize
                };
            }

            if (probeReason is not null && probeReason.StartsWith("mismatch:", StringComparison.Ordinal))
            {
                var parts = probeReason.Split(':', 3);
                if (parts.Length == 3)
                {
                    return new Issue
                    {
                        DetailsJson = JsonSerializer.Serialize(new
                        {
                            reason = "container-extension-mismatch",
                            detail = parts[2],
                            rename_to_ext = parts[1]
                        }),
                        SuggestedFix = "The file plays but its extension doesn't match its container. Approve to rename it to a matching extension — the file is not moved and is not deleted.",
                        SizeSavings = bookSize
                    };
                }
            }

            return new Issue
            {
                DetailsJson = JsonSerializer.Serialize(new { reason = "book-or-comic-corrupt", detail = probeReason, fixerAvailable = false }),
                SuggestedFix = "This document can't be read. Document repair is unavailable; Ignore to keep it or delete it from Files.",
                SizeSavings = bookSize
            };
        }

        if (probe is null || FfprobeService.IsInfrastructureError(probe.Error?.Message))
        {
            // ffprobe unavailable — infrastructure problem, not a file problem.
            return null;
        }

        // DRM-protected files (iTunes Fairplay, Widevine, PlayReady) surface encrypted sample
        // entries as codec_tag_string="encv" (encrypted video) or "enca" (encrypted audio) under
        // ISO base media file format. The repair ladder can never decrypt them, so route them to
        // an informational-only Issue instead of the standard "can't play, will repair" flow:
        // the user should know MediaDash saw the file, but Approve does nothing destructive.
        if (probe.Streams is not null && probe.Streams.Any(s =>
                string.Equals(s.CodecTagString, "encv", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.CodecTagString, "enca", StringComparison.OrdinalIgnoreCase)))
        {
            long drmSize = 0;
            try
            {
                drmSize = new System.IO.FileInfo(path).Length;
            }
            catch (System.IO.IOException)
            {
            }

            return new Issue
            {
                DetailsJson = JsonSerializer.Serialize(new
                {
                    reason = "drm-protected",
                    detail = "The file's video or audio streams are DRM-encrypted (iTunes Fairplay, Widevine, PlayReady or similar). MediaDash cannot decrypt or repair DRM."
                }),
                SuggestedFix = "This file is DRM-protected — MediaDash cannot repair it. Ignore this to hide the notification, or remove the file manually.",
                SizeSavings = drmSize
            };
        }

        string? renameToExt = null;
        if (probe.Error is not null || probe.Streams is null || probe.Streams.Count == 0)
        {
            reason = "unreadable";
            detail = "The file could not be read as a media file.";
            technical = probe.Error?.Message;
        }
        else if (IsContainerExtensionMismatch(path, probe, out var mismatchDetail, out renameToExt))
        {
            reason = "container-extension-mismatch";
            detail = mismatchDetail;
        }
        else if (!IsAudioKind(item.GetBaseItemKind())
            && !probe.Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "no-video";
            detail = "The file contains no video stream.";
        }
        else if (IsAudioKind(item.GetBaseItemKind())
            && !probe.Streams.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "no-audio";
            detail = "The file contains no audio stream.";
        }
        else if (!Config.ThoroughPlayabilityCheck && item.GetBaseItemKind() != BaseItemKind.Photo)
        {
            var decodeError = await Ffprobe.CheckSampledPlaybackAsync(path, probe, cancellationToken).ConfigureAwait(false);
            if (decodeError is not null)
            {
                reason = "decode-error";
                detail = "The decoder rejected part of the video or audio during a sampled playback check.";
                technical = decodeError;
            }
        }
        else if (Config.ThoroughPlayabilityCheck || item.GetBaseItemKind() == BaseItemKind.Photo)
        {
            probe.TryGetDuration(out var duration);
            // Decode-check is the source of truth for truncation and corruption. Removed a
            // bitrate-vs-size pre-filter here (2026-09-14) — it flagged efficient x264 rips of
            // Severance / Landman / Rage / Outlander as "size-truncated" because Format.BitRate
            // overestimated actual VBR content by 2×+. DecodeCheckAsync already catches genuine
            // truncation via the decoded-time shortfall heuristic (FfprobeService line ~227) with
            // far fewer false positives; keeping only that.
            // MEDIA-02 / issue #76: route audio items (Audio / AudioBook) through the audio-only
            // decode path so attached cover art pictures don't get selected as the "best video"
            // stream and reported as truncated-at-0s.
            var isAudioOnly = IsAudioKind(item.GetBaseItemKind());
            var decodeError = item.GetBaseItemKind() == BaseItemKind.Photo
                ? await Ffprobe.DecodeCheckAsync(path, durationSeconds: 0, cancellationToken, isAudioOnly).ConfigureAwait(false)
                : await Ffprobe.DecodeCheckAsync(path, duration, cancellationToken, isAudioOnly).ConfigureAwait(false);
            if (decodeError is not null)
            {
                reason = "decode-error";
                detail = isAudioOnly
                    ? "The audio stream is damaged and the decoder rejected part of it."
                    : "The video stream is damaged and the decoder rejected part of it.";
                technical = decodeError;
            }
            else
            {
                var brokenAudio = await Ffprobe.FindBrokenAudioStreamAsync(path, probe, useCache: true, cancellationToken).ConfigureAwait(false);
                if (brokenAudio is { } audio)
                {
                    reason = "decode-error";
                    detail = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Audio stream {0} ({1}) is damaged and the decoder rejected part of it.",
                        audio.Stream.Index,
                        audio.Stream.Language ?? audio.Stream.CodecName ?? "unknown");
                    technical = audio.Error;
                }
            }
        }

        if (reason is null)
        {
            return await EvaluateInterleaveAsync(item, path, probe, cancellationToken).ConfigureAwait(false);
        }

        long size = 0;
        try
        {
            size = new System.IO.FileInfo(path).Length;
        }
        catch (System.IO.IOException)
        {
        }

        // Extension mismatch is a hygiene issue, not a "won't play" case — the file plays fine.
        // Use a rename-focused suggested-fix and stash the target extension so the fixer can act
        // without re-probing.
        var isMismatch = reason == "container-extension-mismatch";
        var detailsJson = isMismatch
            ? JsonSerializer.Serialize(new { reason, detail, technical, rename_to_ext = renameToExt })
            : JsonSerializer.Serialize(new { reason, detail, technical });
        var suggestedFix = isMismatch
            ? "The file plays but its extension doesn't match its container. Approve to rename it to a matching extension — the file is not moved and is not deleted."
            : "Approve to try repair. If repair fails, the file is kept and returns here for you to review or delete.";
        return new Issue
        {
            DetailsJson = detailsJson,
            SuggestedFix = suggestedFix,
            SizeSavings = size
        };
    }

    // Secondary check for files that passed every other playability gate but hide a seek-time
    // failure: audio stored far from its video. Only runs with Thorough enabled, only on video
    // items, only when the primary checks found nothing. Indeterminate verdicts don't produce
    // issues — this is a scanner, not a "surface everything we're not sure about" alert.
    private async Task<Issue?> EvaluateInterleaveAsync(BaseItem item, string path, FfprobeData? probe, CancellationToken cancellationToken)
    {
        if (!Config.ThoroughPlayabilityCheck || IsAudioKind(item.GetBaseItemKind()) || probe is null)
        {
            return null;
        }

        var result = await Ffprobe.InterleaveCheckAsync(path, probe, cancellationToken).ConfigureAwait(false);
        if (result.Status != InterleaveStatus.Broken)
        {
            return null;
        }

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "Sound and picture are stored up to {0:F0} minutes apart inside this file. It plays from the start, but after skipping ahead it can stall or play without sound.",
            Math.Ceiling(result.OffsetSeconds / 60));
        return new Issue
        {
            DetailsJson = JsonSerializer.Serialize(new { reason = InterleaveAnalyzer.IssueReason, detail }),
            SuggestedFix = "Skipping ahead in this file can stall or lose sound. Approve to rewrite it with sound and picture stored side by side — nothing is re-encoded, and the file is left untouched if the rewrite fails.",
            SizeSavings = 0
        };
    }

    private static bool IsAudioKind(BaseItemKind kind)
        => kind == BaseItemKind.Audio || kind == BaseItemKind.AudioBook;

    /// <summary>
    /// F-213: flags files whose extension disagrees with their actual container. The file
    /// plays fine in Jellyfin's video pipeline either way — this is a hygiene issue and the
    /// fixer resolves it by renaming the file to a matching extension. Fires on:
    ///   - Any audio-only extension (.mp3, .aac, .flac, etc.) with a video stream (real
    ///     routing bug: Jellyfin sends it to the audio pipeline).
    ///   - Video extensions whose container doesn't match (e.g., YIFY-style
    ///     "x264 -YTS.mp4" that's actually HEVC-in-MKV — cosmetic, but the rename is
    ///     what the user asked for so future clients that ARE strict about container/
    ///     extension pairing don't get confused).
    /// The scanner emits a distinct <c>rename_to_ext</c> field in DetailsJson so the fixer
    /// knows the target extension without re-probing.
    /// </summary>
    private static bool IsContainerExtensionMismatch(string path, FfprobeData probe, out string detail, out string? renameToExt)
    {
        detail = string.Empty;
        renameToExt = null;

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant().TrimStart('.');
        var formatName = probe.Format?.FormatName?.ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || string.IsNullOrEmpty(formatName))
        {
            return false;
        }

        var demuxers = formatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extensionAliases = ext switch
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

        if (extensionAliases.Length == 0 || extensionAliases.Any(alias => demuxers.Contains(alias)))
        {
            return false;
        }

        // Map the first demuxer back to a canonical file extension so the fixer knows what to
        // rename to. Covers everything the extensionAliases map supports plus the audio ones.
        var canonicalExt = demuxers[0] switch
        {
            "matroska" or "webm" => "mkv",
            "mov" or "mp4" or "m4a" or "3gp" or "3g2" or "mj2" => "mp4",
            "avi" => "avi",
            "asf" => "wmv",
            "flv" => "flv",
            "mpegts" => "ts",
            "mpeg" or "mpegvideo" or "mpegps" => "mpg",
            "ogg" => "ogg",
            "mp3" => "mp3",
            "aac" => "aac",
            "flac" => "flac",
            "wav" => "wav",
            "ac3" => "ac3",
            "dts" => "dts",
            "aiff" => "aiff",
            _ => null
        };
        if (canonicalExt is null || string.Equals(canonicalExt, ext, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        renameToExt = canonicalExt;
        detail = string.Format(
            CultureInfo.InvariantCulture,
            "The file's extension is '.{0}' but its container is '{1}'. MediaDash can rename it to '.{2}' — approve to fix, or Ignore this if the current name is intentional.",
            ext,
            formatName,
            canonicalExt);
        return true;
    }
}

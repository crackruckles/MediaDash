using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Flags files carrying audio tracks in unwanted languages.
/// Never proposes removing the last audio track or all allowed tracks (safety invariant).
/// </summary>
public sealed class AudioLanguageScanner : ProbingScannerBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AudioLanguageScanner"/> class.
    /// </summary>
    /// <param name="ffprobe">The probe service.</param>
    /// <param name="logger">The logger.</param>
    public AudioLanguageScanner(FfprobeService ffprobe, ILogger<AudioLanguageScanner> logger)
        : base(ffprobe, logger)
    {
    }

    /// <inheritdoc />
    public override IssueType Type => IssueType.AudioLanguage;

    /// <inheritdoc />
    protected override bool IsConfigured() => Config.AllowedAudioLanguages.Length > 0;

    /// <inheritdoc />
    protected override Task<Issue?> EvaluateAsync(BaseItem item, string path, FfprobeData? probe, CancellationToken cancellationToken)
    {
        var audioTracks = probe?.Streams?
            .Where(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (audioTracks is null || audioTracks.Count <= 1)
        {
            // A single audio track is never removed, whatever its language.
            return Task.FromResult<Issue?>(null);
        }

        var allowed = Config.AllowedAudioLanguages;
        var keep = audioTracks.Where(t => LanguageHelper.IsAllowed(t.Language, allowed)).ToList();
        var remove = audioTracks.Where(t => !LanguageHelper.IsAllowed(t.Language, allowed)).ToList();
        if (remove.Count == 0 || keep.Count == 0)
        {
            // Nothing to remove, or removing would leave no allowed track — keep everything.
            return Task.FromResult<Issue?>(null);
        }

        // Data-loss pre-flight: TrackFixer's remux writes to the source's own container (same
        // extension). When that extension is MP4-family and the source carries bitmap subtitle
        // streams (VobSub / PGS / DVB), the fix will DROP those subs because mp4/ipod can't hold
        // them under -c copy. Silent drop is not acceptable — attach a blocking warning so the
        // FixTask auto-queue holds this issue as Detected and the user must consent via Approve.
        var warnings = BuildDataLossWarnings(probe!, path);

        var issue = new Issue
        {
            DetailsJson = JsonSerializer.Serialize(new
            {
                removeIndexes = remove.Select(t => t.Index).ToArray(),
                removeLanguages = remove.Select(t => LanguageHelper.Normalize(t.Language)).ToArray(),
                keepLanguages = keep.Select(t => LanguageHelper.Normalize(t.Language)).ToArray(),
                warnings
            }),
            SuggestedFix = string.Format(
                CultureInfo.InvariantCulture,
                "Remove {0} audio track(s) in {1}; keeps {2}.",
                remove.Count,
                string.Join(", ", remove.Select(t => LanguageHelper.Normalize(t.Language)).Distinct()),
                string.Join(", ", keep.Select(t => LanguageHelper.Normalize(t.Language)).Distinct())),
            SizeSavings = EstimateTrackBytes(probe!, remove)
        };
        return Task.FromResult<Issue?>(issue);
    }

    // Kept internal + static so SubtitleLanguageScanner can call the same builder — both scanners
    // emit issues that route through TrackFixer's same-container remux, so both hit the same
    // bitmap-sub incompatibility.
    internal static IssueWarning[] BuildDataLossWarnings(FfprobeData probe, string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var incompat = TrackFixer.ComputeMuxerIncompatibleSubtitleStreams(probe, ext);
        if (incompat.Count == 0)
        {
            return [];
        }

        var codecList = string.Join(", ", incompat.Select(s => s.CodecName).Distinct());
        var message = string.Format(
            CultureInfo.InvariantCulture,
            "This fix will also drop {0} bitmap subtitle track{1} ({2}) that the .{3} container cannot hold under a copy-only remux. Once approved, those tracks are gone — restore the original from the recycle bin within the retention window to get them back.",
            incompat.Count,
            incompat.Count == 1 ? string.Empty : "s",
            codecList,
            ext);
        return [new IssueWarning { Code = "bitmap-subs-dropped", Blocking = true, Message = message }];
    }

    private static long EstimateTrackBytes(FfprobeData probe, IReadOnlyList<FfprobeStreamInfo> tracks)
    {
        if (!double.TryParse(probe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || duration <= 0)
        {
            return 0;
        }

        long total = 0;
        foreach (var track in tracks)
        {
            // ponytail: assumes 128 kbps when the track doesn't report a bitrate
            var bitrate = long.TryParse(track.BitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b > 0 ? b : 128_000;
            total += (long)(bitrate / 8.0 * duration);
        }

        return total;
    }
}

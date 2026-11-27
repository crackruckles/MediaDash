using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Runs ffprobe against media files, caching results by path, size and modification time
/// so unchanged files are not probed again on re-scan.
/// </summary>
public sealed class FfprobeService
{
    // Seconds decoded at the start, middle and end by the sampled (non-Thorough) playability check.
    // Was 30; 10 cuts the decode work to a third while still crossing several keyframe intervals.
    private const int SampledWindowSeconds = 10;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(20);
    private static readonly JsonSerializerOptions JsonOptions = new();

    // FFmpeg logs these when the GPU cannot decode a codec/profile and it falls back to software.
    // They describe the fallback, not the file, so they never count as decode diagnostics.
    private static readonly Regex HardwareFallbackLine = new(
        @"hwaccel initialisation returned error|\[error\] No support for codec \S+ profile",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ProbeCacheStore _probes;
    private readonly ILogger<FfprobeService> _logger;
    private readonly IConfigurationManager? _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfprobeService"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface, used to locate the server's bundled ffprobe.</param>
    /// <param name="probes">Probe-cache store (05-C.4) — backs probe_cache and decode_cache.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{FfprobeService}"/> interface.</param>
    /// <param name="configuration">Server configuration, used to read Jellyfin's hardware acceleration settings for the sampled playability check. Null disables hardware decoding.</param>
    public FfprobeService(IMediaEncoder mediaEncoder, ProbeCacheStore probes, ILogger<FfprobeService> logger, IConfigurationManager? configuration = null)
    {
        _mediaEncoder = mediaEncoder;
        _probes = probes;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Probes a media file, returning parsed ffprobe output.
    /// A result with <see cref="FfprobeData.Error"/> set (or no streams) means the file itself is unreadable —
    /// that is a playability finding, not an infrastructure failure.
    /// </summary>
    /// <param name="path">Full path of the file to probe.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parsed probe data, or null when the file is missing or ffprobe could not be executed.</returns>
    public async Task<FfprobeData?> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            _logger.LogDebug("Skipping probe, file no longer exists: {Path}", path);
            return null;
        }

        var size = fileInfo.Length;
        var mtimeTicks = fileInfo.LastWriteTimeUtc.Ticks;

        var cached = _probes.GetCachedProbe(path, size, mtimeTicks);
        if (cached is not null)
        {
            var cachedData = Deserialize(cached, path);
            if (cachedData is not null && !IsInfrastructureError(cachedData.Error?.Message))
            {
                return cachedData;
            }
        }

        var json = await RunFfprobeAsync(path, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        var data = Deserialize(json, path);
        if (data is not null && IsInfrastructureError(data.Error?.Message))
        {
            Api.Diagnostics.Record("Ffprobe.ReadFailed", "Could not read '" + path + "' for analysis, so it was skipped this scan. This does not mean the file is damaged. Details: " + data.Error!.Message);
            return null;
        }

        if (data is not null)
        {
            _probes.StoreProbe(path, size, mtimeTicks, json);
        }

        return data;
    }

    internal static bool ShouldSampleWholeFile(double durationSeconds) => durationSeconds > 0 && durationSeconds < 60;

    /// <summary>
    /// Test-plays short files end-to-end, or the start, middle and end (30 seconds each), to catch corruption ffprobe misses.
    /// Results are cached by path, size and modification time so unchanged files are not decoded again.
    /// </summary>
    /// <param name="path">Full path of the file to check.</param>
    /// <param name="durationSeconds">The file's duration in seconds, used to locate the middle segment; 0 skips the middle check.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="isAudioOnly">
    /// MEDIA-02 / issue #76: when true, decode selects only audio streams (<c>-vn</c>) so attached
    /// cover-art pictures on music / audiobooks don't get picked by ffmpeg's default stream
    /// selection and reported as "truncated at 0s". Scanner passes true for Audio / AudioBook items.
    /// </param>
    /// <returns>The decode error output, or null when the file decodes cleanly.</returns>
    public async Task<string?> DecodeCheckAsync(string path, double durationSeconds, CancellationToken cancellationToken, bool isAudioOnly = false)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            return null;
        }

        var cached = _probes.GetCachedDecode(path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
        if (cached is not null && !IsInfrastructureError(cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        // AVI's index-at-end layout plus its ancient stream framing make it a notorious source of
        // benign "Truncating packet" warnings and lossy regional seeks — Jellyfin plays these files
        // end-to-end just fine, but the thorough check was flagging them as unplayable (field
        // report A8). For AVI, use a whole-file decode: no seeks, no regional
        // time-shortfall heuristic, no stderr marker check (tolerantContainer=true below).
        var isAvi = string.Equals(Path.GetExtension(path), ".avi", StringComparison.OrdinalIgnoreCase);

        // MEDIA-02: ffmpeg's default stream selection on an audio file with an attached JPEG picture
        // picks the picture (treats the still frame as "best video") and decodes it. The time= stat
        // reflects decoded video time, which stays at 0 for a single still frame — the shortfall
        // check then falsely flags a healthy 40-second FLAC as "truncated". Adding -vn disables
        // video selection so the probe runs against audio, which is where the duration actually
        // lives. For true video files isAudioOnly stays false and behavior is unchanged.
        string[] streamSelect = isAudioOnly ? ["-vn"] : Array.Empty<string>();

        string? error;
        if (isAvi || durationSeconds <= 0 || ShouldSampleWholeFile(durationSeconds))
        {
            // Keep the declared duration when decoding a short file so clean EOF cannot hide truncation.
            error = await RunFfmpegDecodeAsync(ConcatArgs(["-i", path], streamSelect, ["-f", "null", "-"]), expectedSeconds: isAvi ? 0 : durationSeconds, tolerantContainer: isAvi, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            error = await DecodeWindowsAsync(path, durationSeconds, streamSelect, skipTailForShortFile: false, cancellationToken).ConfigureAwait(false);
        }

        var result = string.IsNullOrWhiteSpace(error) ? null : error;
        _probes.StoreDecode(path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks, result ?? string.Empty);
        return result;
    }

    internal async Task<string?> CheckSampledPlaybackAsync(string path, FfprobeData probe, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        probe.TryGetDuration(out var duration);
        var signature = string.Create(CultureInfo.InvariantCulture, $"sampled-v1:{duration:R}");
        var before = ReadAudioDecodeStamp(path, signature);
        if (before is { } cached && _probes.HasCleanAudioDecode(path, cached.Size, cached.Modified, cached.Signature))
        {
            return null;
        }

        string[] streams = ["-map", "0:V:0?", "-map", "0:a?"];
        // Hardware decoding (Jellyfin's VAAPI device) for this sampled check only; Thorough mode
        // stays in software, which reports damaged slices the GPU decoder decodes silently.
        var hardware = ResolveSampledHardwareDecodeArgs();
        var isAvi = string.Equals(Path.GetExtension(path), ".avi", StringComparison.OrdinalIgnoreCase);
        var window = SampledWindowSeconds.ToString(CultureInfo.InvariantCulture);
        var error = isAvi
            ? await RunFfmpegDecodeAsync(ConcatArgs(hardware, ["-i", path, "-t", window], ConcatArgs(streams, ["-f", "null", "-"], [])), expectedSeconds: 0, tolerantContainer: true, cancellationToken).ConfigureAwait(false)
            : await DecodeWindowsAsync(path, duration, streams, skipTailForShortFile: true, cancellationToken, SampledWindowSeconds, hardware, probe.TryGetAudioVideoEnd(out var mediaEnd) ? mediaEnd : null).ConfigureAwait(false);
        if (error is null)
        {
            CacheSuccessfulTrackDecode(path, signature, before);
        }

        return error;
    }

    private async Task<string?> DecodeWindowsAsync(string path, double durationSeconds, string[] streamSelect, bool skipTailForShortFile, CancellationToken cancellationToken, int windowSeconds = 30, string[]? inputArgs = null, double? audioVideoEnd = null)
    {
        // Start: decode the first window (or the whole file if it's shorter). If ffmpeg's decoded time
        // falls significantly short of what we asked for, treat that as truncation regardless of any
        // stderr markers — ffmpeg sometimes hits EOF cleanly without emitting one.
        var input = inputArgs ?? [];
        var window = windowSeconds.ToString(CultureInfo.InvariantCulture);
        var startExpected = Math.Min(windowSeconds, durationSeconds);
        var error = await RunFfmpegDecodeAsync(ConcatArgs(input, ["-i", path, "-t", window], ConcatArgs(streamSelect, ["-f", "null", "-"], [])), startExpected, tolerantContainer: false, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(error) && durationSeconds > windowSeconds * 3)
        {
            var middle = ((long)(durationSeconds / 2)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            // expectedSeconds=0 disables the decoded-time shortfall check for this segment. -ss seeks
            // land on the nearest keyframe, so on sparse-keyframe MKVs / anime rips ffmpeg legitimately
            // decodes <90% of the requested 30s window and the shortfall heuristic false-positives on
            // files that play fine in Jellyfin. Exit code + truncation-marker still catch real breakage.
            // ponytail: drop this heuristic for seeks; the start-segment shortfall check remains the truth.
            error = await RunFfmpegDecodeAsync(ConcatArgs(input, ["-ss", middle, "-i", path, "-t", window], ConcatArgs(streamSelect, ["-f", "null", "-"], [])), expectedSeconds: 0, tolerantContainer: false, cancellationToken, requireEveryStream: false).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(error) && (!skipTailForShortFile || durationSeconds > windowSeconds))
        {
            // Same reasoning as the middle segment — -sseof seeks are keyframe-anchored, so the
            // decoded-time shortfall check false-positives here too.
            // Anchor the tail on where audio/video end when the container claims more (a later-ending
            // subtitle track). A file whose audio/video data stops before their own declared end still
            // decodes nothing here and is reported as truncated.
            string[] tail = audioVideoEnd is double end && end > windowSeconds && end < durationSeconds - 1
                ? ["-ss", (end - windowSeconds).ToString("0.###", CultureInfo.InvariantCulture), "-i", path, "-t", window]
                : ["-sseof", "-" + window, "-i", path];
            error = await RunFfmpegDecodeAsync(ConcatArgs(input, tail, ConcatArgs(streamSelect, ["-f", "null", "-"], [])), expectedSeconds: 0, tolerantContainer: false, cancellationToken, requireEveryStream: false).ConfigureAwait(false);
        }

        return error;
    }

    /// <summary>
    /// Checks whether the file's audio is stored next to the video it belongs to. Samples a few
    /// positions first (cheap); only when a sample holds video without audio is the whole file
    /// walked to measure how far apart the two really are. Returns a tri-state result so callers
    /// can distinguish a verified-clean file from one that could not be checked.
    /// </summary>
    /// <param name="path">Full path of the file to check.</param>
    /// <param name="probe">The file's probe data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="useCache">False to measure the file afresh and leave the cache untouched.</param>
    /// <returns>Clean, Broken(offset) when the offset exceeds <see cref="InterleaveAnalyzer.MaxOffsetSeconds"/>, or Indeterminate when the check could not run.</returns>
    public async Task<InterleaveResult> InterleaveCheckAsync(string path, FfprobeData probe, CancellationToken cancellationToken, bool useCache = true)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || probe.Streams is null)
        {
            return InterleaveResult.Indeterminate;
        }

        // Cover art is reported as a video stream with a single packet; it says nothing about layout.
        var video = probe.Streams.FirstOrDefault(s =>
            string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)
            && !(s.Disposition is not null && s.Disposition.TryGetValue("attached_pic", out var pic) && pic != 0));
        var audioIndexes = probe.Streams
            .Where(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Index)
            .ToHashSet();
        if (video is null || audioIndexes.Count == 0)
        {
            return InterleaveResult.Clean;
        }

        if (!double.TryParse(probe.Format?.Duration, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration)
            || !double.IsFinite(duration) || duration <= 0)
        {
            return InterleaveResult.Indeterminate;
        }

        var sampleStarts = InterleaveAnalyzer.GetSampleStarts(duration);
        if (sampleStarts.Count == 0)
        {
            // File too short to sample — policy says we don't check.
            return InterleaveResult.Clean;
        }

        if (useCache)
        {
            var cached = _probes.GetCachedInterleave(path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
            if (cached is double c)
            {
                return c > InterleaveAnalyzer.MaxOffsetSeconds ? InterleaveResult.Broken(c) : InterleaveResult.Clean;
            }
        }

        var sampled = new List<(int StreamIndex, double PtsSeconds)>();
        void OnSamplePacket(int index, double pts)
        {
            if (sampled.Count >= 100_000)
            {
                throw new InvalidDataException("Interleave sample exceeded its packet limit.");
            }

            sampled.Add((index, pts));
        }

        var sampledOk = await ReadPacketTimesAsync(path, InterleaveAnalyzer.ToReadIntervals(sampleStarts), ProbeTimeout, OnSamplePacket, cancellationToken).ConfigureAwait(false);
        if (!sampledOk || !InterleaveAnalyzer.HasVideoInEverySample(sampled, sampleStarts, video.Index))
        {
            return InterleaveResult.Indeterminate;
        }

        double offset = 0;
        if (InterleaveAnalyzer.HasSampleWithoutAudio(sampled, sampleStarts, video.Index, audioIndexes))
        {
            // A sample without audio is only a hint: an audio track that ends early or has silent
            // gaps looks the same. The full walk is slow on big files but only runs on suspects.
            var walk = new InterleaveAnalyzer.PacketWalk(video.Index, audioIndexes);
            var walkOk = await ReadPacketTimesAsync(path, readIntervals: null, TimeSpan.FromMinutes(10), walk.Add, cancellationToken).ConfigureAwait(false);
            if (!walkOk || !walk.HasAllStreams)
            {
                return InterleaveResult.Indeterminate;
            }

            offset = walk.MaximumOffsetSeconds;
        }

        if (useCache)
        {
            _probes.StoreInterleave(path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks, offset);
        }

        return offset > InterleaveAnalyzer.MaxOffsetSeconds ? InterleaveResult.Broken(offset) : InterleaveResult.Clean;
    }

    private async Task<bool> ReadPacketTimesAsync(string path, string? readIntervals, TimeSpan timeout, Action<int, double> onPacket, CancellationToken cancellationToken)
    {
        var probePath = _mediaEncoder.ProbePath;
        if (string.IsNullOrEmpty(probePath))
        {
            return false;
        }

        var media = await MediaProcess.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var lifetime = media.ConfigureAwait(false);
        var process = media.Process;
        process.StartInfo.FileName = probePath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.ArgumentList.Add("-threads");
        process.StartInfo.ArgumentList.Add(ResolveScanThreads());
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        if (readIntervals is not null)
        {
            process.StartInfo.ArgumentList.Add("-read_intervals");
            process.StartInfo.ArgumentList.Add(readIntervals);
        }

        foreach (var arg in new[] { "-show_entries", "packet=stream_index,pts_time", "-of", "csv=p=0", path })
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            media.Start();
            TryLowerPriority(process);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            while (await process.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false) is { } line)
            {
                if (InterleaveAnalyzer.TryParsePacketLine(line, out var streamIndex, out var pts))
                {
                    onPacket(streamIndex, pts);
                }
            }

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("ffprobe packet walk timed out on {Path}", path);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe packet walk failed on {Path}", path);
            return false;
        }
    }

    private static string[] ConcatArgs(string[] a, string[] b, string[] c)
    {
        var combined = new string[a.Length + b.Length + c.Length];
        a.CopyTo(combined, 0);
        b.CopyTo(combined, a.Length);
        c.CopyTo(combined, a.Length + b.Length);
        return combined;
    }

    /// <summary>Checks every audio track when default stream selection cannot cover them all.</summary>
    /// <param name="path">The file to decode.</param>
    /// <param name="probe">Its stream inventory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The first broken audio stream and its decode error, or null.</returns>
    public Task<(FfprobeStreamInfo Stream, string Error)?> FindBrokenAudioStreamAsync(string path, FfprobeData probe, CancellationToken cancellationToken)
        => FindBrokenAudioStreamAsync(path, probe, useCache: false, cancellationToken);

    internal async Task<(FfprobeStreamInfo Stream, string Error)?> FindBrokenAudioStreamAsync(string path, FfprobeData probe, bool useCache, CancellationToken cancellationToken)
    {
        var audioStreams = probe.Streams?.Where(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)).ToList();
        if (audioStreams is null || audioStreams.Count < 2)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var indexes = audioStreams.Select(stream => stream.Index.ToString(CultureInfo.InvariantCulture)).ToArray();
        var tracks = string.Join(',', indexes);
        var before = useCache ? ReadAudioDecodeStamp(path, tracks) : null;
        if (before is { } cached && _probes.HasCleanAudioDecode(path, cached.Size, cached.Modified, cached.Signature))
        {
            _logger.LogDebug("Reusing successful MediaDash multi-track audio decode");
            return null;
        }

        var args = new List<string> { "-i", path };
        foreach (var index in indexes)
        {
            args.Add("-map");
            args.Add("0:" + index);
        }

        args.AddRange(["-f", "null", "-"]);
        var combinedError = await RunFfmpegDecodeAsync(args.ToArray(), expectedSeconds: 0, tolerantContainer: true, cancellationToken).ConfigureAwait(false);
        if (combinedError is null)
        {
            CacheSuccessfulTrackDecode(path, tracks, before);
            return null;
        }

        foreach (var stream in audioStreams)
        {
            var error = await DecodeStreamAsync(path, stream.Index, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return (stream, error);
            }
        }

        throw new InvalidOperationException("The combined audio decode failed, but the individual tracks decoded successfully.");
    }

    private void CacheSuccessfulTrackDecode(string path, string signature, (long Size, long Modified, string Signature)? before)
    {
        if (before is not { } decoded)
        {
            return;
        }

        if (ReadAudioDecodeStamp(path, signature) != before)
        {
            throw new IOException("The media file changed during the track decode check.");
        }

        _probes.StoreCleanAudioDecode(path, decoded.Size, decoded.Modified, decoded.Signature);
    }

    private static (long Size, long Modified, string Signature)? ReadAudioDecodeStamp(string path, string tracks)
    {
        var file = new FileInfo(path);
        if (!file.Exists || !PhysicalFileIdentity.TryRead(path, out var identity) || identity.IsReparsePoint || identity.FileId == 0)
        {
            return null;
        }

        return (file.Length, file.LastWriteTimeUtc.Ticks,
            string.Create(CultureInfo.InvariantCulture, $"{identity.Volume:X}:{identity.FileId:X}:{file.CreationTimeUtc.Ticks}:{tracks}"));
    }

    /// <summary>
    /// Decodes a single stream index to /dev/null to check whether it errors. Used by
    /// PlayabilityFixer's rung-2 (drop broken streams) to pick which streams to leave out
    /// of the remux. Whole-file exit-code check — no cache, no time-shortfall heuristic:
    /// broken streams are typically all-broken or all-fine, not partially truncated.
    /// </summary>
    /// <param name="path">Full path of the file.</param>
    /// <param name="streamIndex">Zero-based ffprobe stream index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The decode error output, or null when the stream decodes cleanly.</returns>
    public async Task<string?> DecodeStreamAsync(string path, int streamIndex, CancellationToken cancellationToken)
    {
        var idx = streamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return await RunFfmpegDecodeAsync(
            ["-i", path, "-map", "0:" + idx, "-f", "null", "-"],
            expectedSeconds: 0,
            tolerantContainer: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> RunFfmpegDecodeAsync(string[] args, double expectedSeconds, bool tolerantContainer, CancellationToken cancellationToken, bool requireEveryStream = true)
    {
        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(encoderPath))
        {
            throw new InvalidOperationException("The server has no ffmpeg configured; the decode check could not run.");
        }

        var media = await MediaProcess.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var lifetime = media.ConfigureAwait(false);
        var process = media.Process;
        process.StartInfo.FileName = encoderPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardError = true;
        // -threads caps decode parallelism to the configured share of cores (default: half the host).
        // Whole-file thorough decode used to saturate every core; on a 1–2 vCPU VM that starved the
        // guest agent and hung SSH. Must come before -i / -sseof in the arg order (ffmpeg's -threads
        // is a per-decoder input option — placing it here also applies to the null output encoder).
        process.StartInfo.ArgumentList.Add("-threads");
        process.StartInfo.ArgumentList.Add(ResolveScanThreads());
        // Recover past isolated damaged frames; empty output and fatal decode failures still fail.
        // Seek windows only require *some* output: a stream that legitimately ends a few seconds
        // before the container (common at the tail) decodes nothing there without being damaged.
        process.StartInfo.ArgumentList.Add("-abort_on");
        process.StartInfo.ArgumentList.Add(requireEveryStream ? "empty_output+empty_output_stream" : "empty_output");
        process.StartInfo.ArgumentList.Add("-v");
        // FFmpeg 7.1 doesn't enforce max_error_rate for audio; verbose totals let us check each stream.
        process.StartInfo.ArgumentList.Add("level+verbose");
        // -stats enables the periodic "frame= ... time=HH:MM:SS.ms" progress line on stderr;
        // we need it so we can compare what ffmpeg actually decoded against what we asked for.
        process.StartInfo.ArgumentList.Add("-stats");
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DecodeTimeout);

        try
        {
            media.Start();
            TryLowerPriority(process);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var diagnostics = DecodeDiagnostics(stderr);

            if (IsInfrastructureError(diagnostics))
            {
                throw new InvalidOperationException("The decode check could not run safely: " + diagnostics);
            }

            // Tolerant containers (AVI) rely on exit code only — AVI's framing routinely triggers
            // "Truncating packet" warnings on perfectly playable files, so treating that as an
            // error was flagging every non-trivial AVI (field report A8).
            var markerTriggered = !tolerantContainer && HasTruncationMarker(diagnostics);
            var excessiveErrors = HasExcessiveDecodeErrors(stderr);
            if (process.ExitCode != 0 || markerTriggered || excessiveErrors == true)
            {
                return string.IsNullOrWhiteSpace(diagnostics) ? "ffmpeg exited with an error" : diagnostics;
            }

            if (excessiveErrors is null)
            {
                throw new InvalidOperationException("FFmpeg did not report decoded frame totals; the file could not be checked safely.");
            }

            // ffmpeg sometimes hits EOF cleanly (exit 0, no truncation marker) but still stopped short of the
            // requested segment. When the last time= we saw is meaningfully less than we asked for, the file
            // had less playable content than the container advertised — that's a truncation the shallow check
            // would miss. 10% tolerance covers seek imprecision at segment boundaries.
            if (expectedSeconds > 1.0)
            {
                var decoded = ParseLastTimeSeconds(stderr);
                if (decoded is double d && d < expectedSeconds * 0.9)
                {
                    return string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Decoded only {0:F2}s of {1:F0}s requested — the container claims more content than the file actually holds.",
                        d,
                        expectedSeconds);
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A slow or idle-priority read is not evidence of damaged media. Do not cache it.
            throw new TimeoutException("The decode check timed out; the file could not be checked safely.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    internal static string DecodeDiagnostics(string stderr)
        => string.Join('\n', Regex.Matches(
            stderr,
            @"^(?:\[[^\]\r\n]+\][ \t]*)*\[(?:error|fatal)\][^\r\n]*",
            RegexOptions.Multiline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)).Select(match => match.Value).Where(line => !HardwareFallbackLine.IsMatch(line)));

    /// <summary>
    /// FFmpeg input options that decode the sampled playability windows on Jellyfin's configured
    /// VAAPI device, keeping frames on the GPU. Empty (software decoding) unless Jellyfin's hardware
    /// acceleration is VAAPI on Linux and the device exists. FFmpeg falls back to software decoding
    /// by itself for codecs or profiles the GPU cannot handle.
    /// </summary>
    /// <returns>The input options, or an empty array.</returns>
    internal string[] ResolveSampledHardwareDecodeArgs()
    {
        if (!OperatingSystem.IsLinux() || _configuration is null)
        {
            return [];
        }

        try
        {
            var encoding = _configuration.GetConfiguration<EncodingOptions>("encoding");
            if (!string.Equals(encoding.HardwareAccelerationType.ToString(), "vaapi", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            var device = string.IsNullOrWhiteSpace(encoding.VaapiDevice) ? "/dev/dri/renderD128" : encoding.VaapiDevice;
            return File.Exists(device) ? ["-hwaccel", "vaapi", "-hwaccel_device", device, "-hwaccel_output_format", "vaapi"] : [];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "Could not read Jellyfin's hardware acceleration settings; decoding in software");
            return [];
        }
    }

    internal static bool? HasExcessiveDecodeErrors(string stderr)
    {
        const string pattern = @"^\[in#\d+/[^\r\n]*\][ \t]*(?:\[verbose\][ \t]*)?Input stream #\d+:\d+ \([^)]+\): \d+ packets read \(\d+ bytes\); (\d+) frames decoded;(?: (\d+) decode errors)?";
        var found = false;
        foreach (Match match in Regex.Matches(stderr, pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            if (!ulong.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var decoded))
            {
                return null;
            }

            ulong errors = 0;
            if (match.Groups[2].Success && !ulong.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out errors))
            {
                return null;
            }

            found = true;
            if (errors > ((double)decoded + errors) * 0.02)
            {
                return true;
            }
        }

        return found ? false : null;
    }

    // Storage/decoder failures cannot establish corruption and must not become cached findings.
    internal static bool IsInfrastructureError(string? message)
        => message is not null && (message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Input/output error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Resource temporarily unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Cannot allocate memory", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unknown decoder", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Decoding requested, but no decoder found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not found for input stream", StringComparison.OrdinalIgnoreCase));

    // "File ended prematurely" can exit cleanly; benign "Truncating packet" warnings are ignored.
    internal static bool HasTruncationMarker(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return false;
        }

        return stderr.Contains("File ended prematurely", StringComparison.Ordinal);
    }

    // ffmpeg's -stats output prints progress lines like:
    //   frame=  190 fps=0.0 q=-0.0 Lsize=N/A time=00:00:07.84 bitrate=N/A speed=205x elapsed=0:00:00.03
    // The last such line's time= is what actually got decoded. Compare to the requested segment to catch
    // clean-EOF truncation the marker-scan misses (some containers stop early without printing a marker).
    internal static double? ParseLastTimeSeconds(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return null;
        }

        // Rightmost match; -stats emits multiple lines during a decode, only the last one matters.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            stderr,
            @"(?:^|[\r\n])(?:\[info\][ \t]*)?(?:(?:frame|size)=[^\r\n]*?)?\btime=(\d+):(\d{2}):(\d{2}(?:\.\d+)?)");
        if (matches.Count == 0)
        {
            return null;
        }

        var m = matches[matches.Count - 1];
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer, invariant, out var h)
            || !int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Integer, invariant, out var mm)
            || !double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float, invariant, out var ss))
        {
            return null;
        }

        return (h * 3600.0) + (mm * 60.0) + ss;
    }

    /// <summary>
    /// Counts video packets in a file by asking ffprobe to walk the whole container. Slow (seconds
    /// to tens of seconds on a Blu-ray remux) but definitive: for a <c>-c copy</c> operation the video
    /// packet stream is byte-for-byte identical between input and output, so packet counts must match.
    /// The <see cref="Fixers.OutputVerifier"/> uses this to rescue verification on files whose
    /// container-level duration is unreliable (e.g. Blu-ray rips where <c>Format.Duration</c> was
    /// derived from a removed audio track — see issue #39).
    /// Returns null if ffprobe couldn't be executed, timed out, or the file has no video stream.
    /// </summary>
    /// <param name="path">Full path of the file to probe.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The count of video packets, or null on failure.</returns>
    public async Task<long?> CountVideoPacketsAsync(string path, CancellationToken cancellationToken)
    {
        var probePath = _mediaEncoder.ProbePath;
        if (string.IsNullOrEmpty(probePath) || !File.Exists(path))
        {
            return null;
        }

        var media = await MediaProcess.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var lifetime = media.ConfigureAwait(false);
        var process = media.Process;
        process.StartInfo.FileName = probePath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.ArgumentList.Add("-threads");
        process.StartInfo.ArgumentList.Add(ResolveScanThreads());
        // -select_streams v:0 restricts to the first video stream; -count_packets makes ffprobe
        // walk the whole container to enumerate packets (this is the slow bit). -show_entries
        // stream=nb_read_packets narrows output to the value we want, and -of csv=p=0 strips
        // formatting so stdout is just the count as a single integer on one line.
        foreach (var arg in new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-count_packets",
            "-show_entries", "stream=nb_read_packets",
            "-of", "csv=p=0",
            path
        })
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        // Give it 5 minutes — a 40GB Blu-ray remux can take a few minutes to walk on a spinning HDD.
        // Well past normal probe budget but this only fires on the failure-recovery path, not every scan.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            media.Start();
            TryLowerPriority(process);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
            await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrEmpty(stdout))
            {
                return null;
            }

            return long.TryParse(stdout, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count) && count > 0
                ? count
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("ffprobe packet count timed out on {Path}", path);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe packet count failed on {Path}", path);
            return null;
        }
    }

    private FfprobeData? Deserialize(string json, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<FfprobeData>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse ffprobe output for {Path}", path);
            return null;
        }
    }

    private async Task<string?> RunFfprobeAsync(string path, CancellationToken cancellationToken)
    {
        var probePath = _mediaEncoder.ProbePath;
        if (string.IsNullOrEmpty(probePath))
        {
            _logger.LogError("The server has no ffprobe configured; cannot analyze media files");
            return null;
        }

        var media = await MediaProcess.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var lifetime = media.ConfigureAwait(false);
        var process = media.Process;
        process.StartInfo.FileName = probePath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        // ffprobe is cheap next to a decode, but for very complex containers it still forks worker
        // threads (extradata parsers, HEVC/AV1 header decoders). Match the decode-side cap so scan
        // CPU usage stays predictable regardless of which scanner is running.
        process.StartInfo.ArgumentList.Add("-threads");
        process.StartInfo.ArgumentList.Add(ResolveScanThreads());
        foreach (var arg in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_error", path })
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            media.Start();
            TryLowerPriority(process);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            return stdout;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("ffprobe timed out after {Timeout} on {Path}", ProbeTimeout, path);
            Api.Diagnostics.Record("Ffprobe.Timeout", "Analysing '" + path + "' took longer than " + ProbeTimeout.TotalSeconds + " seconds, so it was skipped this scan. Very large files or slow storage can cause this; it will be tried again on the next scan.");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run ffprobe at {ProbePath}", probePath);
            Api.Diagnostics.Record("Ffprobe.RunFailed", "MediaDash could not run ffprobe at '" + probePath + "', so file checks (Playability, Quality, audio and subtitle language) cannot run. Check the FFmpeg path in Jellyfin's transcoding settings and that ffprobe is next to it. Details: " + ex.Message);
            return null;
        }
    }

    // Resolves the -threads value from the configured cap. 0 = auto = half the host's logical
    // processors (min 1); positive values are used verbatim. Called at each process launch so a
    // runtime config change picks up on the next scanner without a restart.
    private static string ResolveScanThreads()
    {
        var configured = Plugin.Instance?.Configuration?.ScanCpuThreads ?? 0;
        var n = configured > 0 ? configured : Math.Max(1, Environment.ProcessorCount / 2);
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Best-effort priority nudge. On Windows this is ProcessPriorityClass.BelowNormal; on Linux
    // the runtime maps it to a nice bump. Swallowed exceptions cover the "process already exited"
    // race (fast probes finish before we get here) and the rare host that refuses the change.
    private static void TryLowerPriority(Process process)
    {
        if (!(Plugin.Instance?.Configuration?.ScanBelowNormalPriority ?? true))
        {
            return;
        }

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception)
        {
            // Process may have exited, or the host may refuse the change (rootless container without
            // CAP_SYS_NICE, some macOS sandboxes). Silent by design — this is a hint, not a contract.
        }
    }
}

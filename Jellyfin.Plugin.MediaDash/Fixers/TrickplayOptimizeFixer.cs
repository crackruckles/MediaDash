using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Re-encodes trickplay JPG sprites in one item's trickplay directory to WebP, keeping the .jpg
/// extension so Jellyfin clients don't need to change (they sniff magic bytes at render). Runs
/// ffmpeg via the server's bundled encoder and atomically replaces each file only after ffmpeg
/// succeeded and the new file is smaller than the source.
/// </summary>
public sealed class TrickplayOptimizeFixer : IFixer
{
    // Marker so FfmpegExecutor.SweepStaleMediaDashFfmpegs can identify orphan encodes from us.
    private const string TmpMarker = ".mediadash.tmp";

    // Longest reasonable single-frame WebP encode; picture preset at q=100 on a 3200x1800 sprite
    // is still under 15s on modest hardware. 60s is generous headroom.
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(60);

    private readonly FfmpegExecutor _ffmpeg;
    private readonly IApplicationPaths _appPaths;
    private readonly LibraryGuard _libraryGuard;
    private readonly ILogger<TrickplayOptimizeFixer> _logger;

    /// <summary>Initializes a new instance of the <see cref="TrickplayOptimizeFixer"/> class.</summary>
    /// <param name="ffmpeg">Executes the bundled jellyfin-ffmpeg.</param>
    /// <param name="appPaths">Jellyfin's application paths (used as a defense-in-depth safety gate).</param>
    /// <param name="libraryGuard">Confirms media-folder trickplay dirs sit inside a real library before touching them.</param>
    /// <param name="logger">The logger.</param>
    public TrickplayOptimizeFixer(FfmpegExecutor ffmpeg, IApplicationPaths appPaths, LibraryGuard libraryGuard, ILogger<TrickplayOptimizeFixer> logger)
    {
        _ffmpeg = ffmpeg;
        _appPaths = appPaths;
        _libraryGuard = libraryGuard;
        _logger = logger;
    }

    private enum ConvertKind
    {
        Converted,
        Skipped,
        Failed
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.LargeTrickplay;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Defence in depth: only touch trickplay folders in one of the two legitimate storage layouts.
        // Anything else is refused with a clear reason so a bug elsewhere (a bad Issue.Path, a stray
        // path from disk enumeration) can't turn into arbitrary file destruction.
        if (!IsSafeTrickplayPath(issue.Path))
        {
            return FixResult.Fail("Refused: this path is not a recognised Jellyfin trickplay folder (must sit under the trickplay data dir, or be a <basename>-trickplay folder inside a library): " + issue.Path);
        }

        if (!Directory.Exists(issue.Path))
        {
            return FixResult.Fail("The trickplay folder no longer exists: " + issue.Path);
        }

        var jpgs = Directory.EnumerateFiles(issue.Path, "*.jpg", SearchOption.AllDirectories)
            .Where(TrickplayOptimizeScanner.LooksLikeJpg)
            .ToList();
        if (jpgs.Count == 0)
        {
            return FixResult.Fail("Nothing to remove any more — this trickplay folder no longer has any raw JPG sprites.");
        }

        var config = Plugin.Instance!.Configuration;
        var quality = Math.Clamp(config.TrickplayWebPQuality, 40, 95);

        if (config.DryRun)
        {
            var estBefore = jpgs.Sum(f => SafeLength(f));
            var estAfter = (long)(estBefore * 0.45);
            return FixResult.DryRun(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "re-encode {0} trickplay image(s) in {1} to WebP at quality {2} (estimated {3} → {4} bytes)",
                    jpgs.Count,
                    issue.Path,
                    quality,
                    estBefore,
                    estAfter),
                bytesFreed: estBefore - estAfter);
        }

        long totalBefore = 0;
        long totalAfter = 0;
        var converted = 0;
        var skipped = 0;
        var failed = 0;

        for (var i = 0; i < jpgs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(i / (double)jpgs.Count);

            var jpg = jpgs[i];
            long before;
            try
            {
                before = new FileInfo(jpg).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                continue;
            }

            var outcome = await ConvertOneAsync(jpg, quality, cancellationToken).ConfigureAwait(false);
            switch (outcome.Kind)
            {
                case ConvertKind.Converted:
                    converted++;
                    totalBefore += before;
                    totalAfter += outcome.After;
                    break;
                case ConvertKind.Skipped:
                    skipped++;
                    break;
                case ConvertKind.Failed:
                    failed++;
                    _logger.LogWarning("Trickplay convert failed for {Path}: {Reason}", jpg, outcome.Reason);
                    break;
            }
        }

        progress?.Report(1);

        if (converted == 0 && failed > 0)
        {
            return FixResult.Fail("All " + failed + " trickplay conversions failed. The originals were left untouched.");
        }

        var freed = totalBefore - totalAfter;
        var msg = string.Format(
            CultureInfo.InvariantCulture,
            "Re-encoded {0} trickplay image(s) to WebP (quality {1}), reclaimed {2} bytes.{3}{4}",
            converted,
            quality,
            freed,
            skipped > 0 ? " Skipped " + skipped + " already-optimised file(s)." : string.Empty,
            failed > 0 ? " " + failed + " file(s) failed and were left untouched." : string.Empty);

        return new FixResult { Success = converted > 0, Message = msg, BytesFreed = freed };
    }

    /// <summary>
    /// Confirms a path is one of the two legitimate trickplay folder layouts:
    /// (a) somewhere under <c>&lt;DataPath&gt;/trickplay/</c> — Jellyfin's own data dir;
    /// (b) a <c>&lt;basename&gt;-trickplay</c> folder that lives inside one of the configured library roots.
    /// Anything else is refused; this is the last line of defence against a bad Issue.Path.
    /// </summary>
    /// <param name="path">The trickplay folder path from the Issue.</param>
    /// <returns>True when the path is safe to touch.</returns>
    private bool IsSafeTrickplayPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var dataRoot = Path.Combine(_appPaths.DataPath, "trickplay");
        if (LibraryGuard.IsUnder(Path.GetFullPath(path), dataRoot))
        {
            return true;
        }

        // Media-folder layout: the leaf directory must end in "-trickplay" AND the whole path must
        // sit inside a library root. Ordering matters — LibraryGuard.IsInsideLibrary is the invariant,
        // the suffix check just narrows scope to trickplay-shaped names inside that library.
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (!leaf.EndsWith(Scanners.TrickplayOptimizeScanner.SiblingSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return _libraryGuard.IsInsideLibrary(path);
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private async Task<ConvertOutcome> ConvertOneAsync(string jpg, int quality, CancellationToken cancellationToken)
    {
        var tmp = jpg + TmpMarker;
        try
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }

            var args = new List<string>
            {
                "-i", jpg,
                "-c:v", "libwebp",
                "-quality", quality.ToString(CultureInfo.InvariantCulture),
                "-preset", "picture",
                "-f", "webp",
                tmp
            };

            var err = await _ffmpeg.RunAsync(args, PerFileTimeout, cancellationToken).ConfigureAwait(false);
            if (err is not null)
            {
                TryDelete(tmp);
                return ConvertOutcome.Fail(err);
            }

            if (!File.Exists(tmp))
            {
                return ConvertOutcome.Fail("ffmpeg reported success but produced no output.");
            }

            var after = new FileInfo(tmp).Length;
            if (after == 0)
            {
                TryDelete(tmp);
                return ConvertOutcome.Fail("ffmpeg produced a zero-byte output.");
            }

            var before = new FileInfo(jpg).Length;
            if (after >= before)
            {
                // No win — some tiny 32x32 preview tiles compress worse as WebP than the source JPG.
                // Leave the original in place rather than trade cycles for zero savings.
                TryDelete(tmp);
                return ConvertOutcome.Skip();
            }

            // Atomic replace: File.Move overwrites on .NET 9. Failure here leaves both files and the
            // caller counts it as a failure; the original .jpg is still intact.
            File.Move(tmp, jpg, overwrite: true);
            return ConvertOutcome.Convert(after);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            return ConvertOutcome.Fail(ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; the orphan sweep in FixTask picks up strays with .mediadash.tmp marker.
        }
    }

    private readonly struct ConvertOutcome
    {
        private ConvertOutcome(ConvertKind kind, long after, string? reason)
        {
            Kind = kind;
            After = after;
            Reason = reason;
        }

        public ConvertKind Kind { get; }

        public long After { get; }

        public string? Reason { get; }

        public static ConvertOutcome Convert(long after) => new(ConvertKind.Converted, after, null);

        public static ConvertOutcome Skip() => new(ConvertKind.Skipped, 0, null);

        public static ConvertOutcome Fail(string reason) => new(ConvertKind.Failed, 0, reason);
    }
}

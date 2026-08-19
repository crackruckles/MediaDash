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
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Walks each video item's directory looking for <c>.ass</c> / <c>.ssa</c> sidecar subtitles carrying
/// embedded fonts that no style or override actually references — a common fansub bloat pattern where
/// a release drops in every weight of every font "just in case" and adds megabytes per file. Also flags
/// every subtitle with embedded fonts when the user has set <c>SubtitleForceFont</c>, because that
/// override is going to strip them all anyway.
/// </summary>
public sealed class SubtitleFontScanner : IScanner
{
    // Don't bother flagging a file for < 50 KB of reclaimable font bytes — the write cost isn't worth
    // it and Issues-tab clutter is worse than the disk-space win. Force-font mode ignores this floor
    // since every embedded font is going to be stripped regardless.
    private const long ReclaimFloorBytes = 50 * 1024;

    // File-globs Jellyfin recognises as ASS-family subtitles.
    private static readonly string[] SidecarExtensions = [".ass", ".ssa"];

    private readonly ILogger<SubtitleFontScanner> _logger;

    /// <summary>Initializes a new instance of the <see cref="SubtitleFontScanner"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public SubtitleFontScanner(ILogger<SubtitleFontScanner> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.SubtitleFonts;

    /// <inheritdoc />
    public bool AlwaysUnscoped => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var issues = new List<Issue>();
        var forceFont = Plugin.Instance?.Configuration.SubtitleForceFont?.Trim() ?? string.Empty;
        var forceFontActive = !string.IsNullOrEmpty(forceFont);

        var videos = items.OfType<Video>()
            .Where(v => !string.IsNullOrEmpty(v.Path))
            .ToList();

        for (var i = 0; i < videos.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var video = videos[i];
            var sidecars = FindSidecars(video.Path);
            foreach (var sidecar in sidecars)
            {
                var result = MeasureReclaim(sidecar, forceFontActive);
                if (result is null)
                {
                    continue;
                }

                if (!forceFontActive && result.ReclaimBytes < ReclaimFloorBytes)
                {
                    continue;
                }

                if (result.ReclaimBytes <= 0)
                {
                    continue;
                }

                issues.Add(new Issue
                {
                    Type = Type,
                    ItemId = video.Id,
                    Path = sidecar,
                    Status = IssueStatus.Detected,
                    DetectedAtUtc = DateTime.UtcNow,
                    SizeSavings = result.ReclaimBytes,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        unusedFontCount = result.UnusedFontCount,
                        totalFontCount = result.TotalFontCount,
                        forceFontActive
                    }),
                    SuggestedFix = string.Format(
                        CultureInfo.InvariantCulture,
                        forceFontActive
                            ? "Force font \"{2}\" and drop all {1} embedded font(s) from {3} (approx. {0} bytes freed)."
                            : "Strip {0} bytes of unused embedded font(s) ({4} of {1}) from {3}.",
                        result.ReclaimBytes,
                        result.TotalFontCount,
                        forceFont,
                        Path.GetFileName(sidecar),
                        result.UnusedFontCount)
                });
            }

            progress.Report((i + 1) * 100.0 / Math.Max(videos.Count, 1));
        }

        progress.Report(100);
        _logger.LogInformation("SubtitleFontScanner: {Count} sidecar(s) have reclaimable embedded fonts.", issues.Count);
        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    /// <summary>
    /// Returns the .ass/.ssa sidecars sitting alongside a video whose filenames share the video's
    /// basename prefix — the naming convention Jellyfin uses for external subtitles. Exposed internal
    /// for direct unit-testing.
    /// </summary>
    /// <param name="videoPath">Full path to the video file.</param>
    /// <returns>Sidecar paths (empty when none / directory missing).</returns>
    internal static IReadOnlyList<string> FindSidecars(string videoPath)
    {
        if (string.IsNullOrEmpty(videoPath))
        {
            return Array.Empty<string>();
        }

        var dir = Path.GetDirectoryName(videoPath);
        var basename = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(basename) || !Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var ext in SidecarExtensions)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, basename + "*" + ext);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            result.AddRange(files);
        }

        return result;
    }

    /// <summary>
    /// Parses one sidecar and returns how many bytes of embedded fonts could be freed. Returns null on
    /// parse failure (e.g., non-UTF-8, malformed file) so the scanner skips it rather than crashing.
    /// Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="path">Full path to the sidecar.</param>
    /// <param name="forceFontActive">When true, every embedded font counts as unused.</param>
    /// <returns>Reclaim measurement, or null when the file can't be analysed.</returns>
    internal static ReclaimMeasurement? MeasureReclaim(string path, bool forceFontActive)
    {
        AssSubtitleFile ass;
        try
        {
            ass = AssSubtitleFile.Parse(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }

        var embedded = ass.EmbeddedFonts();
        if (embedded.Count == 0)
        {
            return null;
        }

        long reclaim = 0;
        var unused = 0;
        if (forceFontActive)
        {
            reclaim = embedded.Sum(e => e.BytesEstimate);
            unused = embedded.Count;
        }
        else
        {
            var refs = ass.ReferencedFontnames();
            foreach (var font in embedded)
            {
                if (!AssSubtitleFile.IsReferenced(font.Filename, refs))
                {
                    reclaim += font.BytesEstimate;
                    unused++;
                }
            }
        }

        return new ReclaimMeasurement(reclaim, unused, embedded.Count);
    }

    /// <summary>Result of a per-sidecar reclaim measurement.</summary>
    /// <param name="ReclaimBytes">Estimated bytes freed by applying the fix.</param>
    /// <param name="UnusedFontCount">Number of embedded fonts that would be dropped.</param>
    /// <param name="TotalFontCount">Total embedded fonts before the fix.</param>
    internal sealed record ReclaimMeasurement(long ReclaimBytes, int UnusedFontCount, int TotalFontCount);
}

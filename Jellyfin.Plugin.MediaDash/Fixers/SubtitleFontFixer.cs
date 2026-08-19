using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Rewrites an .ass sidecar to drop unused embedded fonts. Two modes, driven by config:
/// (a) plain unused-font strip — keeps blocks whose filename maps to any style/override reference;
/// (b) force-font mode — rewrites every Style Fontname and <c>{\fn}</c> override to a single family and
/// removes the entire <c>[Fonts]</c> section.
/// </summary>
public sealed class SubtitleFontFixer : IFixer
{
    // Marker filename suffix matches the executor's orphan-sweep pattern so a crashed rewrite doesn't
    // strand rubbish next to the original subtitle.
    private const string TmpMarker = ".mediadash.tmp";

    private readonly LibraryGuard _libraryGuard;
    private readonly ILogger<SubtitleFontFixer> _logger;

    /// <summary>Initializes a new instance of the <see cref="SubtitleFontFixer"/> class.</summary>
    /// <param name="libraryGuard">Confirms the sidecar path sits inside a real library before touching it.</param>
    /// <param name="logger">The logger.</param>
    public SubtitleFontFixer(LibraryGuard libraryGuard, ILogger<SubtitleFontFixer> logger)
    {
        _libraryGuard = libraryGuard;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.SubtitleFonts;

    /// <inheritdoc />
    public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!issue.Path.EndsWith(".ass", StringComparison.OrdinalIgnoreCase)
            && !issue.Path.EndsWith(".ssa", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(FixResult.Fail("Refused: not an .ass/.ssa file — " + issue.Path));
        }

        if (!_libraryGuard.IsInsideLibrary(issue.Path))
        {
            return Task.FromResult(FixResult.Fail("Refused: subtitle sits outside your library folders — " + issue.Path));
        }

        if (!File.Exists(issue.Path))
        {
            return Task.FromResult(FixResult.Fail("The subtitle file no longer exists: " + issue.Path));
        }

        var config = Plugin.Instance!.Configuration;
        var forceFont = config.SubtitleForceFont?.Trim() ?? string.Empty;

        AssSubtitleFile ass;
        try
        {
            ass = AssSubtitleFile.Parse(issue.Path);
        }
        catch (NotSupportedException ex)
        {
            return Task.FromResult(FixResult.Fail("Can't optimise this subtitle: " + ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(FixResult.Fail("Couldn't read " + Path.GetFileName(issue.Path) + ": " + ex.Message));
        }

        var embedded = ass.EmbeddedFonts();
        if (embedded.Count == 0)
        {
            return Task.FromResult(FixResult.Fail("Nothing to remove any more — this subtitle has no embedded fonts."));
        }

        long before;
        try
        {
            before = new FileInfo(issue.Path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(FixResult.Fail("Couldn't stat " + Path.GetFileName(issue.Path) + ": " + ex.Message));
        }

        // Compute which fonts to keep based on current config, not what the scanner saw — the setting
        // may have changed between scan and fix.
        HashSet<string> keep;
        var keptCount = 0;
        var droppedCount = 0;
        if (!string.IsNullOrEmpty(forceFont))
        {
            keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // strip everything
            droppedCount = embedded.Count;
        }
        else
        {
            var refs = ass.ReferencedFontnames();
            keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in embedded)
            {
                if (AssSubtitleFile.IsReferenced(f.Filename, refs))
                {
                    keep.Add(f.Filename);
                    keptCount++;
                }
                else
                {
                    droppedCount++;
                }
            }
        }

        if (droppedCount == 0)
        {
            return Task.FromResult(FixResult.Fail("Nothing to remove any more — every embedded font is referenced by at least one style."));
        }

        if (config.DryRun)
        {
            return Task.FromResult(FixResult.DryRun(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "rewrite {0} to drop {1} of {2} embedded font(s){3}",
                    Path.GetFileName(issue.Path),
                    droppedCount,
                    embedded.Count,
                    string.IsNullOrEmpty(forceFont) ? string.Empty : " and force font \"" + forceFont + "\""),
                bytesFreed: embedded
                    .Where(e => !keep.Contains(e.Filename))
                    .Sum(e => e.BytesEstimate)));
        }

        if (!string.IsNullOrEmpty(forceFont))
        {
            ass.ForceFontname(forceFont);
            ass.ClearAllFonts();
        }
        else
        {
            ass.StripFontsExcept(keep);
        }

        var tmp = issue.Path + TmpMarker;
        try
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }

            ass.Save(tmp);
            File.Move(tmp, issue.Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            return Task.FromResult(FixResult.Fail("Couldn't write " + Path.GetFileName(issue.Path) + ": " + ex.Message));
        }

        long after;
        try
        {
            after = new FileInfo(issue.Path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            after = before; // best-effort — assume no gain rather than lie about bytes freed
            _logger.LogWarning(ex, "SubtitleFontFixer: couldn't stat {Path} after rewrite", issue.Path);
        }

        var freed = Math.Max(0, before - after);
        var msg = string.Format(
            CultureInfo.InvariantCulture,
            "Rewrote {0}: dropped {1} of {2} embedded font(s){3}, reclaimed {4} bytes.",
            Path.GetFileName(issue.Path),
            droppedCount,
            embedded.Count,
            string.IsNullOrEmpty(forceFont) ? string.Empty : " and forced font \"" + forceFont + "\"",
            freed);

        return Task.FromResult(new FixResult { Success = true, Message = msg, BytesFreed = freed });
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
            // Best-effort cleanup — orphan sweep on the next FixTask cycle picks up strays.
        }
    }
}

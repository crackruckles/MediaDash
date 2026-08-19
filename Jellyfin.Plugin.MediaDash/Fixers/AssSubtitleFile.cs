using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Minimal parser + rewriter for ASS / SSA subtitle files, scoped to what the font optimiser needs:
/// enumerate style-referenced fontnames, enumerate <c>[Fonts]</c> embedded font blocks with their
/// approximate byte cost, strip or keep specific blocks, and force a single fontname across every style
/// and <c>{\fn}</c> override. Preserves original line endings and UTF-8 BOM presence.
/// </summary>
/// <remarks>
/// This isn't a general-purpose ASS library. It doesn't decode the UUEncoded font payload, doesn't
/// validate timing, and only handles UTF-8 (with or without BOM). UTF-16 files are refused rather than
/// silently mangled — those are rare in modern fansubs.
/// </remarks>
public sealed class AssSubtitleFile
{
    private static readonly Regex InlineFnRegex = new(@"\\fn\s*([^\\}]+)", RegexOptions.Compiled);
    private static readonly Regex StyleSuffixRegex = new(@"_[BRIS]?[01](_[01])?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WeightSuffixRegex = new(@"-(regular|bold|italic|bolditalic|light|medium|semibold|thin|heavy|black|extralight|extrabold|book|condensed)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly List<Section> _sections;
    private readonly bool _hadBom;
    private readonly string _newLine;

    private AssSubtitleFile(List<Section> sections, bool hadBom, string newLine)
    {
        _sections = sections;
        _hadBom = hadBom;
        _newLine = newLine;
    }

    /// <summary>
    /// Loads and parses an .ass/.ssa file from disk.
    /// </summary>
    /// <param name="path">Full path to the subtitle file.</param>
    /// <returns>The parsed representation.</returns>
    /// <exception cref="NotSupportedException">Thrown when the file has a UTF-16 BOM.</exception>
    public static AssSubtitleFile Parse(string path)
    {
        // Real .ass files are text — any reasonable one is well under a few MB. Cap at 100 MB so a
        // corrupt or malicious sidecar can't OOM the plugin when the fixer tries to load it.
        const long MaxSize = 100L * 1024 * 1024;
        var length = new FileInfo(path).Length;
        if (length > MaxSize)
        {
            throw new NotSupportedException("Subtitle file is " + length + " bytes; refusing to load (100 MB cap).");
        }

        var bytes = File.ReadAllBytes(path);
        return ParseBytes(bytes);
    }

    /// <summary>
    /// Parses subtitle content from raw bytes. Exposed for direct unit-testing without disk I/O.
    /// </summary>
    /// <param name="bytes">The subtitle file bytes.</param>
    /// <returns>The parsed representation.</returns>
    /// <exception cref="NotSupportedException">Thrown when the file has a UTF-16 BOM.</exception>
    public static AssSubtitleFile ParseBytes(byte[] bytes)
    {
        if (bytes.Length >= 2
            && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            throw new NotSupportedException("UTF-16 encoded .ass files aren't supported by the font optimiser.");
        }

        var hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var start = hadBom ? 3 : 0;
        var text = Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(newLine, StringSplitOptions.None);

        var sections = new List<Section>();
        var current = new Section { Header = string.Empty };
        sections.Add(current);
        foreach (var line in lines)
        {
            if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
            {
                current = new Section { Header = line };
                sections.Add(current);
            }
            else
            {
                current.Lines.Add(line);
            }
        }

        return new AssSubtitleFile(sections, hadBom, newLine);
    }

    /// <summary>
    /// Returns the set of fontnames referenced by <c>Style:</c> rows in the styles section and by any
    /// <c>{\fn Name}</c> override in the events section. Case-insensitive; whitespace-trimmed.
    /// </summary>
    /// <returns>Unique fontnames the file's styles / overrides actually use.</returns>
    public IReadOnlySet<string> ReferencedFontnames()
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var styles = FindStylesSection();
        if (styles is not null)
        {
            var col = FindFontnameColumn(styles);
            foreach (var line in styles.Lines)
            {
                if (!line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fields = line["Style:".Length..].Split(',');
                if (fields.Length > col)
                {
                    var name = fields[col].Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        refs.Add(name);
                    }
                }
            }
        }

        var events = _sections.FirstOrDefault(s => IsEventsHeader(s.Header));
        if (events is not null)
        {
            foreach (var line in events.Lines)
            {
                foreach (Match m in InlineFnRegex.Matches(line))
                {
                    var name = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        refs.Add(name);
                    }
                }
            }
        }

        return refs;
    }

    /// <summary>
    /// Enumerates the embedded font blocks in <c>[Fonts]</c> with each block's filename and approximate
    /// byte cost (character count of the block within the source text — close enough for reclaim math).
    /// </summary>
    /// <returns>One entry per embedded font, or empty when no <c>[Fonts]</c> section exists.</returns>
    public IReadOnlyList<AssFontBlock> EmbeddedFonts()
    {
        var section = _sections.FirstOrDefault(s => s.Header.Equals("[Fonts]", StringComparison.OrdinalIgnoreCase));
        var result = new List<AssFontBlock>();
        if (section is null)
        {
            return result;
        }

        string? currentName = null;
        long currentBytes = 0;
        foreach (var line in section.Lines)
        {
            if (line.StartsWith("fontname:", StringComparison.OrdinalIgnoreCase))
            {
                if (currentName is not null)
                {
                    result.Add(new AssFontBlock(currentName, currentBytes));
                }

                currentName = line["fontname:".Length..].Trim();
                currentBytes = line.Length + _newLine.Length;
            }
            else if (currentName is not null)
            {
                currentBytes += line.Length + _newLine.Length;
            }
        }

        if (currentName is not null)
        {
            result.Add(new AssFontBlock(currentName, currentBytes));
        }

        return result;
    }

    /// <summary>
    /// Drops embedded font blocks whose filename is not in <paramref name="keep"/>. If every block is
    /// removed, the entire <c>[Fonts]</c> section is dropped so the file has no dangling header.
    /// </summary>
    /// <param name="keep">Filenames (as they appear on <c>fontname:</c> lines) to preserve.</param>
    public void StripFontsExcept(ISet<string> keep)
    {
        var sectionIdx = _sections.FindIndex(s => s.Header.Equals("[Fonts]", StringComparison.OrdinalIgnoreCase));
        if (sectionIdx < 0)
        {
            return;
        }

        var section = _sections[sectionIdx];
        var kept = new List<string>();
        var inKeepBlock = false;
        foreach (var line in section.Lines)
        {
            if (line.StartsWith("fontname:", StringComparison.OrdinalIgnoreCase))
            {
                var name = line["fontname:".Length..].Trim();
                inKeepBlock = keep.Contains(name);
            }

            if (inKeepBlock)
            {
                kept.Add(line);
            }
        }

        if (kept.Count == 0)
        {
            _sections.RemoveAt(sectionIdx);
        }
        else
        {
            section.Lines.Clear();
            section.Lines.AddRange(kept);
        }
    }

    /// <summary>
    /// Removes the entire <c>[Fonts]</c> section, if present. Used by the force-font mode where every
    /// embedded font is unreferenced by construction.
    /// </summary>
    public void ClearAllFonts()
    {
        var sectionIdx = _sections.FindIndex(s => s.Header.Equals("[Fonts]", StringComparison.OrdinalIgnoreCase));
        if (sectionIdx >= 0)
        {
            _sections.RemoveAt(sectionIdx);
        }
    }

    /// <summary>
    /// Rewrites every <c>Style:</c> row's Fontname column and every <c>{\fn}</c> override to the given
    /// name. Combine with <see cref="ClearAllFonts"/> to strip embedded fonts too.
    /// </summary>
    /// <param name="newFontname">The family name to force everywhere.</param>
    public void ForceFontname(string newFontname)
    {
        if (string.IsNullOrWhiteSpace(newFontname))
        {
            return;
        }

        var styles = FindStylesSection();
        if (styles is not null)
        {
            var col = FindFontnameColumn(styles);
            for (var i = 0; i < styles.Lines.Count; i++)
            {
                var line = styles.Lines[i];
                if (!line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fields = line["Style:".Length..].Split(',');
                if (fields.Length > col)
                {
                    fields[col] = newFontname;
                    styles.Lines[i] = "Style:" + string.Join(",", fields);
                }
            }
        }

        var events = _sections.FirstOrDefault(s => IsEventsHeader(s.Header));
        if (events is not null)
        {
            for (var i = 0; i < events.Lines.Count; i++)
            {
                events.Lines[i] = InlineFnRegex.Replace(events.Lines[i], @"\fn" + newFontname);
            }
        }
    }

    /// <summary>
    /// Serialises the (possibly modified) content back to disk. Preserves the original line endings and
    /// UTF-8 BOM presence. Callers should write to a temp file and atomic-rename over the original.
    /// </summary>
    /// <param name="path">Destination path.</param>
    public void Save(string path)
    {
        var all = new List<string>();
        foreach (var section in _sections)
        {
            if (!string.IsNullOrEmpty(section.Header))
            {
                all.Add(section.Header);
            }

            all.AddRange(section.Lines);
        }

        var text = string.Join(_newLine, all);
        var enc = new UTF8Encoding(_hadBom);
        File.WriteAllText(path, text, enc);
    }

    /// <summary>
    /// Case-insensitive canonicalisation of a fontname or font-file basename so that
    /// "NotoSans-Bold_B0.ttf" and "Noto Sans" collapse to the same key. Strips common style / weight
    /// suffixes and non-letter separators. Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="raw">The filename or style-referenced font name.</param>
    /// <returns>A comparison key. Empty when the input reduces to nothing meaningful.</returns>
    internal static string CanonicalizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var s = raw.Trim();
        var dot = s.LastIndexOf('.');
        if (dot > 0 && dot > s.Length - 6)
        {
            // strip .ttf, .otf, .ttc extensions but not embedded dots earlier in the name.
            s = s[..dot];
        }

        // Run each suffix regex up to twice — some fansubs stack _B0_1 markers.
        for (var i = 0; i < 2; i++)
        {
            s = StyleSuffixRegex.Replace(s, string.Empty);
        }

        s = WeightSuffixRegex.Replace(s, string.Empty);
        return s.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    /// <summary>
    /// Returns true when an embedded font filename maps to any of the referenced fontnames (each side
    /// canonicalised). Substring match either way to catch weight-variant filenames.
    /// </summary>
    /// <param name="embeddedFilename">The filename from a <c>fontname:</c> line.</param>
    /// <param name="referenced">The set from <see cref="ReferencedFontnames"/>.</param>
    /// <returns>True if the font is (probably) referenced and should be kept.</returns>
    internal static bool IsReferenced(string embeddedFilename, IReadOnlySet<string> referenced)
    {
        var lhs = CanonicalizeName(embeddedFilename);
        if (lhs.Length == 0)
        {
            return true; // fail safe — unknown match, keep the font
        }

        foreach (var r in referenced)
        {
            var rhs = CanonicalizeName(r);
            if (rhs.Length == 0)
            {
                continue;
            }

            if (lhs == rhs)
            {
                return true;
            }

            // Substring matches require at least 3 chars on the *shorter* side so a canonicalized
            // 1-2 char name ("a", "co") doesn't spuriously match every embedded font and force-keep
            // all of them.
            if (lhs.Length >= 3 && rhs.Length >= 3
                && (lhs.Contains(rhs, StringComparison.Ordinal) || rhs.Contains(lhs, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private Section? FindStylesSection()
    {
        return _sections.FirstOrDefault(s =>
            s.Header.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase)
            || s.Header.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEventsHeader(string header)
    {
        return header.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindFontnameColumn(Section styles)
    {
        foreach (var line in styles.Lines)
        {
            if (!line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cols = line["Format:".Length..].Split(',');
            for (var i = 0; i < cols.Length; i++)
            {
                if (cols[i].Trim().Equals("Fontname", StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        // ASS v4+ Format default when unspecified.
        return 1;
    }

    private sealed class Section
    {
        public string Header { get; init; } = string.Empty;

        public List<string> Lines { get; } = new();
    }
}

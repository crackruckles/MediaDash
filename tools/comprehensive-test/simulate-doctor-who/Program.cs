// simulate-doctor-who — standalone reproduction of GitHub #43.
//
// Demonstrates the collapse in MediaGrouperScanner.BuildTvIssue without
// needing a running Jellyfin. Runs the same logic the scanner runs:
//   1. seriesRaw   = episode.SeriesName ?? extract from filename
//   2. folderName  = RenameTemplate.Scrub(seriesRaw)
//   3. expectedRoot = Path.Combine(tvRoot, folderName)
//
// The bug: when Jellyfin's metadata provider collapses three separate reboots
// into one Series entity (default TVDb behavior for "Doctor Who"), the same
// SeriesName is returned for episodes from all three folders — and step 3
// produces the same target for all three, colliding their Season 01 folders.
//
// Run: dotnet run --project tools\comprehensive-test\simulate-doctor-who

using System.Text.RegularExpressions;

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  MediaDash — GitHub #43 reproduction (Doctor Who reboots)");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine();

var tvRoot = @"C:\media\tv";

// ── Scenario A: Jellyfin metadata provider collapses all three reboots ──
// This is the reporter's setup. SeriesName is identical across reboot years.
Console.WriteLine("── Scenario A: metadata provider collapsed all three reboots ──");
Console.WriteLine("  (Jellyfin's default TVDb match returns 'Doctor Who' for all three)");
Console.WriteLine();

var collapsed = new[]
{
    new Episode("Doctor Who", $@"{tvRoot}\Doctor Who (1963)\Season 01\Doctor Who (1963) - S01E01.mkv"),
    new Episode("Doctor Who", $@"{tvRoot}\Doctor Who (2005)\Season 01\Doctor Who (2005) - S01E01.mkv"),
    new Episode("Doctor Who", $@"{tvRoot}\Doctor Who (2024)\Season 01\Doctor Who (2024) - S01E01.mkv"),
};

RunScenario(collapsed, tvRoot);

// ── Scenario B: metadata provider separated reboots by year ──
Console.WriteLine();
Console.WriteLine("── Scenario B: metadata provider separated reboots by year ──");
Console.WriteLine("  (Jellyfin's TVDb 'Separate by year' option gives 3 distinct Series)");
Console.WriteLine();

var separated = new[]
{
    new Episode("Doctor Who (1963)", $@"{tvRoot}\Doctor Who (1963)\Season 01\Doctor Who (1963) - S01E01.mkv"),
    new Episode("Doctor Who (2005)", $@"{tvRoot}\Doctor Who (2005)\Season 01\Doctor Who (2005) - S01E01.mkv"),
    new Episode("Doctor Who (2024)", $@"{tvRoot}\Doctor Who (2024)\Season 01\Doctor Who (2024) - S01E01.mkv"),
};

RunScenario(separated, tvRoot);

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  Conclusion");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine();
Console.WriteLine("  MediaGrouperScanner.BuildTvIssue uses episode.SeriesName as-is");
Console.WriteLine("  for the target folder name (via RenameTemplate.Scrub, which");
Console.WriteLine("  only strips filesystem-illegal chars). If Jellyfin's metadata");
Console.WriteLine("  provider returns identical SeriesName for reboot years, the");
Console.WriteLine("  scanner proposes to merge them.");
Console.WriteLine();
Console.WriteLine("  Fix candidates (per issue #43 comment):");
Console.WriteLine("    1. Use ProductionYear or PremiereDate to detect reboots");
Console.WriteLine("    2. Use the physical parent folder name when it differs");
Console.WriteLine("       from the SeriesName (folder-preserves-year signal)");
Console.WriteLine("    3. Refuse to propose a merge when the target would combine");
Console.WriteLine("       two folders that both already have Season NN subfolders");
Console.WriteLine();
return 0;

static void RunScenario(Episode[] episodes, string tvRoot)
{
    var proposals = episodes
        .Select(ep => (Episode: ep, Target: BuildTargetFolder(ep, tvRoot)))
        .ToList();

    Console.WriteLine($"  {"Source",-64}  {"Proposed target"}");
    Console.WriteLine($"  {new string('-', 64)}  {new string('-', 40)}");
    foreach (var (ep, target) in proposals)
    {
        var src = ep.Path.Length > 62 ? "..." + ep.Path[^59..] : ep.Path;
        Console.WriteLine($"  {src,-64}  {target}");
    }
    Console.WriteLine();

    // Detect the collapse: multiple proposals targeting the same folder
    var groups = proposals
        .GroupBy(p => p.Target)
        .Where(g => g.Count() > 1)
        .ToList();

    if (groups.Count > 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  ✗ COLLAPSE DETECTED:");
        foreach (var g in groups)
        {
            Console.WriteLine($"    {g.Count()} episodes propose to merge into: {g.Key}");
            foreach (var (ep, _) in g) Console.WriteLine($"      ← {ep.Path}");
        }
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ✓ NO COLLAPSE — each reboot proposes a distinct target folder.");
        Console.ResetColor();
    }
}

// Mirrors MediaGrouperScanner.BuildTvIssue lines 211-226. Returns just the
// target folder (the "expectedRoot" the scanner computes).
static string BuildTargetFolder(Episode episode, string tvRoot)
{
    var seriesRaw = string.IsNullOrWhiteSpace(episode.SeriesName)
        ? ExtractShowNameFromFilename(Path.GetFileNameWithoutExtension(episode.Path))
        : episode.SeriesName;

    var folderName = Scrub(seriesRaw);
    return Path.Combine(tvRoot, folderName);
}

// Mirrors Jellyfin.Plugin.MediaDash.Fixers.RenameTemplate.Scrub.
static string Scrub(string raw)
{
    var invalid = new Regex(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.CultureInvariant);
    var whitespace = new Regex(@"\s+", RegexOptions.CultureInvariant);
    var cleaned = invalid.Replace(raw, string.Empty);
    cleaned = whitespace.Replace(cleaned, " ").Trim().TrimEnd('.');
    return cleaned.Length == 0 ? "Untitled" : cleaned;
}

// Approximation of MediaGrouperScanner.ExtractShowNameFromFilename fallback.
// We take the stem before " - SxxExx" if present, else the whole stem.
static string ExtractShowNameFromFilename(string stem)
{
    var m = Regex.Match(stem, @"^(?<show>.+?)\s*-\s*[sS]\d{1,2}[eE]\d{1,3}");
    return m.Success ? m.Groups["show"].Value : stem;
}

// A minimal Episode surrogate that mimics the two Jellyfin properties the
// scanner reads: SeriesName and Path. Must live below top-level statements.
record Episode(string SeriesName, string Path);

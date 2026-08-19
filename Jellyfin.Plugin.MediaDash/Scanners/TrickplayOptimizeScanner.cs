using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Flags trickplay directories that still contain raw JPG sprites. Re-encoding to WebP with
/// the extension preserved (client sniffs magic bytes, never sees the change) typically halves the
/// on-disk footprint. Covers both storage layouts Jellyfin supports:
/// (a) data-folder — <c>&lt;DataPath&gt;/trickplay/&lt;itemId&gt;/</c> (default);
/// (b) media-folder — <c>&lt;video-basename&gt;-trickplay/</c> alongside the video (opt-in server setting).
/// </summary>
public sealed class TrickplayOptimizeScanner : IScanner
{
    /// <summary>Sibling-folder suffix Jellyfin uses when media-folder trickplay storage is on. The
    /// video's extension is replaced with this — so <c>foo.mp4</c> gets <c>foo.trickplay/</c> alongside.</summary>
    internal const string SiblingSuffix = ".trickplay";

    /// <summary>
    /// How many items to sample per library when probing for legacy media-folder trickplay data that was left
    /// behind after the user flipped <c>LibraryOptions.SaveTrickplayWithMedia</c>. Bounded so slow storage
    /// (NAS/SMB) can't turn the probe itself into an O(items) walk we were trying to avoid.
    /// </summary>
    internal const int ProbeSampleSize = 5;

    // WebP files start with "RIFF" (4 bytes) then a 4-byte little-endian size, then "WEBP".
    // JPGs start with FF D8 FF. Reading 12 bytes covers both magic-byte checks in one syscall.
    private const int MagicBytes = 12;

    private readonly IApplicationPaths _appPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TrickplayOptimizeScanner> _logger;

    /// <summary>Initializes a new instance of the <see cref="TrickplayOptimizeScanner"/> class.</summary>
    /// <param name="appPaths">Jellyfin's application paths (used to locate the trickplay data dir).</param>
    /// <param name="libraryManager">Used to resolve trickplay-folder GUIDs back to item names for the Issue label.</param>
    /// <param name="logger">The logger.</param>
    public TrickplayOptimizeScanner(IApplicationPaths appPaths, ILibraryManager libraryManager, ILogger<TrickplayOptimizeScanner> logger)
    {
        _appPaths = appPaths;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.LargeTrickplay;

    /// <inheritdoc />
    public bool AlwaysUnscoped => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var issues = new List<Issue>();

        // The items list is already scoped to enabled libraries by ScanTask. Use it both as a scope
        // filter on the data-folder walk and as the source for the media-folder walk.
        var scoped = items.OfType<Video>()
            .Where(v => !string.IsNullOrEmpty(v.Path))
            .ToList();
        var scopedIds = new HashSet<Guid>(scoped.Select(v => v.Id));

        var total = scoped.Count + 1;   // +1 for the data-folder pass, treated as a single step.

        // (a) Data-folder layout: <DataPath>/trickplay/<itemId>/...
        var trickplayRoot = Path.Combine(_appPaths.DataPath, "trickplay");
        if (Directory.Exists(trickplayRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(trickplayRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                if (!Guid.TryParse(name, out var itemId) || !scopedIds.Contains(itemId))
                {
                    continue;
                }

                TryAddIssue(dir, itemId, _libraryManager.GetItemById(itemId)?.Name ?? name);
            }
        }

        progress.Report(100.0 / total);

        // (b) Media-folder layout: <video-basename>-trickplay/ alongside the video file.
        // Per-library gating: if a library has SaveTrickplayWithMedia=false AND our 5-item sample probe
        // finds no legacy sidecars, skip the O(items) Directory.Exists loop for that library entirely.
        // The probe catches libraries where the user flipped the setting (old data left in the other spot).
        var libs = BuildLibraryIndex(_libraryManager.GetVirtualFolders());
        var toWalk = SelectItemsForMediaFolderWalk(scoped, libs, Directory.Exists, _logger);

        for (var i = 0; i < toWalk.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var video = toWalk[i];
            var siblingDir = SiblingTrickplayDir(video.Path);
            if (siblingDir is not null && Directory.Exists(siblingDir))
            {
                TryAddIssue(siblingDir, video.Id, video.Name);
            }

            progress.Report((i + 2) * 100.0 / (toWalk.Count + 1));
        }

        progress.Report(100);
        _logger.LogInformation("TrickplayOptimizeScanner: {Count} trickplay folder(s) have convertible sprites.", issues.Count);
        return Task.FromResult<IReadOnlyList<Issue>>(issues);

        void TryAddIssue(string dir, Guid itemId, string displayName)
        {
            var (jpgCount, jpgBytes) = MeasureConvertibleJpgs(dir);
            if (jpgCount == 0)
            {
                return;
            }

            issues.Add(new Issue
            {
                Type = Type,
                ItemId = itemId,
                Path = dir,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow,
                // Rough estimate: WebP q=80 lands at ~45% of source JPG on photographic content
                // (see bench-trickplay.ps1). Actual savings replace this after the fixer runs.
                SizeSavings = (long)(jpgBytes * 0.55),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    fileCount = jpgCount,
                    totalBytes = jpgBytes
                }),
                SuggestedFix = string.Format(
                    CultureInfo.InvariantCulture,
                    "Re-encode {0} trickplay image{1} for \"{2}\" as WebP (keeps .jpg extension so clients don't notice).",
                    jpgCount,
                    jpgCount == 1 ? string.Empty : "s",
                    displayName)
            });
        }
    }

    /// <summary>
    /// Turns Jellyfin's <see cref="MediaBrowser.Model.Entities.VirtualFolderInfo"/> list into the
    /// lightweight shape the media-folder gating helper works with.
    /// </summary>
    /// <param name="folders">Virtual folders from <c>ILibraryManager.GetVirtualFolders()</c>.</param>
    /// <returns>One <see cref="LibraryHint"/> per folder.</returns>
    internal static IReadOnlyList<LibraryHint> BuildLibraryIndex(IEnumerable<MediaBrowser.Model.Entities.VirtualFolderInfo> folders)
    {
        return folders.Select(f => new LibraryHint(
            f.Name ?? string.Empty,
            (f.Locations ?? Array.Empty<string>())
                .Select(l => Path.TrimEndingDirectorySeparator(l) + Path.DirectorySeparatorChar)
                .ToList(),
            f.LibraryOptions?.SaveTrickplayWithMedia ?? false)).ToList();
    }

    /// <summary>
    /// Given the full scoped-item list and the per-library trickplay hints, returns just the videos whose
    /// libraries need the media-folder walk. Items in libraries with <c>SaveTrickplayWithMedia=true</c>
    /// are always included; items in the other libraries are included only when a
    /// <see cref="ProbeSampleSize"/>-item sample finds any existing sibling <c>-trickplay</c> folder
    /// (i.e., legacy data left after a setting flip). Items that don't match any known library location
    /// are always included as a safe fallback. Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="scoped">Videos already scoped to enabled libraries.</param>
    /// <param name="libs">Per-library location + setting index from <see cref="BuildLibraryIndex"/>.</param>
    /// <param name="dirExists">Directory-existence probe (injected so tests don't hit disk).</param>
    /// <param name="logger">Optional logger for per-library skip/walk decisions.</param>
    /// <returns>Videos to hand to the media-folder walk loop.</returns>
    internal static IReadOnlyList<Video> SelectItemsForMediaFolderWalk(
        IReadOnlyList<Video> scoped,
        IReadOnlyList<LibraryHint> libs,
        Func<string, bool> dirExists,
        ILogger? logger)
    {
        var buckets = new Dictionary<int, List<Video>>();
        var orphans = new List<Video>();
        foreach (var v in scoped)
        {
            var idx = FindLibraryIndex(libs, v.Path);
            if (idx < 0)
            {
                orphans.Add(v);
                continue;
            }

            if (!buckets.TryGetValue(idx, out var list))
            {
                buckets[idx] = list = new List<Video>();
            }

            list.Add(v);
        }

        var result = new List<Video>();
        foreach (var kv in buckets)
        {
            var lib = libs[kv.Key];
            if (ShouldWalkMediaFolder(lib.SaveTrickplayWithMedia, kv.Value.Select(v => v.Path), dirExists))
            {
                result.AddRange(kv.Value);
                logger?.LogInformation(
                    "TrickplayOptimizeScanner: walking media-folder trickplay in library \"{Library}\" ({Count} items, setting={Setting}).",
                    lib.Name,
                    kv.Value.Count,
                    lib.SaveTrickplayWithMedia);
            }
            else
            {
                logger?.LogInformation(
                    "TrickplayOptimizeScanner: skipping media-folder walk in library \"{Library}\" — SaveTrickplayWithMedia=false and no legacy sidecars in a {Sample}-item sample.",
                    lib.Name,
                    ProbeSampleSize);
            }
        }

        result.AddRange(orphans);
        return result;
    }

    /// <summary>
    /// Pure gate for one library: returns true when the media-folder walk should happen for it.
    /// True when the library is configured for media-folder storage, OR when a bounded probe finds any
    /// existing sibling <c>-trickplay</c> directory (legacy data). Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="saveTrickplayWithMedia">The library's current setting.</param>
    /// <param name="sampleVideoPaths">Videos in the library, used only up to <see cref="ProbeSampleSize"/>.</param>
    /// <param name="dirExists">Directory-existence probe (injected so tests don't hit disk).</param>
    /// <returns>True to walk the library's items; false to skip.</returns>
    internal static bool ShouldWalkMediaFolder(bool saveTrickplayWithMedia, IEnumerable<string> sampleVideoPaths, Func<string, bool> dirExists)
    {
        if (saveTrickplayWithMedia)
        {
            return true;
        }

        return sampleVideoPaths
            .Take(ProbeSampleSize)
            .Select(SiblingTrickplayDir)
            .Where(d => !string.IsNullOrEmpty(d))
            .Any(d => dirExists(d!));
    }

    private static int FindLibraryIndex(IReadOnlyList<LibraryHint> libs, string videoPath)
    {
        for (var i = 0; i < libs.Count; i++)
        {
            foreach (var loc in libs[i].Locations)
            {
                if (videoPath.StartsWith(loc, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Derives the sibling <c>&lt;basename&gt;-trickplay</c> folder path for a video file, or null when
    /// the path can't be interpreted. Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="videoPath">Full path to the video file.</param>
    /// <returns>The sibling directory path, or null.</returns>
    internal static string? SiblingTrickplayDir(string videoPath)
    {
        if (string.IsNullOrEmpty(videoPath))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(videoPath);
        var basename = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(basename))
        {
            return null;
        }

        return Path.Combine(dir, basename + SiblingSuffix);
    }

    /// <summary>
    /// Counts .jpg files inside a trickplay folder that are real JPGs (magic-byte check) and totals
    /// their bytes. Files whose bytes are already WebP but named .jpg (our own converted output) are
    /// skipped so a re-scan doesn't re-flag them. Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="trickplayDir">Full path to the item's trickplay directory.</param>
    /// <returns>Convertible file count and total bytes.</returns>
    internal static (int Count, long Bytes) MeasureConvertibleJpgs(string trickplayDir)
    {
        var count = 0;
        long bytes = 0;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(trickplayDir, "*.jpg", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }

        foreach (var file in files)
        {
            if (!LooksLikeJpg(file))
            {
                continue;
            }

            try
            {
                bytes += new FileInfo(file).Length;
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip individual unreadable files — they're not blockers for the group.
            }
        }

        return (count, bytes);
    }

    /// <summary>
    /// Returns true when the file's magic bytes are JPG (FF D8 FF). Returns false for WebP (RIFF..WEBP),
    /// zero-byte / unreadable files, and unknown formats. Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="path">Full path to the file.</param>
    /// <returns>True if the file is a real JPG that can be converted.</returns>
    internal static bool LooksLikeJpg(string path)
    {
        Span<byte> buf = stackalloc byte[MagicBytes];
        int read;
        try
        {
            using var s = File.OpenRead(path);
            read = s.Read(buf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return read >= 3 && buf[0] == 0xFF && buf[1] == 0xD8 && buf[2] == 0xFF;
    }

    /// <summary>
    /// Represents one library's location prefixes and its <c>SaveTrickplayWithMedia</c> setting, in the
    /// shape <see cref="SelectItemsForMediaFolderWalk"/> consumes. Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="Name">Human-readable library name for diagnostic logs.</param>
    /// <param name="Locations">The library's root folders, each terminated with a directory separator so path prefix-match is unambiguous.</param>
    /// <param name="SaveTrickplayWithMedia">The current per-library trickplay storage-mode setting.</param>
    internal sealed record LibraryHint(string Name, IReadOnlyList<string> Locations, bool SaveTrickplayWithMedia);
}

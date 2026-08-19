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
/// One scanner, four detection passes gated by per-kind config toggles: video-free folders, orphaned
/// subtitle sidecars, orphaned media-folder <c>.trickplay</c> folders, and orphaned Jellyfin metadata
/// folders whose item GUID no longer resolves. Each finding emits a single
/// <see cref="IssueType.OrphanedDebris"/> issue tagged in <c>DetailsJson.kind</c> so the fixer knows
/// which delete strategy to apply.
/// </summary>
public sealed class OrphanCleanupScanner : IScanner
{
    /// <summary>Sentinel written to <c>DetailsJson.kind</c> for empty-folder findings.</summary>
    internal const string KindEmptyFolder = "EmptyFolder";

    /// <summary>Sentinel for orphan subtitle sidecar findings.</summary>
    internal const string KindOrphanSubtitle = "OrphanSubtitle";

    /// <summary>Sentinel for orphan media-folder trickplay findings.</summary>
    internal const string KindOrphanTrickplay = "OrphanTrickplay";

    /// <summary>Sentinel for orphan Jellyfin metadata folder findings.</summary>
    internal const string KindOrphanMetadata = "OrphanMetadata";

    internal static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
        ".mpg", ".mpeg", ".ts", ".m2ts", ".vob", ".3gp", ".ogv", ".mts", ".divx", ".rmvb"
    };

    internal static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup"
    };

    private const string TrickplaySuffix = ".trickplay";

    private readonly IApplicationPaths _appPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<OrphanCleanupScanner> _logger;

    /// <summary>Initializes a new instance of the <see cref="OrphanCleanupScanner"/> class.</summary>
    /// <param name="appPaths">Jellyfin's application paths — used to reach the metadata folder root.</param>
    /// <param name="libraryManager">Used to enumerate library locations and resolve item GUIDs.</param>
    /// <param name="logger">The logger.</param>
    public OrphanCleanupScanner(IApplicationPaths appPaths, ILibraryManager libraryManager, ILogger<OrphanCleanupScanner> logger)
    {
        _appPaths = appPaths;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.OrphanedDebris;

    /// <inheritdoc />
    public bool AlwaysUnscoped => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var issues = new List<Issue>();

        var libraryLocations = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var passes = new List<(string Name, Action<List<Issue>> Run)>();
        if (config.OrphanScanEmptyFolders)
        {
            passes.Add(("empty folders", i => DetectEmptyFolders(libraryLocations, i, cancellationToken)));
        }

        if (config.OrphanScanSubtitles)
        {
            passes.Add(("subtitles", i => DetectOrphanSubtitles(libraryLocations, i, cancellationToken)));
        }

        if (config.OrphanScanTrickplay)
        {
            passes.Add(("trickplay", i => DetectOrphanTrickplay(libraryLocations, i, cancellationToken)));
        }

        if (config.OrphanScanMetadata)
        {
            passes.Add(("metadata", i => DetectOrphanMetadata(i, cancellationToken)));
        }

        for (var i = 0; i < passes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (name, run) = passes[i];
            try
            {
                run(issues);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "OrphanCleanupScanner: {Pass} pass failed", name);
                Api.Diagnostics.Record("OrphanCleanupScanner." + name, "The " + name + " orphan-detection pass failed: " + ex.Message + ". Any orphans in that category won't be flagged until the next scan succeeds.");
            }

            progress.Report((i + 1) * 100.0 / Math.Max(passes.Count, 1));
        }

        progress.Report(100);
        _logger.LogInformation("OrphanCleanupScanner: {Count} orphan(s) across {Passes} pass(es).", issues.Count, passes.Count);
        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    /// <summary>
    /// Walks each library root and flags subtrees that contain no video files anywhere. The topmost
    /// video-free directory is flagged; subdirs beneath it don't get their own issues because deleting
    /// the parent handles them. Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="libraryLocations">Library root folders.</param>
    /// <param name="issues">Accumulator.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static void DetectEmptyFolders(IReadOnlyList<string> libraryLocations, List<Issue> issues, CancellationToken cancellationToken)
    {
        foreach (var root in libraryLocations)
        {
            WalkForEmpty(root, issues, isRoot: true, cancellationToken);
        }
    }

    private static void WalkForEmpty(string dir, List<Issue> issues, bool isRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Never treat a Jellyfin companion folder as "empty" just because it has no videos.
        // `.trickplay/` holds sprite JPGs; whether it's orphaned or paired is the orphan-trickplay
        // pass's problem, not this one's.
        if (!isRoot && IsCompanionFolder(dir))
        {
            return;
        }

        List<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var hasVideos = SubtreeHasVideos(dir);

        // Never flag the library root itself, even when it's video-free — that's a config artefact,
        // not user debris. But do recurse so a Junk/ subdirectory under an empty root still gets found.
        if (!hasVideos && !isRoot)
        {
            var size = TrySubtreeSize(dir);
            issues.Add(new Issue
            {
                Type = IssueType.OrphanedDebris,
                Path = dir,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow,
                SizeSavings = size,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    kind = KindEmptyFolder,
                    bytesEstimate = size
                }),
                SuggestedFix = string.Format(
                    CultureInfo.InvariantCulture,
                    "Delete video-free folder \"{0}\" (approx. {1} bytes of leftover metadata / sidecars).",
                    Path.GetFileName(dir),
                    size)
            });
            return; // don't recurse — deleting the parent handles all children.
        }

        foreach (var sub in subdirs)
        {
            WalkForEmpty(sub, issues, isRoot: false, cancellationToken);
        }
    }

    /// <summary>
    /// Returns true for folders the empty-folder pass must NOT walk into or flag: Jellyfin trickplay
    /// data companions (<c>*.trickplay/</c>). Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="dir">Absolute path.</param>
    /// <returns>True when the folder is a known companion / should be left to a more specialised pass.</returns>
    internal static bool IsCompanionFolder(string dir)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        return name.EndsWith(TrickplaySuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SubtreeHasVideos(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (VideoExtensions.Contains(Path.GetExtension(f)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Treat unreadable subtree as "has videos" — conservative: don't flag for deletion.
            return true;
        }

        return false;
    }

    /// <summary>
    /// Walks each library root and flags subtitle sidecars whose companion video is missing. The
    /// companion is any file in the same directory whose basename equals or starts with the subtitle
    /// basename before its language / stream suffix (e.g. <c>Foo.en.srt</c> pairs with <c>Foo.mkv</c>).
    /// Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="libraryLocations">Library root folders.</param>
    /// <param name="issues">Accumulator.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static void DetectOrphanSubtitles(IReadOnlyList<string> libraryLocations, List<Issue> issues, CancellationToken cancellationToken)
    {
        foreach (var root in libraryLocations)
        {
            IEnumerable<string> files;
            try
            {
                // IgnoreInaccessible so an unreadable subfolder mid-walk doesn't abort the whole
                // orphan-subtitle pass for this library root.
                var enumOpts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
                files = Directory.EnumerateFiles(root, "*", enumOpts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file);
                if (!SubtitleExtensions.Contains(ext))
                {
                    continue;
                }

                if (HasCompanionVideo(file))
                {
                    continue;
                }

                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    size = 0;
                }

                issues.Add(new Issue
                {
                    Type = IssueType.OrphanedDebris,
                    Path = file,
                    Status = IssueStatus.Detected,
                    DetectedAtUtc = DateTime.UtcNow,
                    SizeSavings = size,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        kind = KindOrphanSubtitle,
                        bytesEstimate = size
                    }),
                    SuggestedFix = string.Format(
                        CultureInfo.InvariantCulture,
                        "Delete orphan subtitle \"{0}\" — no companion video in its folder.",
                        Path.GetFileName(file))
                });
            }
        }
    }

    /// <summary>
    /// Returns true when a video file exists in the same directory as <paramref name="subtitlePath"/>
    /// whose basename is a prefix of the subtitle's basename. Handles multi-dot naming like
    /// <c>Foo.en.srt</c> pairing with <c>Foo.mkv</c>. Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="subtitlePath">Full path to a subtitle file.</param>
    /// <returns>True when a paired video exists.</returns>
    internal static bool HasCompanionVideo(string subtitlePath)
    {
        var dir = Path.GetDirectoryName(subtitlePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return false;
        }

        var subBase = Path.GetFileNameWithoutExtension(subtitlePath) ?? string.Empty;
        if (string.IsNullOrEmpty(subBase))
        {
            return false;
        }

        // Reduce "Foo.en" → "Foo" by stripping any trailing .xx / .xxx language token so a video named
        // Foo.mkv still counts as the companion for Foo.en.srt.
        var candidates = new List<string> { subBase };
        var lastDot = subBase.LastIndexOf('.');
        if (lastDot > 0 && subBase.Length - lastDot - 1 <= 5)
        {
            candidates.Add(subBase[..lastDot]);
        }

        IEnumerable<string> siblings;
        try
        {
            siblings = Directory.EnumerateFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // conservative: don't flag as orphan when we can't verify
        }

        foreach (var s in siblings)
        {
            if (!VideoExtensions.Contains(Path.GetExtension(s)))
            {
                continue;
            }

            var vidBase = Path.GetFileNameWithoutExtension(s);
            foreach (var c in candidates)
            {
                if (string.Equals(vidBase, c, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Walks each library root and flags <c>*.trickplay</c> folders whose companion video is gone.
    /// Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="libraryLocations">Library root folders.</param>
    /// <param name="issues">Accumulator.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static void DetectOrphanTrickplay(IReadOnlyList<string> libraryLocations, List<Issue> issues, CancellationToken cancellationToken)
    {
        foreach (var root in libraryLocations)
        {
            IEnumerable<string> tps;
            try
            {
                tps = Directory.EnumerateDirectories(root, "*" + TrickplaySuffix, SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var tp in tps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasCompanionVideoForTrickplay(tp))
                {
                    continue;
                }

                var size = TrySubtreeSize(tp);
                issues.Add(new Issue
                {
                    Type = IssueType.OrphanedDebris,
                    Path = tp,
                    Status = IssueStatus.Detected,
                    DetectedAtUtc = DateTime.UtcNow,
                    SizeSavings = size,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        kind = KindOrphanTrickplay,
                        bytesEstimate = size
                    }),
                    SuggestedFix = string.Format(
                        CultureInfo.InvariantCulture,
                        "Delete orphan trickplay folder \"{0}\" — no companion video (approx. {1} bytes).",
                        Path.GetFileName(tp),
                        size)
                });
            }
        }
    }

    /// <summary>
    /// Returns true when a video file exists in the same directory as <paramref name="trickplayDir"/>
    /// whose basename matches the trickplay folder's stem (folder name minus the <c>.trickplay</c>
    /// suffix). Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="trickplayDir">Full path to a <c>*.trickplay</c> folder.</param>
    /// <returns>True when the companion video is present.</returns>
    internal static bool HasCompanionVideoForTrickplay(string trickplayDir)
    {
        var folderName = Path.GetFileName(trickplayDir);
        if (!folderName.EndsWith(TrickplaySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true; // odd match — bail rather than delete
        }

        var stem = folderName[..^TrickplaySuffix.Length];
        var parent = Path.GetDirectoryName(trickplayDir);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            return false;
        }

        IEnumerable<string> siblings;
        try
        {
            siblings = Directory.EnumerateFiles(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // conservative
        }

        foreach (var s in siblings)
        {
            if (!VideoExtensions.Contains(Path.GetExtension(s)))
            {
                continue;
            }

            if (string.Equals(Path.GetFileNameWithoutExtension(s), stem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks Jellyfin's internal metadata store looking for <c>library/&lt;hh&gt;/&lt;itemGuid&gt;/</c>
    /// folders whose GUID no longer resolves via <see cref="ILibraryManager.GetItemById(Guid)"/>.
    /// </summary>
    /// <param name="issues">Accumulator.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private void DetectOrphanMetadata(List<Issue> issues, CancellationToken cancellationToken)
    {
        var libraryRoot = Path.Combine(_appPaths.DataPath, "..", "metadata", "library");
        libraryRoot = Path.GetFullPath(libraryRoot);
        if (!Directory.Exists(libraryRoot))
        {
            // Fallback for installs where InternalMetadataPath lives elsewhere; try DataPath-relative
            // "metadata/library" too so unusual layouts still get swept.
            libraryRoot = Path.Combine(_appPaths.DataPath, "metadata", "library");
            if (!Directory.Exists(libraryRoot))
            {
                return;
            }
        }

        foreach (var shard in Directory.EnumerateDirectories(libraryRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> itemDirs;
            try
            {
                itemDirs = Directory.EnumerateDirectories(shard);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var itemDir in itemDirs)
            {
                var name = Path.GetFileName(itemDir);
                if (!Guid.TryParse(name, out var itemId))
                {
                    continue;
                }

                if (_libraryManager.GetItemById(itemId) is not null)
                {
                    continue;
                }

                var size = TrySubtreeSize(itemDir);
                issues.Add(new Issue
                {
                    Type = IssueType.OrphanedDebris,
                    ItemId = itemId,
                    Path = itemDir,
                    Status = IssueStatus.Detected,
                    DetectedAtUtc = DateTime.UtcNow,
                    SizeSavings = size,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        kind = KindOrphanMetadata,
                        bytesEstimate = size
                    }),
                    SuggestedFix = string.Format(
                        CultureInfo.InvariantCulture,
                        "Delete orphan Jellyfin metadata folder for missing item {0} (approx. {1} bytes).",
                        itemId,
                        size)
                });
            }
        }
    }

    private static long TrySubtreeSize(string dir)
    {
        try
        {
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(f).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip individual unreadable files.
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

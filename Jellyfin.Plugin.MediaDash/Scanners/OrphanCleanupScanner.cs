using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
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

    /// <summary>Sentinel for a series folder whose episodes were grouped into its TVDb master folder.</summary>
    internal const string KindGroupedLeftover = "GroupedLeftover";

    /// <summary>
    /// Video extensions the orphan pass considers when pairing subtitles / trickplay folders to a
    /// companion video. Aliased to <see cref="MediaFormats.Video"/> — mirrors Jellyfin's own
    /// <c>NamingOptions.VideoFileExtensions</c> so any format Jellyfin indexes is recognised here.
    /// </summary>
    internal static readonly HashSet<string> VideoExtensions = MediaFormats.Video;

    /// <summary>
    /// Every extension the empty-folder pass counts as "user media". Union of Video + Audio + Books
    /// + Comics + Pictures. Field report: a music-only library (or books-only, audiobooks-only,
    /// comics-only, pictures-only) has zero files matching VideoExtensions in any sub-directory, so
    /// every artist/album folder used to be flagged as an "empty folder" and the fixer deleted the
    /// entire library. Adding non-video kinds keeps a folder from being classed as "empty" when it
    /// still holds Jellyfin-recognised content.
    /// </summary>
    internal static readonly HashSet<string> MediaExtensions = MediaFormats.All;

    /// <summary>
    /// Extensions the orphan-cleanup pass recognises as subtitle sidecars. Aliased to the canonical
    /// <see cref="SubtitleFormats.Extensions"/> so a format added there is automatically visible here.
    /// </summary>
    internal static readonly HashSet<string> SubtitleExtensions = SubtitleFormats.Extensions;

    // Folder names Jellyfin (and users) commonly use to keep subtitle sidecars beside — but not
    // inside — a video's folder. Only these names trigger the parent-lookup in HasCompanionVideo,
    // so a completely-unrelated adjacent folder still gets treated as an orphan.
    // Explicit whitelist of subtitle-container folder names (case-insensitive), incl. common
    // non-English variants. Used together with IsSubtitleContainerFolder's prefix rule so a
    // folder like "Subs-EN" or "Subtitles (SDH)" also qualifies without needing every variant
    // enumerated. Kept intentionally trim — walking up from an arbitrary adjacent folder would
    // false-pair a subtitle with an unrelated sibling video.
    internal static readonly HashSet<string> SubtitleContainerFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "subtitles", "subtitle", "subs", "sub", "captions", "caption", "cc", "srt",
        // Spanish / Portuguese
        "subtitulos", "legendas",
        // French
        "sous-titres", "sous titres",
        // German
        "untertitel",
        // Italian
        "sottotitoli",
        // Russian / Ukrainian
        "субтитры", "субтитри",
        // Chinese / Japanese / Korean
        "字幕", "자막"
    };

    private const string TrickplaySuffix = ".trickplay";

    // Jellyfin's local artwork names (https://jellyfin.org/docs/general/server/media/movies#images)
    // plus "<video name>-thumb" style per-item art and numbered backdrops/fanart.
    private static readonly Regex JellyfinArtworkName = new(
        @"^(?:(?:poster|folder|cover|default|movie|show|jacket|backdrop|fanart|background|art|banner|logo|clearlogo|clearart|landscape|thumb|disc|discart|cdart)\d*"
        + @"|season(?:\d+|-all|-specials)-(?:poster|banner|fanart|landscape|thumb)"
        + @"|.+-(?:poster|fanart|thumb|landscape|banner|logo|clearart|backdrop|disc|discart))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IServerApplicationPaths _appPaths;
    private readonly LibraryGuard _libraryGuard;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<OrphanCleanupScanner> _logger;
    private readonly ProtectionService? _protection;
    private readonly MediaMoveStore? _moves;
    private readonly MediaIdentityStore? _identities;

    /// <summary>Initializes a new instance of the <see cref="OrphanCleanupScanner"/> class.</summary>
    /// <param name="appPaths">Jellyfin's application paths — used to reach the metadata folder root.</param>
    /// <param name="libraryManager">Used to enumerate library locations and resolve item GUIDs.</param>
    /// <param name="protection">Rules excluding protected files from analysis.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="taskManager">Host library-refresh state, when available.</param>
    /// <param name="moves">Durable move paths excluded from orphan detection.</param>
    /// <param name="identities">TVDb master folders, used to find folders left behind by grouping.</param>
    public OrphanCleanupScanner(IServerApplicationPaths appPaths, ILibraryManager libraryManager, ILogger<OrphanCleanupScanner> logger, ProtectionService? protection = null, ITaskManager? taskManager = null, MediaMoveStore? moves = null, MediaIdentityStore? identities = null)
    {
        _identities = identities;
        _appPaths = appPaths;
        _libraryGuard = new LibraryGuard(libraryManager, appPaths, taskManager, moves: moves);
        _libraryManager = libraryManager;
        _logger = logger;
        _protection = protection;
        _moves = moves;
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
        var reservations = _moves?.CaptureReservations();
        var reserved = reservations?.Path;

        // Respect EnabledLibraries via the shared helper. AlwaysUnscoped only means "skip the
        // DB scoped-delete branch" — never "walk every folder on disk regardless of the user's
        // Settings → Libraries opt-in list". See VirtualFolderIdentity.GetEnabledFolders.
        var libraryLocations = VirtualFolderIdentity.GetEnabledFolders(_libraryManager, config.EnabledLibraries)
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Movies and Shows libraries: Jellyfin artwork and release notes there are metadata, not media.
        var videoLibraryRoots = VirtualFolderIdentity.GetEnabledFolders(_libraryManager, config.EnabledLibraries)
            .Where(folder => folder.CollectionType is CollectionTypeOptions.movies or CollectionTypeOptions.tvshows)
            .SelectMany(folder => folder.Locations ?? Array.Empty<string>())
            .Select(location => Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var passes = new List<(string Name, Action<List<Issue>> Run)>();
        if (config.OrphanScanEmptyFolders)
        {
            passes.Add(("empty folders", i => DetectEmptyFolders(libraryLocations, i, cancellationToken, _protection, reserved, videoLibraryRoots)));
            if (_identities is not null)
            {
                passes.Add(("grouped leftovers", i => DetectGroupedLeftovers(_identities.GetGroupedLeftovers(), libraryLocations, i, cancellationToken, _protection, reserved)));
            }

            passes.Add(("untracked grouped leftovers", i => DetectUntrackedGroupedLeftovers(libraryLocations, i, reserved, cancellationToken)));
        }

        if (config.OrphanScanSubtitles)
        {
            passes.Add(("subtitles", i => DetectOrphanSubtitles(libraryLocations, i, cancellationToken, _protection, reserved)));
        }

        if (config.OrphanScanTrickplay)
        {
            passes.Add(("trickplay", i => DetectOrphanTrickplay(libraryLocations, i, cancellationToken, _protection, reserved)));
        }

        if (config.OrphanScanMetadata)
        {
            passes.Add(("metadata", i => DetectOrphanMetadata(i, reserved, reservations?.Item, cancellationToken)));
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
        var currentReservations = _moves?.CaptureReservations();
        return Task.FromResult<IReadOnlyList<Issue>>(currentReservations is null ? issues : issues.Where(issue => !currentReservations.Value.Path(issue.Path) && !currentReservations.Value.Item(issue.ItemId)).ToList());
    }

    /// <summary>
    /// Walks each library root and flags subtrees that contain no video files anywhere. The topmost
    /// video-free directory is flagged; subdirs beneath it don't get their own issues because deleting
    /// the parent handles them. Exposed internal for direct unit-testing.
    /// </summary>
    /// <param name="libraryLocations">Library root folders.</param>
    /// <param name="issues">Accumulator.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="protection">Rules excluding protected paths from analysis.</param>
    /// <param name="reserved">Move paths excluded from detection.</param>
    /// <param name="videoLibraryRoots">Roots of Movies and Shows libraries, where Jellyfin artwork and release notes are metadata rather than media.</param>
    internal static void DetectEmptyFolders(IReadOnlyList<string> libraryLocations, List<Issue> issues, CancellationToken cancellationToken, ProtectionService? protection = null, Func<string, bool>? reserved = null, IReadOnlySet<string>? videoLibraryRoots = null)
    {
        foreach (var root in libraryLocations)
        {
            var videoLibrary = videoLibraryRoots?.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))) == true;
            WalkForEmpty(root, issues, isRoot: true, protection, reserved, cancellationToken, videoLibrary);
        }
    }

    private void DetectUntrackedGroupedLeftovers(IReadOnlyList<string> libraryLocations, List<Issue> issues, Func<string, bool>? reserved, CancellationToken cancellationToken)
    {
        // Jellyfin can forget a release folder once its episodes move; recover artwork-only
        // leftovers from disk rather than requiring a cached identity for that old folder.
        var series = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Series]
        }).OfType<Series>().Where(show => !string.IsNullOrWhiteSpace(show.Path)
            && Directory.Exists(show.Path) && Metadata.MediaIdentityService.IsCleanFolderName(Path.GetFileName(show.Path))
            && show.ProviderIds.TryGetValue("Tvdb", out var id) && int.TryParse(id, out var parsed) && parsed > 0).ToArray();
        foreach (var root in libraryLocations)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var masters = series.Where(show => string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(show.Path)), Path.TrimEndingDirectorySeparator(root), comparison)).ToArray();
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(folder);
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var (title, year) = Metadata.MediaIdentityService.CleanTitle(name);
                var nfoId = Metadata.MediaIdentityService.ReadSeriesNfoTvdbId(folder);
                var eligible = masters.Where(show => !string.Equals(Path.TrimEndingDirectorySeparator(show.Path), folder, comparison))
                    .Where(show => nfoId is int id ? int.Parse(show.ProviderIds["Tvdb"], CultureInfo.InvariantCulture) == id
                        : new[] { show.Name ?? string.Empty, Path.GetFileName(show.Path) }.Any(value =>
                            Metadata.MediaIdentityService.Normalize(Metadata.MediaIdentityService.CleanTitle(value).Title) == Metadata.MediaIdentityService.Normalize(title)))
                    .Where(show => year is null || (Metadata.MediaIdentityService.FolderYear(Path.GetFileName(show.Path)) ?? show.ProductionYear) is not int masterYear
                        || Math.Abs(masterYear - year.Value) <= 1).ToArray();
                if (eligible.Length != 1)
                {
                    continue;
                }

                var master = eligible[0];
                GroupedLeftover[] candidates = [new(folder, int.Parse(master.ProviderIds["Tvdb"], CultureInfo.InvariantCulture), master.Path, master.ProductionYear)];
                DetectGroupedLeftovers(candidates, libraryLocations, issues, cancellationToken, _protection, reserved, untracked: true);
            }
        }
    }

    /// <summary>
    /// Flags series folders whose TVDb title already has a different master folder and which no
    /// longer hold any video, audio, book or comic file — what an earlier grouping move left behind
    /// (artwork, <c>tvshow.nfo</c>, release notes). A folder naming a different year than its master
    /// is skipped, matching the grouping rule.
    /// </summary>
    /// <param name="candidates">Series folders grouped into another master folder.</param>
    /// <param name="libraryLocations">Enabled library roots.</param>
    /// <param name="issues">Accumulator.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="protection">Rules excluding protected paths.</param>
    /// <param name="reserved">Move paths excluded from detection.</param>
    /// <param name="untracked">True when grouping provenance is inferred from a release name.</param>
    internal static void DetectGroupedLeftovers(IReadOnlyList<GroupedLeftover> candidates, IReadOnlyList<string> libraryLocations, List<Issue> issues, CancellationToken cancellationToken, ProtectionService? protection = null, Func<string, bool>? reserved = null, bool untracked = false)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = Path.TrimEndingDirectorySeparator(candidate.Path);
            var master = Path.TrimEndingDirectorySeparator(candidate.MasterPath);
            var existing = issues.FirstOrDefault(issue => string.Equals(issue.Path, folder, comparison));
            if (string.Equals(folder, master, comparison) || !Directory.Exists(folder) || !Directory.Exists(master)
                || !string.Equals(Path.GetDirectoryName(folder), Path.GetDirectoryName(master), comparison)
                || !libraryLocations.Any(root => LibraryGuard.IsUnder(folder, root) && !string.Equals(Path.TrimEndingDirectorySeparator(root), folder, comparison))
                || (existing is not null && (!untracked || OrphanCleanupFixer.ReadKind(existing.DetailsJson) != KindEmptyFolder))
                || reserved?.Invoke(folder) == true
                || protection?.IsScannerBlocked(folder, IssueType.OrphanedDebris) == true
                || protection?.IsAnyDescendantProtected(folder, IssueType.OrphanedDebris, scanners: true) == true)
            {
                continue;
            }

            var folderYear = Metadata.MediaIdentityService.FolderYear(Path.GetFileName(folder));
            var masterYear = candidate.MasterYear ?? Metadata.MediaIdentityService.FolderYear(Path.GetFileName(master));
            if ((folderYear is int claimed && masterYear is int expected && Math.Abs(claimed - expected) > 1) || HasPlayableMedia(folder))
            {
                continue;
            }

            var size = TrySubtreeSize(folder);
            if (existing is not null)
            {
                issues.Remove(existing);
            }

            issues.Add(new Issue
            {
                Type = IssueType.OrphanedDebris,
                Path = folder,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow,
                SizeSavings = size,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    kind = KindGroupedLeftover,
                    master,
                    untracked,
                    tvdbId = candidate.TvdbId,
                    bytesEstimate = size
                }),
                SuggestedFix = string.Format(
                    CultureInfo.InvariantCulture,
                    untracked ? "Recycle leftover series artwork and metadata in \"{0}\". The show folder \"{1}\" stays unchanged; the leftovers remain recoverable even if their contents differ."
                        : "Recycle leftover folder \"{0}\" — its episodes are grouped into \"{1}\".",
                    Path.GetFileName(folder),
                    Path.GetFileName(master))
            });
        }
    }

    internal static bool HasOnlySeriesMetadata(string folder, string master, CancellationToken cancellationToken)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(master), comparison)
                || !string.Equals(Path.GetDirectoryName(folder), Path.GetDirectoryName(master), comparison))
            {
                return false;
            }

            MediaMoveSnapshot.ValidateAncestors(folder);
            MediaMoveSnapshot.ValidateAncestors(master);
            var inventory = MediaMoveSnapshot.CaptureIdentity(folder, cancellationToken).Export();
            foreach (var file in Directory.EnumerateFileSystemEntries(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                var stem = Path.GetFileNameWithoutExtension(file);
                var releaseName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                var episodeNfo = Path.GetExtension(file).Equals(".nfo", StringComparison.OrdinalIgnoreCase)
                    && Metadata.MediaIdentityService.SameReleaseName(releaseName, stem);
                var artwork = MediaFormats.Pictures.Contains(Path.GetExtension(file))
                    && (stem.ToLowerInvariant() is "backdrop" or "folder" or "landscape" or "logo" or "poster" or "banner" or "thumb" or "clearart" or "discart"
                        || Regex.IsMatch(stem, @"^season(?:\d+|-all|-specials)-poster$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                        || (stem.EndsWith("-thumb", StringComparison.OrdinalIgnoreCase) && stem.Length > 6
                            && Metadata.MediaIdentityService.SameReleaseName(releaseName, stem[..^6])));
                if ((!artwork && !episodeNfo && !name.Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase))
                    || (File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return false;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            MediaMoveSnapshot.VerifyInventory(folder, inventory, true, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsVideoLibraryMetadata(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || (MediaFormats.Pictures.Contains(extension) && JellyfinArtworkName.IsMatch(Path.GetFileNameWithoutExtension(file)));
    }

    // Video, audio, book or comic anywhere below the folder. Artwork, NFOs, release-note .txt
    // files and orphan subtitles don't count (.txt is a book format elsewhere, but in a series
    // folder it is release notes). An unreadable subtree counts as media.
    internal static bool HasPlayableMedia(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(file =>
            {
                var extension = Path.GetExtension(file);
                return MediaFormats.Video.Contains(extension) || MediaFormats.Audio.Contains(extension)
                    || (MediaFormats.Books.Contains(extension) && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    || MediaFormats.Comics.Contains(extension);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void WalkForEmpty(string dir, List<Issue> issues, bool isRoot, ProtectionService? protection, Func<string, bool>? reserved, CancellationToken cancellationToken, bool videoLibrary = false)
    {
        if (protection?.IsScannerBlocked(dir, IssueType.OrphanedDebris) == true || (!isRoot && reserved?.Invoke(dir) == true))
        {
            return;
        }

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

        var hasMedia = protection?.IsAnyDescendantProtected(dir, IssueType.OrphanedDebris, scanners: true) == true || SubtreeHasMediaOrCompanions(dir, videoLibrary);

        // Never flag the library root itself, even when it's media-free — that's a config artefact,
        // not user debris. But do recurse so a Junk/ subdirectory under an empty root still gets found.
        if (!hasMedia && !isRoot)
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
                    bytesEstimate = size,
                    videoLibrary
                }),
                SuggestedFix = string.Format(
                    CultureInfo.InvariantCulture,
                    "Delete media-free folder \"{0}\" (approx. {1} bytes of leftover metadata / sidecars).",
                    Path.GetFileName(dir),
                    size)
            });
            return; // don't recurse — deleting the parent handles all children.
        }

        foreach (var sub in subdirs)
        {
            WalkForEmpty(sub, issues, isRoot: false, protection, reserved, cancellationToken, videoLibrary);
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

    /// <summary>
    /// True when the subtree holds media or a subtitle paired with a video. In a Movies or Shows
    /// library, Jellyfin artwork (<c>folder.jpg</c>, <c>backdrop.jpg</c>, <c>&lt;name&gt;-thumb.jpg</c>…) and
    /// release-note <c>.txt</c> files are metadata: counting them as pictures and books kept every
    /// folder whose video had been moved away (a <c>movie.nfo</c> plus artwork) from ever being
    /// reported, which is how release folders holding only metadata built up beside the real ones.
    /// </summary>
    /// <param name="dir">Folder to inspect.</param>
    /// <param name="videoLibrary">True for a Movies or Shows library.</param>
    /// <returns>True when the folder must be kept.</returns>
    internal static bool SubtreeHasMediaOrCompanions(string dir, bool videoLibrary = false)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(f);
                if (videoLibrary && IsVideoLibraryMetadata(f))
                {
                    continue;
                }

                if (MediaExtensions.Contains(extension) || (SubtitleExtensions.Contains(extension) && HasCompanionVideo(f)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Treat unreadable subtree as "has media" — conservative: don't flag for deletion.
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
    /// <param name="protection">Rules excluding protected paths from analysis.</param>
    /// <param name="reserved">Move paths excluded from detection.</param>
    internal static void DetectOrphanSubtitles(IReadOnlyList<string> libraryLocations, List<Issue> issues, CancellationToken cancellationToken, ProtectionService? protection = null, Func<string, bool>? reserved = null)
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
                if (reserved?.Invoke(file) == true || protection?.IsScannerBlocked(file, IssueType.OrphanedDebris) == true || protection?.IsAnyDescendantProtected(file, IssueType.OrphanedDebris, scanners: true) == true)
                {
                    continue;
                }

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
    /// (or in the immediate parent, for the common <c>Season 01/Subtitles/</c> layout) whose basename
    /// is a prefix of the subtitle's basename. This handles arbitrary trailing metadata tokens Jellyfin
    /// recognises — language (<c>.en</c>), hearing-impaired variants (<c>.sdh</c>, <c>.hi</c>, <c>.cc</c>),
    /// modifiers (<c>.forced</c>, <c>.default</c>, <c>.foreign</c>, <c>.sign</c>, <c>.commentary</c>) and
    /// any combination of them (<c>Foo.en.sdh.srt</c>, <c>Foo.forced.srt</c>, …) without maintaining a
    /// closed list of flavour tokens. Exposed internal for direct unit-testing.
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

        if (HasCompanionVideoIn(dir, subBase))
        {
            return true;
        }

        // Field report B4: some libraries keep subtitles in a Subtitles/ (or Subs/) subfolder next to
        // the season's video files. Look one level up so the parent-dir video still counts as the
        // companion. Only walk up when the immediate folder is a known subtitle-container name;
        // anything else could be an unrelated adjacent folder.
        var subFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        if (IsSubtitleContainerFolder(subFolderName))
        {
            var parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && HasCompanionVideoIn(parent, subBase))
            {
                return true;
            }
        }

        return false;
    }

    // True when a directory name plausibly means "this holds subtitles" — whitelist match or a
    // name that starts with "sub" / "caption". Catches Subs-EN, Subtitles (SDH), CaptionsForced,
    // etc. without an exhaustive list. False positives ("Subaru", "SubmarineDocs") would need to
    // also sit next to unrelated video files to matter, which is vanishingly rare in a real media
    // library, so the prefix rule is a net win vs. missed real subtitle folders.
    internal static bool IsSubtitleContainerFolder(string folderName)
    {
        if (string.IsNullOrEmpty(folderName))
        {
            return false;
        }

        if (SubtitleContainerFolders.Contains(folderName))
        {
            return true;
        }

        return folderName.StartsWith("sub", StringComparison.OrdinalIgnoreCase)
            || folderName.StartsWith("caption", StringComparison.OrdinalIgnoreCase);
    }

    // A subtitle is a companion when a sibling video's basename either equals the sidecar basename
    // (Foo.mkv + Foo.srt) or is a dotted prefix of it (Foo.mkv + Foo.en.sdh.srt, Foo.mkv + Foo.forced.srt).
    // Prefix requires a following "." so Foo.mkv doesn't accidentally match Foobar.srt. False negatives
    // (weird custom naming) are fine — this pass deletes files, so under-flagging is the safe direction.
    private static bool HasCompanionVideoIn(string dir, string subBase)
    {
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
            if (string.IsNullOrEmpty(vidBase))
            {
                continue;
            }

            if (string.Equals(vidBase, subBase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (subBase.Length > vidBase.Length + 1
                && subBase[vidBase.Length] == '.'
                && subBase.AsSpan(0, vidBase.Length).Equals(vidBase.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
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
    /// <param name="protection">Rules excluding protected paths from analysis.</param>
    /// <param name="reserved">Move paths excluded from detection.</param>
    internal static void DetectOrphanTrickplay(IReadOnlyList<string> libraryLocations, List<Issue> issues, CancellationToken cancellationToken, ProtectionService? protection = null, Func<string, bool>? reserved = null)
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
                if (reserved?.Invoke(tp) == true || protection?.IsScannerBlocked(tp, IssueType.OrphanedDebris) == true || protection?.IsAnyDescendantProtected(tp, IssueType.OrphanedDebris, scanners: true) == true)
                {
                    continue;
                }

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
    /// <param name="reserved">Move paths excluded from detection.</param>
    /// <param name="reservedItem">Move item identities excluded from detection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private void DetectOrphanMetadata(List<Issue> issues, Func<string, bool>? reserved, Func<Guid, bool>? reservedItem, CancellationToken cancellationToken)
    {
        var locations = _libraryGuard.GetEnabledLibraryLocations(Plugin.Instance!.Configuration.EnabledLibraries);
        var libraryRoot = Path.Combine(_appPaths.InternalMetadataPath, "library");
        if (!Directory.Exists(libraryRoot) || locations.Count == 0)
        {
            return;
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
                if (reserved?.Invoke(itemDir) == true || _protection?.IsScannerBlocked(itemDir, Type) == true || _protection?.IsAnyDescendantProtected(itemDir, Type, scanners: true) == true)
                {
                    continue;
                }

                var name = Path.GetFileName(itemDir);
                if (!Guid.TryParse(name, out var itemId) || reservedItem?.Invoke(itemId) == true)
                {
                    continue;
                }

                if (!_libraryGuard.IsOrphanMetadataInEnabledLocations(itemDir, itemId, locations) || !LibraryGuard.HasOnlyManagedMetadata(itemDir))
                {
                    continue;
                }

                if (!OrphanMetadataObservation.TryCapture(itemDir, out var observation, cancellationToken))
                {
                    continue;
                }

                var size = TrySubtreeSize(itemDir);
                var observedAt = DateTime.UtcNow;
                issues.Add(new Issue
                {
                    Type = IssueType.OrphanedDebris,
                    ItemId = itemId,
                    Path = itemDir,
                    Status = IssueStatus.Detected,
                    DetectedAtUtc = observedAt,
                    SizeSavings = size,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        kind = KindOrphanMetadata,
                        bytesEstimate = size,
                        metadataObservation = new { version = 1, observedAtUtcTicks = observedAt.Ticks, signature = observation.Signature }
                    }),
                    SuggestedFix = string.Format(
                        CultureInfo.InvariantCulture,
                        "Delete orphan metadata for missing item {0} (approx. {1} bytes) after Jellyfin's Scan Media Library task confirms it is still missing.",
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

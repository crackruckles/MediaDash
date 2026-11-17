using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "type", Target = "~T:Jellyfin.Plugin.MediaDash.Fixers.MediaMovePlanner", Justification = "The planner validates all source/destination paths and inventories against LibraryGuard before proposing a move; it never mutates files.")]

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Plans complete media moves without modifying the library.</summary>
public sealed partial class MediaMovePlanner
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly HashSet<string> CompanionExtensions = new(SubtitleFormats.Extensions, StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".jpg", ".jpeg", ".png", ".webp", ".tbn", ".bif", ".lrc", ".smi", ".xml", ".opf"
    };

    private static readonly HashSet<string> ExtraFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "trailers", "featurettes", "behind the scenes", "deleted scenes", "interviews", "scenes", "shorts", "other", "backdrops", "screenshots"
    };

    private readonly LibraryGuard _guard;
    private readonly ProtectionService _protection;
    private readonly ILibraryManager _library;
    private readonly MediaMoveStore _moves;

    /// <summary>Initializes a new instance of the <see cref="MediaMovePlanner"/> class.</summary>
    /// <param name="guard">Current library scope.</param>
    /// <param name="protection">Current protection rules.</param>
    /// <param name="library">Current item identities and containers.</param>
    /// <param name="moves">Durable path reservations.</param>
    public MediaMovePlanner(LibraryGuard guard, ProtectionService protection, ILibraryManager library, MediaMoveStore moves)
    {
        _guard = guard;
        _protection = protection;
        _library = library;
        _moves = moves;
    }

    internal static InternalItemsQuery CatalogQuery() => new()
    {
        Recursive = true,
        IsVirtualItem = false,
        IncludeItemTypes =
        [
            BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.AudioBook,
            BaseItemKind.Book, BaseItemKind.MusicVideo, BaseItemKind.Photo, BaseItemKind.Video,
            BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.MusicAlbum, BaseItemKind.PhotoAlbum
        ]
    };

    internal Task<IReadOnlyList<Issue>> ScanAsync(IssueType type, IReadOnlyList<BaseItem> items, PluginConfiguration config, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plans = new List<MediaMovePlan>();
        var plannedSources = new HashSet<string>(Paths);
        var processed = 0;
        void ReadingProgress(string path, long read, long length)
        {
            Plugin.CurrentActivity = path;
            Plugin.CurrentActivityLabel = "Checking move files (" + (processed + 1) + "/" + items.Count + "; " + (length == 0 ? 100 : read * 100 / length) + "% of file)";
            progress.Report(items.Count == 0 ? 0 : (double)processed / items.Count * 90);
        }

        foreach (var item in items.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plugin.CurrentActivity = item.Path;
            Plugin.CurrentActivityLabel = "Planning media moves (" + (processed + 1) + "/" + items.Count + ")";
            progress.Report(items.Count == 0 ? 0 : (double)processed / items.Count * 90);
            try
            {
                var plan = Build(type, item, items, config, plannedSources, ReadingProgress, cancellationToken);
                if (plan is not null && plannedSources.Add(plan.Entries[0].Source))
                {
                    plans.Add(plan);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Api.Diagnostics.Record("MediaMove.Plan", "The move could not be planned for '" + item.Path + "': " + ex.Message + ". The source was left untouched.");
            }

            processed++;
            progress.Report(items.Count == 0 ? 90 : (double)processed / items.Count * 90);
        }

        for (var index = 0; index < plans.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plugin.CurrentActivity = plans[index].Entries[0].Source;
            Plugin.CurrentActivityLabel = "Checking move conflicts (" + (index + 1) + "/" + plans.Count + ")";
            progress.Report(90 + ((double)index / plans.Count * 10));
            var plan = plans[index];
            if (plans.Where((_, other) => other != index).Any(other => plan.Entries.Any(entry => other.Entries.Any(candidate =>
                    Overlap(entry.Source, candidate.Source) || Overlap(entry.Destination, candidate.Destination)
                    || Overlap(entry.Source, candidate.Destination) || Overlap(entry.Destination, candidate.Source)
                    || (plan.ContainerIdentity != other.ContainerIdentity && !entry.IsDirectory && !candidate.IsDirectory
                        && Paths.Equals(Path.GetDirectoryName(entry.Destination), Path.GetDirectoryName(candidate.Destination)))))))
            {
                plan = plan with { BlockingReasons = [.. plan.BlockingReasons, "This move overlaps another proposed move. Review the complete selection before moving it."] };
            }

            plans[index] = plan.Seal();
        }

        progress.Report(100);
        return Task.FromResult<IReadOnlyList<Issue>>(plans.Select(ToIssue).ToArray());
    }

    private MediaMovePlan? Build(IssueType type, BaseItem item, IReadOnlyList<BaseItem> peers, PluginConfiguration config, HashSet<string> plannedSources, Action<string, long, long> readingProgress, CancellationToken cancellationToken)
    {
        var directoryDisc = IsDirectoryDisc(item);
        if (string.IsNullOrWhiteSpace(item.Path) || (!File.Exists(item.Path) && !directoryDisc))
        {
            return null;
        }

        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path));
        var roots = _guard.GetEnabledLibraryLocations(config.EnabledLibraries);
        var sourceRoot = roots.Where(root => LibraryGuard.IsUnder(path, root)).OrderByDescending(root => root.Length).FirstOrDefault();
        if (sourceRoot is null)
        {
            return null;
        }

        var kind = MediaSorterScanner.ClassifyItem(item, path, config.MediaSortSource, !string.IsNullOrWhiteSpace(config.AnimeTargetPath), !string.IsNullOrWhiteSpace(config.ComicsTargetPath));
        var targetRoot = kind switch
        {
            MediaSorterScanner.MediaKind.Movie => config.MoviesTargetPath,
            MediaSorterScanner.MediaKind.Tv => config.TvTargetPath,
            MediaSorterScanner.MediaKind.Anime => config.AnimeTargetPath,
            MediaSorterScanner.MediaKind.Music => config.MusicTargetPath,
            MediaSorterScanner.MediaKind.AudioBook or MediaSorterScanner.MediaKind.Book => config.BooksTargetPath,
            MediaSorterScanner.MediaKind.Comic => config.ComicsTargetPath,
            MediaSorterScanner.MediaKind.Picture => config.PicturesTargetPath,
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            return null;
        }

        targetRoot = Path.GetFullPath(targetRoot);
        var atTarget = LibraryGuard.IsUnder(path, targetRoot);
        if ((type == IssueType.Misplaced && atTarget) || (type == IssueType.Ungrouped && !atTarget))
        {
            return null;
        }

        if (type == IssueType.Ungrouped && kind is not (MediaSorterScanner.MediaKind.Movie or MediaSorterScanner.MediaKind.Tv))
        {
            return null;
        }

        var blockers = new List<string>();
        var source = path;
        var destination = string.Empty;
        var parent = Path.GetDirectoryName(path)!;
        if (item is Audio audio && FindAlbumContainer(audio, sourceRoot, peers) is { } albumContainer)
        {
            parent = albumContainer;
        }

        var itemPaths = MediaFileHelper.GetMovePayloadPaths(item).Select(Path.GetFullPath).Distinct(Paths).ToArray();
        var declaredCompanions = item is Video external ? external.AudioFiles.Concat(external.SubtitleFiles).Where(value => !string.IsNullOrWhiteSpace(value)).Select(Path.GetFullPath).Distinct(Paths).ToArray() : [];
        foreach (var declared in itemPaths.Concat(declaredCompanions))
        {
            if (!InScope(declared, roots))
            {
                blockers.Add("A declared payload is outside currently enabled library folders: '" + declared + "'. Its contents were not read.");
            }
            else if (!File.Exists(declared) && !Directory.Exists(declared))
            {
                blockers.Add("A declared payload is missing: '" + declared + "'. Restore it or refresh Jellyfin's item metadata before approval.");
            }
        }

        if (directoryDisc)
        {
            if (type == IssueType.Ungrouped)
            {
                return null;
            }

            source = Path.GetFileName(path).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("BDMV", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(path)! : path;
            destination = Path.Combine(targetRoot, Path.GetFileName(source));
            if (!roots.Any(root => Paths.Equals(Path.TrimEndingDirectorySeparator(root), source)))
            {
                CheckScopedContainer(source, item, peers, roots, blockers);
            }
        }
        else if (item is Episode episode)
        {
            var series = episode.SeriesId == Guid.Empty ? null : peers.OfType<Series>().FirstOrDefault(candidate => candidate.Id == episode.SeriesId)
                ?? _library.GetItemById(episode.SeriesId) as Series;
            var name = MediaGrouperScanner.ResolveSafeSeriesName(episode.SeriesName, series?.PremiereDate?.Year, path, sourceRoot);
            if (string.IsNullOrWhiteSpace(name))
            {
                blockers.Add("The series identity could not be established. Check this item's series metadata before moving it.");
                name = Path.GetFileNameWithoutExtension(path);
            }

            var seriesRoot = Path.Combine(targetRoot, RenameTemplate.Scrub(name));
            if (atTarget && LibraryGuard.IsUnder(path, seriesRoot))
            {
                return null;
            }

            if (series is not null && !string.IsNullOrWhiteSpace(series.Path) && LibraryGuard.IsUnder(path, series.Path)
                && !Paths.Equals(Path.TrimEndingDirectorySeparator(series.Path), Path.TrimEndingDirectorySeparator(sourceRoot)))
            {
                source = Path.GetFullPath(series.Path);
                destination = atTarget ? seriesRoot : Path.Combine(targetRoot, Path.GetFileName(source));
                CheckScopedContainer(source, item, peers, roots, blockers);
            }
            else if (!Paths.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(sourceRoot)))
            {
                source = parent;
                destination = Path.Combine(seriesRoot, Path.GetFileName(parent));
                CheckScopedContainer(source, item, peers, roots, blockers);
            }
            else
            {
                var season = episode.ParentIndexNumber;
                if (season is null)
                {
                    blockers.Add("The season is unknown. Check the episode metadata before moving it.");
                }

                destination = Path.Combine(seriesRoot, season is null ? "Season unknown" : "Season " + season.Value.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(path));
            }
        }
        else
        {
            var dedicated = !Paths.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(sourceRoot))
                && IsDedicated(parent, item, peers);
            if (type == IssueType.Ungrouped && dedicated)
            {
                return null;
            }

            if (dedicated)
            {
                source = parent;
                destination = Path.Combine(targetRoot, Path.GetFileName(parent));
                CheckScopedContainer(source, item, peers, roots, blockers);
            }
            else
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                if (itemPaths.Length > 1)
                {
                    var versionStems = itemPaths.Select(Path.GetFileNameWithoutExtension).ToArray();
                    var multipart = item is Video video && video.AdditionalParts.Length > 0;
                    var prefixes = versionStems.Select(value => multipart ? MultipartSuffix().Replace(value!, string.Empty) : value!.Split(" - ", StringSplitOptions.None)[0])
                        .Distinct(StringComparer.Ordinal).ToArray();
                    if (prefixes.Length != 1)
                    {
                        blockers.Add("The versions do not share one folder prefix. Review their filenames before moving them.");
                    }
                    else
                    {
                        stem = prefixes[0];
                    }
                }

                destination = Path.Combine(targetRoot, stem, Path.GetFileName(path));
                if (item is not Movie)
                {
                    blockers.Add("This item is in a shared container. Select its complete album, book or photo folder in Files instead of moving only this file.");
                }
            }
        }

        if (Paths.Equals(source, destination) || plannedSources.Contains(source))
        {
            return null;
        }

        var actions = new List<(string Source, string Destination)> { (source, destination) };
        if (File.Exists(source))
        {
            foreach (var alternate in itemPaths.Concat(declaredCompanions).Where(candidate => !Paths.Equals(candidate, source)))
            {
                actions.Add((alternate, Path.Combine(Path.GetDirectoryName(destination)!, Path.GetFileName(alternate))));
            }

            AddCompanions(actions, peers, roots, blockers);
        }
        else if (itemPaths.Concat(declaredCompanions).Any(declared => !LibraryGuard.IsUnder(declared, source)))
        {
            blockers.Add("A declared version, part or companion lies outside this complete container. Review the complete item before moving.");
        }

        var entries = new List<MediaMovePlan.Entry>();
        foreach (var action in actions.DistinctBy(action => action.Source, Paths).OrderBy(action => action.Source, StringComparer.Ordinal))
        {
            Preflight(type, action.Source, action.Destination, roots, blockers);
            try
            {
                if (!InScope(action.Source, roots) || !InScope(action.Destination, roots)
                    || roots.Any(root => Paths.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(action.Source))))
                {
                    entries.Add(new(action.Source, action.Destination, false, []));
                    continue;
                }

                var inventory = MediaMoveSnapshot.Capture(action.Source, readingProgress, cancellationToken).Export();
                entries.Add(new(action.Source, action.Destination, Directory.Exists(action.Source), inventory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                blockers.Add("The complete contents could not be verified at '" + action.Source + "': " + ex.Message);
                entries.Add(new(action.Source, action.Destination, Directory.Exists(action.Source), []));
            }
        }

        // Preserve the primary as the first action; its path is the finding's durable source binding.
        entries = entries.OrderBy(entry => Paths.Equals(entry.Source, source) ? 0 : 1).ThenBy(entry => entry.Source, StringComparer.Ordinal).ToList();
        var destinationContainer = MediaMoveManifest.ClaimContainer(destination, Directory.Exists(source));
        if (item is Movie)
        {
            try
            {
                if (HasForeignMovieDestination(destinationContainer, entries))
                {
                    blockers.Add("The destination movie folder already contains other content. Review its complete contents in Files instead of merging automatically.");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                blockers.Add("The destination movie folder's complete contents could not be verified. Review it before moving.");
            }
        }

        return new MediaMovePlan
        {
            Type = type,
            ItemId = item.Id,
            ItemPath = Path.GetFullPath(item.Path),
            Identity = Identity(item),
            ContainerIdentity = ContainerIdentity(item),
            DestinationContainer = destinationContainer,
            DestinationParents = entries.All(entry => InScope(entry.Destination, roots)) ? MediaMoveManifest.CaptureDestinationParents(entries.ToArray()) : [],
            SourceScopes = CaptureSourceScopes(entries.Where(entry => InScope(entry.Source, roots)).ToArray()),
            ItemBindings = CaptureItemBindings(entries.ToArray(), peers, item is Episode metadataEpisode && metadataEpisode.SeriesId != Guid.Empty ? _library.GetItemById(metadataEpisode.SeriesId) : null),
            PolicyStamp = PolicyStamp(config, type),
            Entries = entries.ToArray(),
            BlockingReasons = blockers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        }.Seal();
    }

    private static bool IsDedicated(string folder, BaseItem owner, IReadOnlyList<BaseItem> peers) =>
        !peers.Any(peer => peer.Id != owner.Id && MediaSorterScanner.ClassifyByBaseItemKind(peer.GetBaseItemKind()) is not null
            && MediaFileHelper.GetMovePayloadPaths(peer).Any(path => LibraryGuard.IsUnder(Path.GetFullPath(path), folder)) && !SameOwner(owner, peer));

    private static bool SameOwner(BaseItem first, BaseItem other) => first.Id == other.Id
        || (first is Episode episode && other is Episode second && episode.SeriesId != Guid.Empty && episode.SeriesId == second.SeriesId)
        || (first is Audio audio && other is Audio secondAudio && !string.IsNullOrWhiteSpace(audio.Album) && audio.AlbumArtists.Count > 0
            && string.Equals(audio.Album, secondAudio.Album, StringComparison.Ordinal)
            && audio.AlbumArtists.Order(StringComparer.Ordinal).SequenceEqual(secondAudio.AlbumArtists.Order(StringComparer.Ordinal), StringComparer.Ordinal));

    private static string? FindAlbumContainer(Audio audio, string sourceRoot, IReadOnlyList<BaseItem> peers)
    {
        var declared = peers.OfType<MusicAlbum>().FirstOrDefault(album => !string.IsNullOrWhiteSpace(album.Path)
            && LibraryGuard.IsUnder(audio.Path, album.Path) && !Paths.Equals(Path.TrimEndingDirectorySeparator(album.Path), Path.TrimEndingDirectorySeparator(sourceRoot)));
        if (declared is not null)
        {
            return declared.Path;
        }

        if (!string.IsNullOrWhiteSpace(audio.Album) && audio.AlbumArtists.Count > 0)
        {
            for (var parent = Path.GetDirectoryName(audio.Path); !string.IsNullOrEmpty(parent)
                && !Paths.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(sourceRoot)); parent = Path.GetDirectoryName(parent))
            {
                if (Paths.Equals(Path.GetFileName(parent), RenameTemplate.Scrub(audio.Album)))
                {
                    return parent;
                }
            }
        }

        return null;
    }

    private static void CheckContainer(string folder, BaseItem item, IReadOnlyList<BaseItem> peers, List<string> blockers)
    {
        if (!IsDedicated(folder, item, peers))
        {
            blockers.Add("This folder contains more than one identified item. Review ownership before moving the complete folder.");
        }

        var known = peers.Where(peer => SameOwner(item, peer)).SelectMany(MediaFileHelper.GetMovePayloadPaths).Select(Path.GetFullPath).ToHashSet(Paths);
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    blockers.Add("The container includes a linked path: '" + Path.GetRelativePath(folder, file) + "'. Its target was not inspected.");
                    continue;
                }

                if (Directory.Exists(file))
                {
                    pending.Push(file);
                    continue;
                }

                var relative = Path.GetRelativePath(folder, file);
                var first = relative.Split(Path.DirectorySeparatorChar)[0];
                if (known.Contains(file) || ExtraFolders.Contains(first) || first.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase)
                    || IsOwnedDiscStructure(item, first)
                    || !IsPrimaryForOwner(item, file))
                {
                    continue;
                }

                blockers.Add("The folder includes an unidentified media file: '" + relative + "'. Identify it or move it separately first.");
            }
        }
    }

    private static bool IsDirectoryDisc(BaseItem item) => item is Video video && Directory.Exists(item.Path)
        && (video.VideoType.ToString().Equals("Dvd", StringComparison.OrdinalIgnoreCase) || video.VideoType.ToString().Equals("BluRay", StringComparison.OrdinalIgnoreCase));

    private static bool IsOwnedDiscStructure(BaseItem item, string first) => IsDirectoryDisc(item)
        && ((first.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase) && ((Video)item).VideoType.ToString().Equals("Dvd", StringComparison.OrdinalIgnoreCase))
            || (first.Equals("BDMV", StringComparison.OrdinalIgnoreCase) && ((Video)item).VideoType.ToString().Equals("BluRay", StringComparison.OrdinalIgnoreCase)));

    private static bool IsPrimaryForOwner(BaseItem item, string path)
    {
        var extension = Path.GetExtension(path);
        var sourceExtension = Path.GetExtension(item.Path);
        if (item is Audio || (MediaFormats.Audio.Contains(sourceExtension) && !MediaFormats.Video.Contains(sourceExtension)))
        {
            return MediaFormats.Audio.Contains(extension) && extension.ToUpperInvariant() is not (".CUE" or ".M3U" or ".M3U8" or ".PLS" or ".XSP");
        }

        if (MediaFormats.Books.Contains(sourceExtension) || MediaFormats.Comics.Contains(sourceExtension))
        {
            return MediaFormats.Books.Contains(extension) || MediaFormats.Comics.Contains(extension);
        }

        return item is Photo || MediaFormats.Pictures.Contains(sourceExtension) ? MediaFormats.Pictures.Contains(extension) : MediaFormats.Video.Contains(extension);
    }

    private void AddCompanions(List<(string Source, string Destination)> actions, IReadOnlyList<BaseItem> peers, IReadOnlyList<string> roots, List<string> blockers, bool keepSharedInPlace = false)
    {
        foreach (var parent in actions.Select(action => Path.GetDirectoryName(action.Source)!).Distinct(Paths).ToArray())
        {
            if (!InScope(parent, roots) || !Directory.Exists(parent))
            {
                continue;
            }

            MediaMoveSnapshot.ValidateAncestors(parent);
            var primaries = Directory.EnumerateFiles(parent).Where(path => MediaFormats.Video.Contains(Path.GetExtension(path)) || MediaFormats.Audio.Contains(Path.GetExtension(path))).ToArray();
            foreach (var child in Directory.EnumerateFileSystemEntries(parent)
                .OrderBy(path => MediaFormats.Audio.Contains(Path.GetExtension(path)) ? 0 : 1)
                .ThenBy(path => Path.GetFileNameWithoutExtension(path).Length).ThenBy(path => path, StringComparer.Ordinal))
            {
                if (actions.Any(action => Paths.Equals(action.Source, child)))
                {
                    continue;
                }

                var name = Path.GetFileName(child);
                if (Directory.Exists(child) && peers.Any(peer => MediaFileHelper.GetMovePayloadPaths(peer)
                    .Any(path => Path.IsPathFullyQualified(path) && LibraryGuard.IsUnder(path, child))))
                {
                    // An identified sibling's container is not a shared asset of this loose item.
                    continue;
                }

                if (File.Exists(child) && (MediaFormats.Video.Contains(Path.GetExtension(child))
                    || (MediaFormats.Audio.Contains(Path.GetExtension(child)) && peers.OfType<Audio>().Any(audio => Paths.Equals(audio.Path, child)))))
                {
                    continue;
                }

                var owners = primaries.Where(primary => !Paths.Equals(primary, child) && (name.StartsWith(Path.GetFileNameWithoutExtension(primary) + ".", PathComparison)
                    || name.StartsWith(Path.GetFileNameWithoutExtension(primary) + "-", PathComparison)))
                    .OrderByDescending(primary => Path.GetFileNameWithoutExtension(primary).Length).ToArray();
                var owner = owners.FirstOrDefault();
                var action = actions.FirstOrDefault(candidate => Paths.Equals(candidate.Source, owner));
                if (action.Source is not null)
                {
                    if (owners.Length > 1 && Path.GetFileNameWithoutExtension(owners[0]).Length == Path.GetFileNameWithoutExtension(owners[1]).Length)
                    {
                        blockers.Add("Companion ownership is ambiguous for '" + name + "'.");
                    }
                    else if (CompanionExtensions.Contains(Path.GetExtension(child)) || MediaFormats.Audio.Contains(Path.GetExtension(child))
                        || (Directory.Exists(child) && name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase)))
                    {
                        actions.Add((child, Path.Combine(Path.GetDirectoryName(action.Destination)!, name)));
                    }
                    else
                    {
                        blockers.Add("Review the unrecognized companion '" + name + "' before moving this item.");
                    }
                }
                else if (!keepSharedInPlace && owner is null && (Directory.Exists(child) || CompanionExtensions.Contains(Path.GetExtension(child)) || name == ".ignore"))
                {
                    blockers.Add("The shared file or folder '" + name + "' has no unambiguous owner. Review it before moving media out of this folder.");
                }
            }
        }
    }

    private bool InScope(string path, IReadOnlyList<string> roots) => _guard.IsInsideLibrary(path) && roots.Any(root => LibraryGuard.IsUnder(path, root));

    internal static MediaMovePlan.DirectoryScope[] CaptureSourceScopes(MediaMovePlan.Entry[] entries, bool genericSelection = false) => entries.Where(entry => !entry.IsDirectory)
        .Select(entry => Path.GetDirectoryName(entry.Source)!).Distinct(Paths).Where(Directory.Exists)
        .Select(parent => new MediaMovePlan.DirectoryScope(parent, SourceScopeFingerprint(ReadSourceNames(parent, entries, genericSelection)))).ToArray();

    internal static string SourceScopeFingerprint(IEnumerable<string> names) => MediaMovePlan.Hash(JsonSerializer.Serialize(names
        .Select(name => OperatingSystem.IsWindows() ? name.ToUpperInvariant() : name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));

    internal static string[] ReadSourceNames(string parent, MediaMovePlan.Entry[] entries, bool genericSelection = false)
    {
        MediaMoveSnapshot.ValidateAncestors(parent);
        if (genericSelection)
        {
            return Directory.EnumerateFileSystemEntries(parent).Where(path => entries.Any(entry => Paths.Equals(entry.Source, path)))
                .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        }

        var stems = entries.Where(entry => Paths.Equals(Path.GetDirectoryName(entry.Source), parent)).Select(entry => Path.GetFileNameWithoutExtension(entry.Source)).ToArray();
        var children = Directory.EnumerateFileSystemEntries(parent).ToArray();
        var primaries = children.Where(child => !Directory.Exists(child)
            && (MediaFormats.Video.Contains(Path.GetExtension(child)) || MediaFormats.Audio.Contains(Path.GetExtension(child)))).ToArray();
        return children.Where(Relevant).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

        bool Relevant(string child)
        {
            var name = Path.GetFileName(child);
            var associated = entries.Any(entry => Paths.Equals(entry.Source, child))
                || stems.Any(stem => name.StartsWith(stem + ".", PathComparison) || name.StartsWith(stem + "-", PathComparison));
            if (Directory.Exists(child))
            {
                if (entries.Any(entry => LibraryGuard.IsUnder(entry.Destination, child)))
                {
                    // Destination vacancy/identity is checked separately, including parents that
                    // an earlier approved item created in this same run.
                    return false;
                }

                if (associated || ExtraFolders.Contains(name) || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                // A separate media container stays independent. Unknown/shared asset folders
                // remain in the scope so newly arriving ambiguous assets require another preview.
                return !Directory.EnumerateFiles(child).Any(file => MediaFormats.Video.Contains(Path.GetExtension(file)) || MediaFormats.Audio.Contains(Path.GetExtension(file)));
            }

            if (associated || name == ".ignore")
            {
                return true;
            }

            if (!CompanionExtensions.Contains(Path.GetExtension(child)))
            {
                return false;
            }

            // Keep generic/shared assets in scope, while unrelated titles retain ownership of
            // their own named companions and can finish an earlier approved operation.
            return !primaries.Any(primary => name.StartsWith(Path.GetFileNameWithoutExtension(primary) + ".", PathComparison)
                || name.StartsWith(Path.GetFileNameWithoutExtension(primary) + "-", PathComparison));
        }
    }

    private void CheckScopedContainer(string source, BaseItem item, IReadOnlyList<BaseItem> peers, IReadOnlyList<string> roots, List<string> blockers)
    {
        if (!InScope(source, roots))
        {
            blockers.Add("The complete source container is outside currently enabled library folders. Its contents were not read.");
            return;
        }

        MediaMoveSnapshot.ValidateAncestors(source);
        CheckContainer(source, item, peers, blockers);
    }

    private void Preflight(IssueType type, string source, string target, IReadOnlyList<string> roots, List<string> blockers)
    {
        if (InScope(source, roots) && InScope(target, roots) && MediaMoveSafety.RequiresCopyDelete(source, target))
        {
            blockers.Add(MediaMoveSafety.UnsafeSourceDeletionMessage);
        }

        if (roots.Any(root => Paths.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(source))))
        {
            blockers.Add("The selected content is a configured library root. Select a complete item inside that library instead.");
        }

        if (_moves.HasActiveConflict(source) || _moves.HasActiveConflict(target))
        {
            blockers.Add("A move operation has reserved part of this item or destination. Review that operation before moving it again.");
        }

        if (!_guard.IsInsideLibrary(source) || !_guard.IsInsideLibrary(target)
            || !LibraryGuard.IsInEnabledLocations(source, roots) || !LibraryGuard.IsInEnabledLocations(target, roots))
        {
            blockers.Add("The complete move must remain inside currently enabled library folders.");
        }

        if (Overlap(source, target))
        {
            blockers.Add("The source and destination overlap.");
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            blockers.Add("The destination is already occupied: '" + target + "'. Nothing will be overwritten or merged.");
        }

        if (_protection.IsFixerBlocked(source, type) || _protection.IsFixerBlocked(target, type)
            || _protection.IsAnyDescendantProtected(source, type) || _protection.IsAnyDescendantProtected(target, type))
        {
            blockers.Add("The move includes a protected path. Remove its protection only if the complete move is intended.");
        }

        for (var parent = Path.GetDirectoryName(source); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if (!Paths.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(target)) && File.Exists(Path.Combine(parent, ".ignore")))
            {
                blockers.Add("Moving out of this folder changes a shared .ignore scope. Review the exclusion before moving it.");
            }
        }
    }

    internal static string PolicyStamp(PluginConfiguration config, IssueType type) => MediaMovePlan.Hash(JsonSerializer.Serialize(
        new
        {
            mode = config.GetFixMode(type), config.EnabledLibraries, config.MediaSortSource,
            config.MoviesTargetPath, config.TvTargetPath, config.AnimeTargetPath,
            config.MusicTargetPath, config.BooksTargetPath, config.ComicsTargetPath, config.PicturesTargetPath
        },
        MediaMovePlan.JsonOptions) + (type == IssueType.ManualMove ? "|dryRun=" + config.DryRun : string.Empty));

    internal static bool HasForeignMovieDestination(string container, IReadOnlyList<MediaMovePlan.Entry> entries, MediaMoveManifest? manifest = null)
    {
        if (!Directory.Exists(container) || entries.Any(entry => entry.IsDirectory && Paths.Equals(entry.Destination, container)))
        {
            return false;
        }

        MediaMoveSnapshot.ValidateAncestors(container);
        return Directory.EnumerateFileSystemEntries(container).Any(child => !entries.Any(entry => LibraryGuard.IsUnder(entry.Destination, child))
            && manifest?.Steps.Any(step => Paths.Equals(step.Stage, child)) != true);
    }

    internal static bool Overlap(string first, string second) => LibraryGuard.IsUnder(first, second) || LibraryGuard.IsUnder(second, first);

    internal static MediaMovePlan.ItemBinding[] CaptureItemBindings(MediaMovePlan.Entry[] entries, IReadOnlyList<BaseItem> peers, BaseItem? relatedContainer = null) => peers
        .Where(peer => Path.IsPathFullyQualified(peer.Path) && entries.Any(entry => LibraryGuard.IsUnder(peer.Path, entry.Source)))
        .Concat(relatedContainer is not null && Path.IsPathFullyQualified(relatedContainer.Path) && entries.Any(entry => Overlap(relatedContainer.Path, entry.Source)) ? [relatedContainer] : [])
        .Where(peer => peer.Id != Guid.Empty).DistinctBy(peer => peer.Id).OrderBy(peer => peer.Id)
        .Select(peer => new MediaMovePlan.ItemBinding(peer.Id, Path.TrimEndingDirectorySeparator(Path.GetFullPath(peer.Path)), Identity(peer))).ToArray();

    internal static string Identity(BaseItem item, MediaMovePlan? movedPlan = null) => MediaMovePlan.Hash(JsonSerializer.Serialize(
        new
        {
            item.Id, item.Name, item.ProductionYear, providers = item.ProviderIds.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            series = item is Episode episode ? episode.SeriesId : Guid.Empty,
            premiereYear = item.PremiereDate?.Year,
            genres = item.Genres?.Order(StringComparer.Ordinal).ToArray(),
            mediaKind = item.GetType().FullName,
            videoType = item is Video typedVideo ? typedVideo.VideoType.ToString() : null,
            episodeMetadata = item is Episode episodeItem ? new { episodeItem.SeriesName, episodeItem.ParentIndexNumber, episodeItem.IndexNumber } : null,
            albumMetadata = item is Audio audioItem ? new { audioItem.Album, artists = audioItem.AlbumArtists?.Order(StringComparer.Ordinal).ToArray() } : null,
            declared = item is Video video ? new
            {
                alternates = NormalizeDeclaredPaths(video.LocalAlternateVersions, movedPlan),
                parts = NormalizeDeclaredPaths(video.AdditionalParts, movedPlan),
                audio = NormalizeDeclaredPaths(video.AudioFiles, movedPlan),
                subtitles = NormalizeDeclaredPaths(video.SubtitleFiles, movedPlan)
            }
            : null
        },
        MediaMovePlan.JsonOptions));

    private static string[]? NormalizeDeclaredPaths(string[]? paths, MediaMovePlan? movedPlan) => paths?.Select(path => NormalizeMovedBinding(path ?? string.Empty, movedPlan)).Order(StringComparer.Ordinal).ToArray();

    internal static string NormalizeMovedBinding(string path, MediaMovePlan? movedPlan)
    {
        if (movedPlan is not null && Path.IsPathFullyQualified(path))
        {
            var entry = movedPlan.Entries.FirstOrDefault(entry => LibraryGuard.IsUnder(path, entry.Destination));
            if (entry is not null)
            {
                return Path.GetFullPath(Path.Combine(entry.Source, Path.GetRelativePath(entry.Destination, path)));
            }
        }

        return path;
    }

    private static string ContainerIdentity(BaseItem item) => item switch
    {
        Episode { SeriesId: var series } when series != Guid.Empty => "series:" + series.ToString("N"),
        Audio audio when !string.IsNullOrWhiteSpace(audio.Album) && audio.AlbumArtists.Count > 0 => "album:" + MediaMovePlan.Hash(JsonSerializer.Serialize(
            new { audio.Album, artists = audio.AlbumArtists.Order(StringComparer.Ordinal).ToArray() },
            MediaMovePlan.JsonOptions)),
        _ => "item:" + item.Id.ToString("N")
    };

    [GeneratedRegex(@"[ ._-]+(?:cd|dvd|part|pt|disc|disk)\s*\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultipartSuffix();

    private static Issue ToIssue(MediaMovePlan plan)
    {
        var primary = plan.Entries[0];
        return new Issue
        {
            Type = plan.Type,
            ItemId = plan.ItemId,
            Path = primary.Source,
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow,
            SizeSavings = 0,
            SuggestedFix = plan.BlockingReasons.Length > 0 ? "Move needs review: " + plan.BlockingReasons[0] : "Move complete item to " + primary.Destination,
            DetailsJson = JsonSerializer.Serialize(
                new
                {
                    action = primary.IsDirectory ? "MoveFolder" : "MoveFile", source = primary.Source, target = primary.Destination,
                    approvedSource = primary.Source, targetPath = primary.Destination, movePlan = plan,
                    warnings = plan.BlockingReasons.Select(reason => new IssueWarning { Code = "move-needs-review", Blocking = true, Message = reason }).ToArray()
                },
                MediaMovePlan.JsonOptions)
        };
    }
}

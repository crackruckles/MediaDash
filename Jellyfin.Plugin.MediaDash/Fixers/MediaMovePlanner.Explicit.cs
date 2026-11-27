using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Plans explicit administrator selections using the same complete inventory and safety checks.</summary>
public sealed partial class MediaMovePlanner
{
    internal Task<MediaMovePlan> BuildExplicitAsync(IReadOnlyList<(string Source, string Destination)> selection, IReadOnlyList<BaseItem> peers, PluginConfiguration config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Count is 0 or > 4096)
        {
            throw new IOException("Select between one and 4096 paths for a move preview.");
        }

        var roots = _guard.GetEnabledLibraryLocations(config.EnabledLibraries);
        var blockers = new List<string>();
        var actions = new List<(string Source, string Destination)>();
        var selected = selection.Select(pair => (Source: Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.Source)), Destination: Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.Destination))))
            .Distinct().OrderBy(pair => pair.Source.Length).ThenBy(pair => pair.Source, StringComparer.Ordinal).ToArray();
        var owners = new List<BaseItem>();
        foreach (var pair in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Validate containment before enumerating or reading any selected payload.
            if (!InScope(pair.Source, roots) || !InScope(pair.Destination, roots))
            {
                throw new IOException("Every selected source and destination must be inside enabled library folders. Outside paths were not read.");
            }

            if (roots.Any(root => Paths.Equals(Path.TrimEndingDirectorySeparator(root), pair.Source)))
            {
                throw new IOException("Select content inside a library folder; the library root itself cannot be moved.");
            }

            var ancestor = actions.FirstOrDefault(action => LibraryGuard.IsUnder(pair.Source, action.Source));
            if (ancestor.Source is not null)
            {
                var expected = Path.GetFullPath(Path.Combine(ancestor.Destination, Path.GetRelativePath(ancestor.Source, pair.Source)));
                if (!Paths.Equals(expected, pair.Destination))
                {
                    blockers.Add("A selected parent and child have different destinations. Review them as one complete selection.");
                }

                continue;
            }

            actions.Add(pair);
            if (Directory.Exists(pair.Source))
            {
                continue;
            }

            var owner = peers.FirstOrDefault(item => MediaFileHelper.GetMovePayloadPaths(item).Any(path => Paths.Equals(path, pair.Source)));
            if (owner is not null)
            {
                owners.Add(owner);
                if (owner is not Movie && MediaFormats.All.Contains(Path.GetExtension(pair.Source)))
                {
                    blockers.Add("Select the complete series, album, book or photo folder so this move preserves its shared metadata and structure.");
                    continue;
                }
            }

            if (owner is Movie || MediaFormats.Video.Contains(Path.GetExtension(pair.Source)))
            {
                var declared = owner is null ? [pair.Source] : MediaFileHelper.GetMovePayloadPaths(owner).ToArray();
                if (owner is Video video)
                {
                    declared = declared.Concat(video.AudioFiles).Concat(video.SubtitleFiles).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(Paths).ToArray();
                }

                if (owner is not null && MediaFileHelper.GetMovePayloadPaths(owner).Distinct(Paths).Count() > 1
                    && !Paths.Equals(Path.GetFileName(pair.Source), Path.GetFileName(pair.Destination)))
                {
                    blockers.Add("Rename the complete version or multipart folder, or preserve these filenames while moving the bundle.");
                }

                foreach (var path in declared.Where(path => !Paths.Equals(path, pair.Source)))
                {
                    if (!InScope(path, roots) || !File.Exists(path))
                    {
                        blockers.Add("A declared version, part or external stream is missing or outside enabled libraries. Its contents were not read.");
                        continue;
                    }

                    var name = Path.GetFileName(path);
                    var stem = Path.GetFileNameWithoutExtension(pair.Source);
                    if (!Paths.Equals(Path.GetFileName(pair.Source), Path.GetFileName(pair.Destination))
                        && owner is Video declaredVideo && declaredVideo.AudioFiles.Concat(declaredVideo.SubtitleFiles).Any(stream => Paths.Equals(stream, path)))
                    {
                        if (name.StartsWith(stem + ".", PathComparison) || name.StartsWith(stem + "-", PathComparison))
                        {
                            name = Path.GetFileNameWithoutExtension(pair.Destination) + name[stem.Length..];
                        }
                        else
                        {
                            blockers.Add("This external stream's filename cannot be safely renamed with the selected primary. Select its complete folder instead.");
                        }
                    }

                    actions.Add((Path.GetFullPath(path), Path.Combine(Path.GetDirectoryName(pair.Destination)!, name)));
                }

                var bundle = actions.Where(action => Paths.Equals(Path.GetDirectoryName(action.Destination), Path.GetDirectoryName(pair.Destination))).ToList();
                AddCompanions(bundle, peers, roots, blockers, keepSharedInPlace: bundle.All(action => Paths.Equals(Path.GetDirectoryName(action.Source), Path.GetDirectoryName(action.Destination))));
                foreach (var companion in bundle.Where(action => !actions.Any(existing => Paths.Equals(existing.Source, action.Source))))
                {
                    var name = Path.GetFileName(companion.Source);
                    var stem = Path.GetFileNameWithoutExtension(pair.Source);
                    if (!Paths.Equals(Path.GetFileName(pair.Source), Path.GetFileName(pair.Destination)) && (name.StartsWith(stem + ".", PathComparison) || name.StartsWith(stem + "-", PathComparison)))
                    {
                        name = Path.GetFileNameWithoutExtension(pair.Destination) + name[stem.Length..];
                    }

                    actions.Add((companion.Source, Path.Combine(Path.GetDirectoryName(pair.Destination)!, name)));
                }
            }
        }

        var entries = new List<MediaMovePlan.Entry>();
        foreach (var action in actions.Distinct().OrderBy(action => action.Source, StringComparer.Ordinal))
        {
            Preflight(IssueType.ManualDelete, action.Source, action.Destination, roots, blockers);
            var inventory = MediaMoveSnapshot.Capture(action.Source, cancellationToken).Export();
            entries.Add(new(action.Source, action.Destination, inventory[0].IsDirectory, inventory));
        }

        var array = entries.ToArray();
        var container = MediaMoveManifest.ClaimContainer(array[0].Destination, array[0].IsDirectory);
        while (array.Any(entry => !LibraryGuard.IsUnder(entry.Destination, container)))
        {
            container = Path.GetDirectoryName(container) ?? throw new IOException("The selected destinations do not share one parent folder. Choose a single destination folder.");
        }

        var primary = owners.DistinctBy(owner => owner.Id).ToArray();
        var plan = new MediaMovePlan
        {
            Type = IssueType.ManualMove, ItemId = primary.Length == 1 ? primary[0].Id : Guid.Empty,
            ItemPath = primary.Length == 1 ? primary[0].Path : array[0].Source,
            Identity = primary.Length == 1 ? Identity(primary[0]) : string.Empty,
            ContainerIdentity = "explicit", DestinationContainer = container,
            ItemBindings = CaptureItemBindings(array, peers),
            IndependentFiles = primary.Length == 0 && array.All(entry => entry.IsDirectory || !MediaFormats.All.Contains(Path.GetExtension(entry.Source))),
            DestinationParents = MediaMoveManifest.CaptureDestinationParents(array),
            SourceScopes = CaptureSourceScopes(array, primary.Length == 0 && array.All(entry => entry.IsDirectory || !MediaFormats.All.Contains(Path.GetExtension(entry.Source)))),
            PolicyStamp = PolicyStamp(config, IssueType.ManualMove), Entries = array,
            BlockingReasons = blockers.Distinct(StringComparer.Ordinal).ToArray()
        }.Seal();
        return Task.FromResult(plan);
    }
}

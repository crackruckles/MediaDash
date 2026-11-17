using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Filesystem executor fixtures explicitly approve a complete inventory. Scanner ownership/layout
// behavior is exercised separately through the actual planner in MediaMovePlanningTests.
internal static class MediaMoveTestApproval
{
    internal static Issue Create(MediaDashDb database, PluginConfiguration config, List<BaseItem> items, IssueType type, string path, string target, string? approvedSource = null, string action = "MoveFile")
    {
        var source = approvedSource ?? path;
        var owner = new Movie { Id = Guid.NewGuid(), Name = Path.GetFileNameWithoutExtension(source), Path = source };
        items.Add(owner);
        var actions = new List<(string Source, string Target)> { (source, target) };
        if (File.Exists(source))
        {
            var extensions = new HashSet<string>(Scanners.SubtitleFormats.Extensions, StringComparer.OrdinalIgnoreCase) { ".nfo", ".jpg", ".jpeg", ".png", ".bif" };
            var parent = Path.GetDirectoryName(source)!;
            var primaries = Directory.EnumerateFiles(parent).Where(file => Scanners.MediaFormats.Video.Contains(Path.GetExtension(file))).ToArray();
            var stem = Path.GetFileNameWithoutExtension(source);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var companion in Directory.EnumerateFiles(parent).Where(file => extensions.Contains(Path.GetExtension(file))))
            {
                var name = Path.GetFileName(companion);
                var bestOwner = primaries.Where(file => name.StartsWith(Path.GetFileNameWithoutExtension(file) + ".", comparison)
                    || name.StartsWith(Path.GetFileNameWithoutExtension(file) + "-", comparison)).OrderByDescending(file => Path.GetFileNameWithoutExtension(file).Length).FirstOrDefault();
                if (string.Equals(bestOwner, source, comparison))
                {
                    actions.Add((companion, Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + name[stem.Length..])));
                }
            }
        }

        var entries = actions.Select(pair => new MediaMovePlan.Entry(pair.Source, pair.Target, Directory.Exists(pair.Source), MediaMoveSnapshot.Capture(pair.Source).Export())).ToArray();
        var plan = new MediaMovePlan
        {
            ItemId = owner.Id, ItemPath = source, Type = type, Identity = MediaMovePlanner.Identity(owner),
            PolicyStamp = MediaMovePlanner.PolicyStamp(config, type),
            DestinationContainer = MediaMoveManifest.ClaimContainer(target, Directory.Exists(source)),
            DestinationParents = MediaMoveManifest.CaptureDestinationParents(entries), Entries = entries,
            SourceScopes = MediaMovePlanner.CaptureSourceScopes(entries)
        }.Seal();
        var issue = new Issue
        {
            ItemId = owner.Id, Type = type, Path = path, Status = IssueStatus.Detected,
            DetailsJson = action is "MoveFile" or "MoveFolder" ? JsonSerializer.Serialize(new { action, movePlan = plan }, MediaMovePlan.JsonOptions) : JsonSerializer.Serialize(new { action, source, target })
        };
        database.Issues.ReplaceDetectedIssues(type, [issue]);
        var stored = database.Issues.GetIssues(type).Single(candidate => candidate.ItemId == owner.Id);
        database.Issues.TryTransitionIssueStatus(stored.Id, [IssueStatus.Detected], IssueStatus.Queued);
        return database.Issues.GetIssue(stored.Id)!;
    }
}

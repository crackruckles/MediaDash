using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>Refreshes changed items and notifies Jellyfin about affected folder membership.</summary>
public sealed class FixLibraryRefresher
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryMonitor _monitor;
    private readonly ILogger<FixLibraryRefresher> _logger;
    private readonly NativeLibraryRefresher? _nativeRefresh;

    /// <summary>Initializes a new instance of the <see cref="FixLibraryRefresher"/> class.</summary>
    /// <param name="libraryManager">Item and path resolution.</param>
    /// <param name="providers">Jellyfin's metadata providers.</param>
    /// <param name="fileSystem">Filesystem used by the refresh.</param>
    /// <param name="logger">Refresh failure logger.</param>
    /// <param name="monitor">Jellyfin's folder-change notifications.</param>
    /// <param name="nativeRefresh">Awaited bounded native membership and metadata refresh.</param>
    public FixLibraryRefresher(ILibraryManager libraryManager, IProviderManager providers, IFileSystem fileSystem, ILogger<FixLibraryRefresher> logger, ILibraryMonitor monitor, NativeLibraryRefresher? nativeRefresh = null)
    {
        _libraryManager = libraryManager;
        _providers = providers;
        _fileSystem = fileSystem;
        _logger = logger;
        _monitor = monitor;
        _nativeRefresh = nativeRefresh;
    }

    internal Attempt Capture(Issue issue)
    {
        var targets = new Targets();
        Collect(issue, targets);
        return new Attempt(issue, GetPaths(issue).Distinct().ToDictionary(path => path, Stamp.Read), targets);
    }

    internal void Complete(Attempt attempt, FixResult? result, Targets targets)
    {
        if (result?.WasDryRun == true)
        {
            return;
        }

        if (result?.Outcome == FixOutcome.Applied || result?.Reason == FixReason.RecoveryRequired
            || attempt.Before.Any(pair => pair.Value != Stamp.Read(pair.Key)))
        {
            targets.Items.UnionWith(attempt.Targets.Items);
            targets.Paths.UnionWith(attempt.Targets.Paths);
            Collect(attempt.Issue, targets);
        }
    }

    internal void Collect(Issue issue, Targets targets)
    {
        string[] roots;
        try
        {
            roots = _libraryManager.GetVirtualFolders().SelectMany(folder => folder.Locations ?? []).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve library paths for refresh; known owning items will still refresh.");
            roots = [];
        }

        foreach (var original in GetPaths(issue).Distinct())
        {
            try
            {
                if (!Path.IsPathFullyQualified(original) || !roots.Any(root => LibraryGuard.IsUnder(Path.GetFullPath(original), root)))
                {
                    continue;
                }

                targets.Paths.Add(Path.GetFullPath(original));

                var path = original;
                BaseItem? item = null;
                while (!string.IsNullOrEmpty(path) && roots.Any(root => LibraryGuard.IsUnder(path, root)))
                {
                    item = _libraryManager.FindByPath(path, null);
                    if (item is not null && (File.Exists(item.Path) || Directory.Exists(item.Path)))
                    {
                        break;
                    }

                    item = null;
                    path = Path.GetDirectoryName(path);
                }

                if (item is not null && item.Id != Guid.Empty)
                {
                    targets.Items.Add(item.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve the changed item at {Path}; refresh this path manually in Jellyfin.", original);
            }
        }

        try
        {
            if (issue.ItemId != Guid.Empty && _libraryManager.GetItemById(issue.ItemId) is { } owner
                && (File.Exists(owner.Path) || Directory.Exists(owner.Path)))
            {
                targets.Items.Add(owner.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve changed item {ItemId}.", issue.ItemId);
        }
    }

    internal void Queue(Targets targets) => Queue(targets.Items);

    internal async Task QueueAsync(Targets targets, CancellationToken cancellationToken)
    {
        if (_nativeRefresh is null)
        {
            Queue(targets);
            return;
        }

        if (targets.Paths.Count == 0 && targets.Items.Count == 0)
        {
            return;
        }

        try
        {
            await _nativeRefresh.RefreshAsync(targets.Paths, targets.Items, cancellationToken).ConfigureAwait(false);
        }
        catch (NativeLibraryRefresher.LibraryScanInProgressException)
        {
            _logger.LogInformation("Changed-item indexing is awaiting Jellyfin's running library scan.");
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Changed items still need native indexing; run Jellyfin's library scan to complete it.");
            Api.Diagnostics.Record("Fix.NativeIndex", "Changed items still need native indexing. Run Jellyfin's library scan to complete it: " + error.Message);
        }
    }

    internal void Queue(IEnumerable<Guid> targets)
    {
        var notified = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var id in targets.Distinct().Where(id => id != Guid.Empty))
        {
            try
            {
                if (_libraryManager.GetItemById(id) is not { } item)
                {
                    continue;
                }

                if (item.IsFolder || !File.Exists(item.Path))
                {
                    var path = item.IsFolder ? item.Path : Path.GetDirectoryName(item.Path);
                    if (!string.IsNullOrEmpty(path) && notified.Add(path))
                    {
                        try
                        {
                            _monitor.ReportFileSystemChanged(path);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Could not notify Jellyfin about changed folder {Path}.", path);
                        }
                    }
                }

                // Only queue individual files: folder QueueRefresh recursively walks their children.
                if (!item.IsFolder && File.Exists(item.Path))
                {
                    _providers.QueueRefresh(
                        item.Id,
                        new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                        {
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                            ImageRefreshMode = MetadataRefreshMode.Default,
                            ForceSave = true
                        },
                        RefreshPriority.High);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not refresh changed item {ItemId}; refresh this item manually in Jellyfin.", id);
            }
        }
    }

    private List<string> GetPaths(Issue issue)
    {
        var paths = new List<string> { issue.Path };
        if (MediaMovePlan.ReadIssue(issue) is { } plan)
        {
            paths.AddRange(plan.Entries.SelectMany(entry => new[] { entry.Source, entry.Destination }));
        }

        if (issue.Type is IssueType.Misplaced or IssueType.Ungrouped && !string.IsNullOrWhiteSpace(issue.DetailsJson))
        {
            try
            {
                using var details = JsonDocument.Parse(issue.DetailsJson);
                if (details.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "source", "target", "targetPath" })
                    {
                        if (details.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            paths.Add(value.GetString()!);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // The source still needs a refresh when move details are malformed.
            }
        }

        try
        {
            if (issue.ItemId != Guid.Empty && _libraryManager.GetItemById(issue.ItemId) is { } item && !string.IsNullOrWhiteSpace(item.Path))
            {
                paths.Add(item.Path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve changed item {ItemId}; checking the fix paths instead.", issue.ItemId);
        }

        return paths;
    }

    internal sealed class Targets
    {
        internal HashSet<Guid> Items { get; } = [];

        internal HashSet<string> Paths { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    internal sealed record Attempt(Issue Issue, IReadOnlyDictionary<string, Stamp> Before, Targets Targets);

    internal sealed record Stamp(bool Exists, bool Directory, long Length, DateTime Modified, PhysicalFileIdentity.Value Identity)
    {
        internal static Stamp Read(string path)
        {
            try
            {
                var directory = System.IO.Directory.Exists(path);
                var exists = directory || File.Exists(path);
                PhysicalFileIdentity.TryRead(path, out var identity);
                return new Stamp(
                    exists,
                    directory,
                    !directory && exists ? new FileInfo(path).Length : 0,
                    directory ? System.IO.Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path),
                    identity);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return new Stamp(false, false, 0, default, default);
            }
        }
    }
}

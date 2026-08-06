using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Scanners;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// REST endpoints backing the MediaDash dashboard page.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("MediaDash")]
[Produces("application/json")]
public class MediaDashController : ControllerBase
{
    private readonly MediaDashDb _db;
    private readonly ITaskManager _taskManager;
    private readonly RecycleBin _recycleBin;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;
    private readonly IEnumerable<ISubtitleProvider> _subtitleProviders;
    private readonly IEnumerable<IScanner> _scanners;
    private readonly PostUpgradeCleanup _postUpgradeCleanup;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDashController"/> class.
    /// </summary>
    /// <param name="db">The plugin database.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="appHost">Server application host, used for Jellyfin version in diagnostics.</param>
    /// <param name="subtitleProviders">Registered subtitle providers, used to warn when none are configured.</param>
    /// <param name="scanners">Registered scanners, used by targeted-scan endpoints like the Maintenance virus scan.</param>
    /// <param name="postUpgradeCleanup">The one-shot post-Jellyfin-12 upgrade cleaner.</param>
    public MediaDashController(
        MediaDashDb db,
        ITaskManager taskManager,
        RecycleBin recycleBin,
        ILibraryMonitor libraryMonitor,
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        IEnumerable<ISubtitleProvider> subtitleProviders,
        IEnumerable<IScanner> scanners,
        PostUpgradeCleanup postUpgradeCleanup)
    {
        _db = db;
        _taskManager = taskManager;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _libraryManager = libraryManager;
        _appHost = appHost;
        _subtitleProviders = subtitleProviders;
        _scanners = scanners;
        _postUpgradeCleanup = postUpgradeCleanup;
    }

    /// <summary>
    /// Gets the dashboard status: issue counts, potential savings and scan state.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusResponse> GetStatus()
    {
        var summary = _db.GetSummary();
        var scanTask = GetScanTask();
        var fixTask = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is FixTask);
        long freeDisk = 0, totalDisk = 0;
        var drives = new List<DriveUsage>();

        // Roots that host a library folder — used to mark library drives and to keep the aggregated
        // FreeDiskBytes/TotalDiskBytes fields scoped to what MediaDash actually cares about.
        var libraryRoots = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Select(l => System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(l)))
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => System.IO.Path.TrimEndingDirectorySeparator(r!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recycleDrive = Fixers.RecycleBin.FindDriveForPath(_recycleBin.GetEffectiveRoot());
        var recycleRoot = recycleDrive is null ? null : System.IO.Path.TrimEndingDirectorySeparator(recycleDrive.RootDirectory.FullName);

        foreach (var drive in System.IO.DriveInfo.GetDrives())
        {
            try
            {
                // Fixed only — skip CDs, network shares (which may not be ready or may be huge remote mounts we
                // don't want to poll), and RAM disks. IsReady guards against USB drives that are attached but
                // not yet mounted.
                if (drive.DriveType != System.IO.DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                var trimmedName = System.IO.Path.TrimEndingDirectorySeparator(drive.Name);
                var isLibraryDrive = libraryRoots.Contains(trimmedName);
                var isRecycleDrive = recycleRoot is not null && string.Equals(trimmedName, recycleRoot, StringComparison.OrdinalIgnoreCase);
                if (isLibraryDrive)
                {
                    freeDisk += drive.AvailableFreeSpace;
                    totalDisk += drive.TotalSize;
                }

                drives.Add(new DriveUsage
                {
                    Root = drive.Name,
                    FreeBytes = drive.AvailableFreeSpace,
                    TotalBytes = drive.TotalSize,
                    IsLibraryDrive = isLibraryDrive,
                    IsRecycleBinDrive = isRecycleDrive
                });
            }
            catch (IOException ex)
            {
                Diagnostics.Record("SystemStats.DriveStat", "Could not read drive '" + drive.Name + "' for the Overview: " + ex.Message + ". Its free-space total will be missing until the next scan.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Diagnostics.Record("SystemStats.DriveStat", "Access denied reading drive '" + drive.Name + "' for the Overview: " + ex.Message + ". Grant Jellyfin's user read permission on the drive root to include it.");
            }
        }

        // Docker overlay / btrfs subvolume / etc. often report as DriveType.Unknown and get filtered out
        // above. If the recycle bin lives on one of those, its drive wouldn't appear on the Overview and
        // the user would run out of space with no warning. Force-add it here.
        if (recycleDrive is not null && !drives.Any(d =>
            string.Equals(System.IO.Path.TrimEndingDirectorySeparator(d.Root), recycleRoot, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                drives.Add(new DriveUsage
                {
                    Root = recycleDrive.Name,
                    FreeBytes = recycleDrive.AvailableFreeSpace,
                    TotalBytes = recycleDrive.TotalSize,
                    IsLibraryDrive = false,
                    IsRecycleBinDrive = true
                });
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var config = Plugin.Instance!.Configuration;
        var queuedCount = _db.GetIssues(status: IssueStatus.Queued).Count;
        var autoQueueableCount = _db.GetIssues(status: IssueStatus.Detected)
            .Count(i => config.GetFixMode(i.Type) == Configuration.FixMode.Automatic);

        return new StatusResponse
        {
            IsScanning = scanTask is not null && scanTask.State != TaskState.Idle,
            ScanProgress = scanTask?.CurrentProgress,
            IsFixing = fixTask is not null && fixTask.State != TaskState.Idle,
            FixProgress = fixTask?.CurrentProgress,
            OpenIssueTotal = summary.Sum(s => s.Count),
            FreeDiskBytes = freeDisk,
            TotalDiskBytes = totalDisk,
            LastScanUtc = summary.Count > 0 ? summary.Max(s => s.NewestDetectedUtc) : null,
            TotalPotentialSavings = summary.Sum(s => s.PotentialSavings),
            LifetimeBytesReclaimed = _db.GetLifetimeBytesFreed(),
            LifetimeCounts = _db.GetLifetimeSummary().Select(s => new TypeCount
            {
                Type = s.Type.ToString(),
                Count = s.Count,
                PotentialSavings = s.PotentialSavings
            }).ToList(),
            Counts = summary.Select(s => new TypeCount
            {
                Type = s.Type.ToString(),
                Count = s.Count,
                PotentialSavings = s.PotentialSavings
            }).ToList(),
            PendingFixCount = queuedCount + autoQueueableCount,
            Drives = drives,
            CurrentActivity = Plugin.CurrentActivity,
            System = SystemStats.Sample(),
            RecycleBinPath = _recycleBin.GetEffectiveRoot(),
            RecycleBinCrossVolume = ComputeRecycleBinCrossVolume(drives),
            LastFixRun = Plugin.LastFixRun,
            FixPauseReason = FixTask.PauseReason,
            RedownloadWarnings = Plugin.RedownloadWarnings
        };
    }

    private bool ComputeRecycleBinCrossVolume(List<DriveUsage> drives)
    {
        // Only warn when there ARE library drives (i.e., the user has configured libraries) and the recycle
        // bin sits on a different volume. On single-drive setups the answer is trivially "no" — do nothing.
        var libraryDrives = drives.Where(d => d.IsLibraryDrive).ToList();
        if (libraryDrives.Count == 0)
        {
            return false;
        }

        var recycleDrive = Fixers.RecycleBin.FindDriveForPath(_recycleBin.GetEffectiveRoot());
        if (recycleDrive is null)
        {
            return false;
        }

        var recycleRoot = System.IO.Path.TrimEndingDirectorySeparator(recycleDrive.RootDirectory.FullName);
        return !libraryDrives.Any(d => string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(d.Root),
            recycleRoot,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets issues, optionally filtered by type and status.
    /// </summary>
    /// <param name="type">Filter by issue type.</param>
    /// <param name="status">Filter by status; defaults to detected.</param>
    /// <param name="openOnly">When true, returns Detected + Queued combined (open work) — overrides <paramref name="status"/>.</param>
    /// <returns>The matching issues.</returns>
    [HttpGet("Issues")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<IssueDto>> GetIssues(
        [FromQuery] IssueType? type = null,
        [FromQuery] IssueStatus? status = IssueStatus.Detected,
        [FromQuery] bool openOnly = false)
    {
        if (openOnly)
        {
            var combined = _db.GetIssues(type, IssueStatus.Detected)
                .Concat(_db.GetIssues(type, IssueStatus.Queued))
                .Select(IssueDto.FromIssue)
                .ToList();
            return Ok(combined);
        }

        return Ok(_db.GetIssues(type, status).Select(IssueDto.FromIssue).ToList());
    }

    /// <summary>
    /// Starts a scan now.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Scan")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult StartScan()
    {
        var scanTask = GetScanTask();
        if (scanTask is not null && scanTask.State == TaskState.Idle)
        {
            ScanTask.BypassIdleCheckOnce = true;
            _taskManager.Execute(scanTask, new TaskOptions());
        }

        return NoContent();
    }

    /// <summary>
    /// Runs just the <see cref="SuspiciousFileScanner"/> — the "virus scan" surfaced in Maintenance.
    /// Skips the other nine scanners so a user who suspects a pirated release dropped an .exe next to
    /// a movie can get a fast, targeted answer without waiting through a full library scan. Runs
    /// inline (not via ITaskManager) because the scanner is filesystem-walking-only and finishes in
    /// seconds even on large libraries.
    /// </summary>
    /// <returns>Count of MalwareRisk issues detected, and the elapsed milliseconds.</returns>
    [HttpPost("Scan/Suspicious")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> ScanSuspicious()
    {
        var scanner = _scanners.OfType<SuspiciousFileScanner>().FirstOrDefault();
        if (scanner is null)
        {
            return NotFound(new { Error = "SuspiciousFileScanner is not registered." });
        }

        var start = DateTime.UtcNow;
        var progress = new Progress<double>();
        var issues = await scanner.RunScanAsync(progress, HttpContext.RequestAborted).ConfigureAwait(false);
        _db.ReplaceDetectedIssues(scanner.Type, issues, null);
        return Ok(new
        {
            Detected = issues.Count,
            ElapsedMs = (long)(DateTime.UtcNow - start).TotalMilliseconds,
        });
    }

    /// <summary>
    /// Approves an issue: it is queued for the next fix run.
    /// </summary>
    /// <param name="id">The issue id.</param>
    /// <returns>No content, or 404 when the issue does not exist.</returns>
    [HttpPost("Issues/{id}/Approve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult ApproveIssue([FromRoute] long id)
    {
        return _db.UpdateIssueStatus(id, IssueStatus.Queued) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Dismisses an issue: it will not be re-reported by future scans.
    /// </summary>
    /// <param name="id">The issue id.</param>
    /// <returns>No content, or 404 when the issue does not exist.</returns>
    [HttpPost("Issues/{id}/Dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DismissIssue([FromRoute] long id)
    {
        return _db.UpdateIssueStatus(id, IssueStatus.Dismissed) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Starts a fix run now.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Fix")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult StartFix()
    {
        var fixTask = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is FixTask);
        if (fixTask is not null && fixTask.State == TaskState.Idle)
        {
            FixTask.BypassIdleCheckOnce = true;
            _taskManager.Execute(fixTask, new TaskOptions());
        }

        return NoContent();
    }

    /// <summary>
    /// Resets the fix task's trigger to MediaDash's current default (an interval trigger — see
    /// <see cref="ScheduledTasks.FixTask.FixInterval"/>). Kept for backward-compat with settings pages that
    /// still call it after Save. Also useful for one-shot migration from the old daily-at-time trigger:
    /// hitting this endpoint clears any lingering user-customized trigger and restores the opportunistic default.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Schedule/Apply")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ApplySchedule()
    {
        var fixTask = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is FixTask);
        if (fixTask is not null)
        {
            fixTask.Triggers =
            [
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = FixTask.FixInterval.Ticks
                }
            ];
        }

        return NoContent();
    }

    /// <summary>
    /// Cancels the running scan, if any.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Scan/Cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult CancelScan()
    {
        var scanTask = GetScanTask();
        if (scanTask is not null && scanTask.State != TaskState.Idle)
        {
            _taskManager.Cancel(scanTask);
        }

        return NoContent();
    }

    /// <summary>
    /// Cancels the running fix, if any. Fix work that has already completed on individual files remains done —
    /// only the remaining queue is skipped.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Fix/Cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult CancelFix()
    {
        var fixTask = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is FixTask);
        if (fixTask is not null && fixTask.State != TaskState.Idle)
        {
            _taskManager.Cancel(fixTask);
        }

        return NoContent();
    }

    /// <summary>
    /// Flips the "ignore viewer activity" flag for the currently-running manual fix run so a paused run
    /// resumes even if someone is still watching. The flag resets when the next fix run starts.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Fix/IgnoreActivity")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult IgnoreFixActivity()
    {
        FixTask.IgnoreActivityForCurrentRun = true;
        return NoContent();
    }

    /// <summary>
    /// Approves all open issues of a type at once.
    /// </summary>
    /// <param name="type">The issue type to approve.</param>
    /// <returns>The number of issues queued.</returns>
    [HttpPost("Issues/ApproveAll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<int> ApproveAll([FromQuery] IssueType? type = null)
    {
        if (type is not null)
        {
            return _db.QueueDetectedIssues(type.Value);
        }

        var total = 0;
        foreach (var t in Enum.GetValues<IssueType>())
        {
            total += _db.QueueDetectedIssues(t);
        }

        return total;
    }

    /// <summary>
    /// Gets the fix history, newest first.
    /// </summary>
    /// <returns>The history entries.</returns>
    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<HistoryDto>> GetHistory()
    {
        // Cache library locations once per request so per-row lookup is a linear scan over a small
        // in-memory list, not a fresh call into ILibraryManager for every history row.
        var libraryLocations = _libraryManager.GetVirtualFolders()
            .SelectMany(f => (f.Locations ?? []).Select(l => (Name: f.Name, Root: Path.TrimEndingDirectorySeparator(Path.GetFullPath(l)))))
            .ToList();

        return Ok(_db.GetHistory().Select(entry =>
        {
            var dto = HistoryDto.FromEntry(entry);
            dto.Library = ResolveLibraryName(entry.Path, libraryLocations);
            return dto;
        }).ToList());
    }

    private static string ResolveLibraryName(string path, List<(string Name, string Root)> libraries)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }

        foreach (var (name, root) in libraries)
        {
            if (LibraryGuard.IsUnder(fullPath, root))
            {
                return name;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Advances the history-hidden watermark so every existing row disappears from the History tab list.
    /// The rows themselves stay in the DB, so per-library chart, "Reclaimed since install", and monthly
    /// analytics all keep their totals - only the visible list gets a fresh slate.
    /// </summary>
    /// <returns>No content on success.</returns>
    [HttpPost("History/Clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearHistoryList()
    {
        var config = Plugin.Instance!.Configuration;
        config.HistoryHiddenBeforeUtcTicks = DateTime.UtcNow.Ticks;
        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    /// <summary>
    /// Restores a recycled file to its original location.
    /// </summary>
    /// <param name="id">The history entry id.</param>
    /// <returns>No content on success; 404 when unknown; 409 when the file cannot be restored.</returns>
    [HttpPost("History/{id}/Restore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult RestoreFromHistory([FromRoute] long id)
    {
        var entry = _db.GetHistory().FirstOrDefault(h => h.Id == id);
        if (entry is null)
        {
            return NotFound();
        }

        if (entry.Restored || string.IsNullOrEmpty(entry.RecyclePath) || !System.IO.File.Exists(entry.RecyclePath))
        {
            return Conflict("This file is no longer in the recycle bin.");
        }

        try
        {
            _recycleBin.Restore(entry.RecyclePath, entry.Path);
        }
        catch (IOException ex)
        {
            return Conflict(ex.Message);
        }

        _db.MarkRestored(id);
        _libraryMonitor.ReportFileSystemChanged(entry.Path);
        return NoContent();
    }

    /// <summary>
    /// Gets the recycle bin contents summary.
    /// </summary>
    /// <returns>File count and total size.</returns>
    /// <summary>
    /// Probes each configured library folder for read+write access so first-run setup can flag
    /// ownership/ACL problems before the user approves anything.
    /// </summary>
    /// <returns>One entry per library location.</returns>
    [HttpGet("LibraryAccessCheck")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LibraryAccessResult>> CheckLibraryAccess()
    {
        var results = new List<LibraryAccessResult>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            foreach (var location in folder.Locations ?? [])
            {
                var entry = new LibraryAccessResult { Name = folder.Name, Path = location };
                try
                {
                    _ = System.IO.Directory.EnumerateFileSystemEntries(location).GetEnumerator().MoveNext();
                    entry.CanRead = true;
                }
                catch (System.IO.DirectoryNotFoundException)
                {
                    entry.Error = "Folder does not exist: " + location;
                }
                catch (UnauthorizedAccessException)
                {
                    entry.Error = "Jellyfin can't read '" + location + "'. Grant the Jellyfin user read access.";
                }
                catch (IOException ex)
                {
                    entry.Error = "Cannot read '" + location + "': " + ex.Message;
                }

                if (entry.CanRead)
                {
                    var probe = System.IO.Path.Combine(location, ".mediadash-access-probe-" + System.Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
                    try
                    {
                        System.IO.File.WriteAllBytes(probe, []);
                        System.IO.File.Delete(probe);
                        entry.CanWrite = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        entry.Error = "Jellyfin can't write to '" + location + "'. On Linux: sudo chown -R jellyfin:jellyfin '" + location + "' (and chmod g+rwx if using a shared group).";
                    }
                    catch (IOException ex)
                    {
                        entry.Error = "Cannot write to '" + location + "': " + ex.Message;
                    }
                }

                results.Add(entry);
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// Returns the distinct genres present on any BaseItem in the user's libraries. Sorted
    /// case-insensitively. Powers the Stale scanner's "Skip these genres" datalist — replaces the
    /// old freeform CSV text input with a picker that only offers genres the user's library
    /// actually has, so no more "Christmis" typos silently skipping nothing.
    /// </summary>
    /// <returns>Distinct genre names.</returns>
    [HttpGet("Genres")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> GetGenres()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            IncludeItemTypes =
            [
                Jellyfin.Data.Enums.BaseItemKind.Movie,
                Jellyfin.Data.Enums.BaseItemKind.Series,
                Jellyfin.Data.Enums.BaseItemKind.Episode,
                Jellyfin.Data.Enums.BaseItemKind.Book,
                Jellyfin.Data.Enums.BaseItemKind.MusicAlbum,
                Jellyfin.Data.Enums.BaseItemKind.Audio,
                Jellyfin.Data.Enums.BaseItemKind.AudioBook,
            ],
            IsVirtualItem = false,
            Recursive = true,
        });
        foreach (var item in items)
        {
            if (item.Genres is { Length: > 0 } genres)
            {
                foreach (var g in genres)
                {
                    if (!string.IsNullOrWhiteSpace(g))
                    {
                        seen.Add(g.Trim());
                    }
                }
            }
        }

        return Ok(seen.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Lists the server's virtual libraries with a stable identity, replacing Jellyfin's native
    /// <c>/Library/VirtualFolders</c> for MediaDash's frontend. Jellyfin 12 dropped the JSON
    /// <c>ItemId</c> field from that response, so the config page's library checkboxes rendered
    /// with no id and saved an empty list. This endpoint routes the id through
    /// <see cref="Scanners.VirtualFolderIdentity"/> so it stays populated on both v10 and v12.
    /// </summary>
    /// <returns>One entry per virtual folder.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LibraryInfo>> GetLibraries()
    {
        var idLookup = Scanners.VirtualFolderIdentity.BuildIdLookup(_libraryManager);
        var libraries = _libraryManager.GetVirtualFolders()
            .Select(f => new LibraryInfo
            {
                ItemId = Scanners.VirtualFolderIdentity.GetId(f, idLookup) ?? string.Empty,
                Name = f.Name ?? string.Empty,
                CollectionType = f.CollectionType?.ToString()?.ToLowerInvariant(),
                Locations = f.Locations ?? [],
            })
            .Where(l => !string.IsNullOrEmpty(l.ItemId))
            .ToList();
        return Ok(libraries);
    }

    /// <summary>Gets the recycle bin contents summary including any in-flight empty progress.</summary>
    /// <returns>File count, size, and empty-run progress fields.</returns>
    [HttpGet("RecycleBin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RecycleBinInfo> GetRecycleBin()
    {
        var (count, size) = _recycleBin.GetContents();
        var (running, done, total, error) = _recycleBin.GetEmptyingProgress();
        return new RecycleBinInfo
        {
            FileCount = count,
            SizeBytes = size,
            IsEmptying = running,
            EmptyingDone = done,
            EmptyingTotal = total,
            EmptyingError = error
        };
    }

    /// <summary>
    /// Lists the files currently held in the recycle bin, newest first.
    /// </summary>
    /// <returns>The recycled files.</returns>
    [HttpGet("RecycleBin/Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<RecycleBinItem>> GetRecycleBinItems()
    {
        // Join each bin file back to its history row so the UI can show a per-item Restore button.
        // History is authoritative for the original destination path; the bin itself doesn't store it.
        var byRecyclePath = _db.GetHistory()
            .Where(h => !h.Restored && !string.IsNullOrEmpty(h.RecyclePath))
            .GroupBy(h => h.RecyclePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.FixedAtUtc).First(), StringComparer.OrdinalIgnoreCase);

        return Ok(_recycleBin.ListContents()
            .Select(e =>
            {
                var item = new RecycleBinItem { FileName = e.FileName, SizeBytes = e.SizeBytes, RecycledAtUtc = e.RecycledAtUtc };
                if (byRecyclePath.TryGetValue(e.BinPath, out var h))
                {
                    item.HistoryId = h.Id;
                    item.OriginalPath = h.Path;
                }

                return item;
            })
            .ToList());
    }

    /// <summary>
    /// Kicks off a background empty of the recycle bin. Returns immediately with the current progress so the
    /// UI can poll <c>RecycleBin</c> for a bar; older builds ran this synchronously and appeared frozen for
    /// large bins.
    /// </summary>
    /// <returns>The recycle bin state with <c>IsEmptying=true</c> when the run started.</returns>
    [HttpPost("RecycleBin/Empty")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RecycleBinInfo> EmptyRecycleBin()
    {
        var alreadyRunning = _recycleBin.GetEmptyingProgress().IsRunning;
        if (!alreadyRunning)
        {
            _ = System.Threading.Tasks.Task.Run(() => _recycleBin.EmptyAll());
        }

        var (count, size) = _recycleBin.GetContents();
        var (running, done, total, error) = _recycleBin.GetEmptyingProgress();
        return new RecycleBinInfo
        {
            FileCount = count,
            SizeBytes = size,
            IsEmptying = running,
            EmptyingDone = done,
            EmptyingTotal = total,
            EmptyingError = error
        };
    }

    /// <summary>
    /// Wipes all scan state (issues, probe cache, decode cache) so the next scan starts fresh.
    /// Refuses while a scan or fix is running to avoid corrupting in-flight state.
    /// Fix history and the recycle bin are preserved.
    /// </summary>
    /// <returns>No content on success, or 409 while a task is running.</returns>
    [HttpPost("Reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult ResetScanState()
    {
        var scanTask = GetScanTask();
        var fixTask = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is FixTask);
        if ((scanTask is not null && scanTask.State != TaskState.Idle)
            || (fixTask is not null && fixTask.State != TaskState.Idle))
        {
            return Conflict("Cannot reset while a scan or fix is running.");
        }

        _db.ResetScanState();
        return NoContent();
    }

    /// <summary>
    /// Returns whether the one-shot post-Jellyfin-12 upgrade cleanup should be offered to the user.
    /// True only when the host is on Jellyfin 12+ and the user has not yet run or dismissed it.
    /// </summary>
    /// <returns>Availability + host version metadata.</returns>
    [HttpGet("PostUpgradeCleanup/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetPostUpgradeCleanupStatus()
    {
        var v = _appHost.ApplicationVersion;
        var configFlag = Plugin.Instance!.Configuration.PostV12CleanupCompleted;
        var available = v.Major >= 12 && !configFlag;
        return Ok(new
        {
            Available = available,
            Completed = configFlag,
            JellyfinMajor = v.Major
        });
    }

    /// <summary>
    /// Executes the one-shot post-Jellyfin-12 cleanup: filesystem sweep of the trickplay data directory
    /// removing subfolders whose GUID no longer resolves to any BaseItem. Writes a History row summarising
    /// what was reclaimed and permanently sets the "already run" config flag so the offer never appears again.
    /// </summary>
    /// <param name="dismissOnly">When true, marks the cleanup as dismissed WITHOUT running the sweep. Same
    /// once-only guarantee — user gets no more banners about it — but no filesystem work happens.</param>
    /// <returns>The sweep result, or an empty result on dismiss-only.</returns>
    [HttpPost("PostUpgradeCleanup/Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> RunPostUpgradeCleanup([FromQuery] bool dismissOnly = false)
    {
        var config = Plugin.Instance!.Configuration;
        if (dismissOnly)
        {
            config.PostV12CleanupCompleted = true;
            Plugin.Instance!.SaveConfiguration();
            return Ok(new { OrphanedFoldersDeleted = 0, BytesFreed = 0L, Errors = Array.Empty<string>(), Dismissed = true });
        }

        var result = await _postUpgradeCleanup.RunAsync().ConfigureAwait(false);
        _db.AddHistory(new Data.HistoryEntry
        {
            Type = Data.IssueType.Stale,
            Path = "(post-Jellyfin-12 cleanup)",
            Action = $"Removed {result.OrphanedFoldersDeleted} orphaned trickplay folder(s) reclaiming {result.BytesFreed} bytes.",
            FixedAtUtc = DateTime.UtcNow,
            BytesFreed = result.BytesFreed,
            Success = true
        });
        config.PostV12CleanupCompleted = true;
        Plugin.Instance!.SaveConfiguration();
        return Ok(new { OrphanedFoldersDeleted = result.OrphanedFoldersDeleted, BytesFreed = result.BytesFreed, Errors = result.Errors, Dismissed = false });
    }

    /// <summary>
    /// Gets recently-recorded plugin errors (system-stats sample failures, scanner/fixer exceptions).
    /// Bounded to the last ~100 entries in memory; not persisted across Jellyfin restarts.
    /// </summary>
    /// <returns>The entries, newest first.</returns>
    [HttpGet("Errors")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DiagnosticEntry>> GetErrors()
    {
        return Ok(Diagnostics.Recent());
    }

    /// <summary>
    /// Empties the diagnostic buffer.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Errors/Clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearErrors()
    {
        Diagnostics.Clear();
        return NoContent();
    }

    /// <summary>
    /// Gets environment info used by the Errors tab's "Copy diagnostics" button and by the wizard's
    /// subtitle step to warn when no provider is installed.
    /// </summary>
    /// <returns>The env snapshot.</returns>
    [HttpGet("Environment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<EnvInfo> GetEnvironment()
    {
        return Ok(new EnvInfo
        {
            PluginVersion = Plugin.Instance?.Version?.ToString() ?? "unknown",
            JellyfinVersion = _appHost.ApplicationVersionString ?? string.Empty,
            Os = RuntimeInformation.OSDescription,
            Framework = RuntimeInformation.FrameworkDescription,
            SubtitleProviders = _subtitleProviders.Select(p => p.Name).ToList()
        });
    }

    /// <summary>
    /// Gets the plugin logo. Anonymous so image tags can load it without a token header.
    /// </summary>
    /// <returns>The logo PNG.</returns>
    [HttpGet("Logo")]
    [AllowAnonymous]
    [Produces("image/png")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLogo()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.MediaDash.Configuration.logo.png");
        return stream is null ? NotFound() : File(stream, "image/png");
    }

    /// <summary>
    /// Serves the UI translation dictionary for a locale. Falls back down BCP-47 tags
    /// ("de-AT" → "de") and finally to English so an unknown locale never breaks the page.
    /// </summary>
    /// <param name="locale">The requested locale tag (e.g. "de-AT", "es").</param>
    /// <returns>The dictionary JSON.</returns>
    [HttpGet("I18n/{locale}")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetI18n(string locale)
    {
        var stream = I18n.I18nCatalog.OpenBestMatch(locale);
        return File(stream, "application/json");
    }

    private IScheduledTaskWorker? GetScanTask()
    {
        return _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is ScanTask);
    }
}

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
    private readonly LibraryGuard _libraryGuard;

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
    /// <param name="libraryGuard">The library boundary guard; used to refuse restores whose stored path escaped the current library set.</param>
    public MediaDashController(
        MediaDashDb db,
        ITaskManager taskManager,
        RecycleBin recycleBin,
        ILibraryMonitor libraryMonitor,
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        IEnumerable<ISubtitleProvider> subtitleProviders,
        IEnumerable<IScanner> scanners,
        PostUpgradeCleanup postUpgradeCleanup,
        LibraryGuard libraryGuard)
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
        _libraryGuard = libraryGuard;
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
        // FindDriveForPath is the right resolver on both Windows AND Linux — Path.GetPathRoot
        // returns "/" for every Linux path, which would collapse every library folder to root
        // and never match any mount point (leaving all drives unmarked as library drives on Linux,
        // which suppressed the SMART probe for every library drive there).
        var libraryRoots = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Select(l => Fixers.RecycleBin.FindDriveForPath(l))
            .Where(d => d is not null)
            .Select(d => System.IO.Path.TrimEndingDirectorySeparator(d!.RootDirectory.FullName))
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

                var usage = new DriveUsage
                {
                    Root = drive.Name,
                    FreeBytes = drive.AvailableFreeSpace,
                    TotalBytes = drive.TotalSize,
                    IsLibraryDrive = isLibraryDrive,
                    IsRecycleBinDrive = isRecycleDrive
                };
                if (isLibraryDrive || isRecycleDrive)
                {
                    CopySmartFields(usage, Probing.SmartHealthProbe.Get(drive.Name));
                }

                drives.Add(usage);
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
                var recycleUsage = new DriveUsage
                {
                    Root = recycleDrive.Name,
                    FreeBytes = recycleDrive.AvailableFreeSpace,
                    TotalBytes = recycleDrive.TotalSize,
                    IsLibraryDrive = false,
                    IsRecycleBinDrive = true
                };
                CopySmartFields(recycleUsage, Probing.SmartHealthProbe.Get(recycleDrive.Name));
                drives.Add(recycleUsage);
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
            FailedHistoryTotal = _db.GetFailedHistoryCount(config.HistoryHiddenBeforeUtcTicks),
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
            CurrentActivityLabel = Plugin.CurrentActivityLabel,
            DataDirectory = Data.MediaDashDb.DataDirectory,
            // When the user has hidden the System Performance card, skip sampling entirely — no WMI
            // perf counter, no /proc read, no PDH GPU walk, no nvidia-smi spawn. Empty payload lets
            // the client render its placeholder chip without a special-case. Doubled-up polling was
            // the field report: users running Task Manager / btop / Grafana alongside MediaDash
            // didn't want the plugin re-sampling the same counters every 3 seconds.
            System = Plugin.Instance!.Configuration.ShowSystemPerformance
                ? SystemStats.Sample()
                : new SystemStats { SystemStatsAvailable = false },
            RecycleBinPath = _recycleBin.GetEffectiveRoot(),
            RecycleBinCrossVolume = ComputeRecycleBinCrossVolume(drives),
            LastFixRun = Plugin.LastFixRun,
            FixPauseReason = FixTask.PauseReason,
            RedownloadWarnings = Plugin.RedownloadWarnings
        };
    }

    // Flatten a Probing.SmartHealthResult onto a DriveUsage. Keeping the DTO flat (as opposed to nesting a
    // "smart" object) means older clients ignore new fields cleanly and the Overview JS reads one row.
    private static void CopySmartFields(DriveUsage usage, Probing.SmartHealthResult health)
    {
        usage.SmartHealth = health.Status.ToString().ToLowerInvariant();
        usage.SmartMessage = health.Message;
        usage.SmartModel = health.ModelName;
        usage.SmartTemperatureCelsius = health.TemperatureCelsius;
        usage.SmartTemperatureMaxCelsius = health.TemperatureMaxCelsius;
        usage.SmartWearPercent = health.WearPercent;
        usage.SmartPowerOnHours = health.PowerOnHours;
        usage.SmartReadErrorsUncorrected = health.ReadErrorsUncorrected;
        usage.SmartWriteErrorsUncorrected = health.WriteErrorsUncorrected;
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

        // Keep the seeded flag on so ScheduleMigrator still won't fight the user next boot; this
        // endpoint is the sanctioned way to bring the trigger back once it's been deleted.
        var config = Plugin.Instance?.Configuration;
        if (config is not null && !config.FixTaskSeeded)
        {
            config.FixTaskSeeded = true;
            Plugin.Instance?.SaveConfiguration();
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
    /// Bulk-updates a set of issues by id. Used by the Issues tab's filtered "approve all shown"
    /// and the context-menu "ignore all in this folder / of this type / etc." actions — the client
    /// computes the id list, the server just applies the status.
    /// </summary>
    /// <param name="request">Ids and target action ("Approve" or "Dismiss").</param>
    /// <returns>The number of issues updated.</returns>
    [HttpPost("Issues/Bulk")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<int> BulkUpdateIssues([FromBody] BulkIssueRequest request)
    {
        if (request?.Ids is null || request.Ids.Count == 0)
        {
            return BadRequest("Ids required.");
        }

        // Sanity cap on the request array so an absurd payload can't allocate GB of list memory before
        // the DB layer chunks it. 50k covers every plausible real-world "approve all shown" use case.
        if (request.Ids.Count > 50_000)
        {
            return BadRequest("Too many IDs (max 50000 per request).");
        }

        IssueStatus target;
        if (string.Equals(request.Action, "Approve", StringComparison.OrdinalIgnoreCase))
        {
            target = IssueStatus.Queued;
        }
        else if (string.Equals(request.Action, "Dismiss", StringComparison.OrdinalIgnoreCase))
        {
            target = IssueStatus.Dismissed;
        }
        else
        {
            return BadRequest("Action must be 'Approve' or 'Dismiss'.");
        }

        // Guarded transition — only Detected/Queued rows are touched. Prevents a stale client
        // snapshot from un-ignoring items via bulk-approve, or un-fixing them via bulk-dismiss.
        return _db.BulkUpdateOpenIssueStatus(request.Ids, target);
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

    /// <summary>
    /// Lifetime reclaim totals broken out per library. Computed over the full history table so the
    /// number matches Status.LifetimeBytesReclaimed regardless of how many rows /History returns.
    /// Fixes the drift between the Overview "Reclaimed since install" panel (server-side sum) and
    /// the History tab's per-library chart (was client-side sum over the paginated 500-row window).
    /// </summary>
    /// <returns>Total bytes reclaimed + one entry per library (highest first).</returns>
    [HttpGet("History/Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetHistoryStats()
    {
        var libraryLocations = _libraryManager.GetVirtualFolders()
            .SelectMany(f => (f.Locations ?? []).Select(l => (Name: f.Name, Root: Path.TrimEndingDirectorySeparator(Path.GetFullPath(l)))))
            .ToList();

        long total = 0;
        var byLibrary = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (path, bytesFreed) in _db.GetLifetimePathTotals())
        {
            total += bytesFreed;
            var libName = ResolveLibraryName(path, libraryLocations);
            byLibrary[libName] = (byLibrary.TryGetValue(libName, out var running) ? running : 0) + bytesFreed;
        }

        return Ok(new
        {
            TotalBytesFreed = total,
            ByLibrary = byLibrary
                .Select(kv => new { Library = kv.Key, BytesFreed = kv.Value })
                .OrderByDescending(x => x.BytesFreed)
                .ToList()
        });
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
    /// <param name="force">When true and a file already exists at the original location, that file
    /// is moved to the recycle bin (and logged to history for its own restore) before this one
    /// is put back in its place. Without force=true the endpoint returns 409 so the UI can prompt.</param>
    /// <returns>No content on success; 404 when unknown; 409 when the file cannot be restored.</returns>
    [HttpPost("History/{id}/Restore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult RestoreFromHistory([FromRoute] long id, [FromQuery] bool force = false)
    {
        var entry = _db.GetHistoryEntry(id);
        if (entry is null)
        {
            return NotFound();
        }

        // Defense in depth: history rows from older MediaDash versions or a hand-edited DB can carry
        // a target path outside the current library set. Without this guard, a poisoned row could
        // direct MoveAcrossVolumes to write to arbitrary paths (e.g. /etc/cron.d/) that Jellyfin can
        // reach — arbitrary file write escalation. Mirrors RestoreOptimizedCopy's guard.
        if (!_libraryGuard.IsInsideLibrary(entry.Path))
        {
            return Conflict("Refused: the target path '" + entry.Path + "' is not inside a configured Jellyfin library.");
        }

        if (entry.Restored || string.IsNullOrEmpty(entry.RecyclePath) || !System.IO.File.Exists(entry.RecyclePath))
        {
            return Conflict("This file is no longer in the recycle bin.");
        }

        // The common conflict: a fix run replaced the file at entry.Path with a new (re-encoded /
        // stripped) version, so File.Exists(target) is true and RecycleBin.Restore refuses. When
        // force=true the caller has explicitly opted in — send the current file to the bin first
        // (reversible) and log it, then restore the original on top.
        if (System.IO.File.Exists(entry.Path))
        {
            if (!force)
            {
                return Conflict("A file already exists at " + entry.Path + ". Restoring would overwrite it. Use force=true to send the current file to the recycle bin first, then restore this one.");
            }

            try
            {
                var swappedBinPath = _recycleBin.MoveToBin(entry.Path);
                long swappedSize = 0;
                try
                {
                    swappedSize = new System.IO.FileInfo(swappedBinPath).Length;
                }
                catch (IOException)
                {
                }

                _db.AddHistory(new HistoryEntry
                {
                    IssueId = 0,
                    Type = entry.Type,
                    Path = entry.Path,
                    Action = "Swapped out to recycle bin so an older copy could be restored in its place.",
                    BytesFreed = 0,
                    RecyclePath = swappedBinPath,
                    FixedAtUtc = DateTime.UtcNow,
                    WasDryRun = false,
                    Success = true
                });
                _ = swappedSize; // reserved for a future BytesFreed reporting change
            }
            catch (IOException ex)
            {
                return Conflict("Couldn't move the current file to the recycle bin before restoring: " + ex.Message);
            }
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
    /// Probes the currently-configured recycle bin location for read+write access. Creates the folder
    /// if missing so a user setting up a fresh install can verify the path before their first fix.
    /// </summary>
    /// <returns>Path + CanRead / CanWrite / Error for the effective recycle bin root.</returns>
    [HttpGet("RecycleBinAccessCheck")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LibraryAccessResult> CheckRecycleBinAccess()
    {
        var effectiveRoot = _recycleBin.GetEffectiveRoot();
        var configured = Plugin.Instance!.Configuration.RecycleBinPath;
        var displayName = string.IsNullOrWhiteSpace(configured) ? "Recycle bin (default location)" : "Recycle bin";
        var entry = new LibraryAccessResult { Name = displayName, Path = effectiveRoot };

        try
        {
            System.IO.Directory.CreateDirectory(effectiveRoot);
            _ = System.IO.Directory.EnumerateFileSystemEntries(effectiveRoot).GetEnumerator().MoveNext();
            entry.CanRead = true;
        }
        catch (UnauthorizedAccessException)
        {
            entry.Error = "Jellyfin can't read '" + effectiveRoot + "'. Grant the Jellyfin user read access, or change the path in Settings → Recycle bin.";
        }
        catch (IOException ex)
        {
            entry.Error = "Cannot read '" + effectiveRoot + "': " + ex.Message;
        }

        if (entry.CanRead)
        {
            var probe = System.IO.Path.Combine(effectiveRoot, ".mediadash-access-probe-" + System.Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                System.IO.File.WriteAllBytes(probe, []);
                System.IO.File.Delete(probe);
                entry.CanWrite = true;
            }
            catch (UnauthorizedAccessException)
            {
                entry.Error = "Jellyfin can't write to '" + effectiveRoot + "'. On Linux: sudo chown -R jellyfin:jellyfin '" + effectiveRoot + "' — or pick a different location in Settings → Recycle bin.";
            }
            catch (IOException ex)
            {
                entry.Error = "Cannot write to '" + effectiveRoot + "': " + ex.Message;
            }
        }

        return Ok(entry);
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

    /// <summary>
    /// Per-library aggregates for the Overview breakdown charts: total item count, on-disk bytes,
    /// resolution / codec / container distribution. One item is matched to its library by path prefix
    /// against each virtual folder's Locations.
    /// </summary>
    /// <returns>One <see cref="LibraryStat"/> per configured library, sorted by name.</returns>
    [HttpGet("LibraryStats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LibraryStat>> GetLibraryStats()
    {
        var idLookup = Scanners.VirtualFolderIdentity.BuildIdLookup(_libraryManager);
        var folders = _libraryManager.GetVirtualFolders()
            .Where(f => Scanners.VirtualFolderIdentity.GetId(f, idLookup) is not null)
            .Select(f => new
            {
                Folder = f,
                Id = Scanners.VirtualFolderIdentity.GetId(f, idLookup)!,
                Locations = (f.Locations ?? Array.Empty<string>())
                    .Select(l => Path.TrimEndingDirectorySeparator(l) + Path.DirectorySeparatorChar)
                    .ToList()
            })
            .ToList();

        if (folders.Count == 0)
        {
            return Ok(new List<LibraryStat>());
        }

        var stats = folders.ToDictionary(
            f => f.Id,
            f => new LibraryStat
            {
                ItemId = f.Id,
                Name = f.Folder.Name ?? string.Empty,
                CollectionType = f.Folder.CollectionType?.ToString()?.ToLowerInvariant()
            });

        var items = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            IncludeItemTypes = new[]
            {
                Jellyfin.Data.Enums.BaseItemKind.Movie,
                Jellyfin.Data.Enums.BaseItemKind.Episode,
                Jellyfin.Data.Enums.BaseItemKind.MusicVideo,
                Jellyfin.Data.Enums.BaseItemKind.AudioBook
            },
            IsVirtualItem = false,
            Recursive = true
        });

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            LibraryStat? stat = null;
            foreach (var f in folders)
            {
                if (f.Locations.Any(loc => item.Path.StartsWith(loc, StringComparison.OrdinalIgnoreCase)))
                {
                    stat = stats[f.Id];
                    break;
                }
            }

            if (stat is null)
            {
                continue;
            }

            stat.ItemCount++;
            stat.TotalBytes += SafeFileSize(item.Path);

            var video = item.GetMediaStreams()
                .FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);

            var resKey = ResolutionBucket(video?.Height);
            stat.Resolutions[resKey] = stat.Resolutions.GetValueOrDefault(resKey) + 1;

            var codecKey = string.IsNullOrEmpty(video?.Codec) ? "unknown" : video!.Codec.ToLowerInvariant();
            stat.Codecs[codecKey] = stat.Codecs.GetValueOrDefault(codecKey) + 1;

            var ext = Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant();
            var contKey = string.IsNullOrEmpty(ext) ? "other" : ext;
            stat.Containers[contKey] = stat.Containers.GetValueOrDefault(contKey) + 1;
        }

        return Ok(stats.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static long SafeFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string ResolutionBucket(int? height)
    {
        if (!height.HasValue || height <= 0)
        {
            return "Unknown";
        }

        // Bucket to nearest common label — DVD-era vertical is 480/576, so 700 threshold catches HD.
        if (height.Value >= 2000)
        {
            return "4K";
        }

        if (height.Value >= 1000)
        {
            return "1080p";
        }

        if (height.Value >= 700)
        {
            return "720p";
        }

        return "SD";
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
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    _recycleBin.EmptyAll();
                }
                catch (Exception ex)
                {
                    // Fire-and-forget — no caller sees the exception. Surface via Diagnostics so a
                    // silently-failed empty (permission denied on a bin folder, disk gone away) isn't
                    // invisible to the user; the next Empty click retries.
                    Diagnostics.Record("RecycleBin.EmptyAll", "Recycle-bin empty did not complete: " + ex.Message + ". The next Empty click retries.");
                }
            });
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
            Success = result.Errors.Count == 0
        });
        // Only mark the sweep permanently completed when nothing failed. If some folders errored the
        // banner stays available so the user can retry, otherwise the failed folders would be
        // unreachable through the UI.
        if (result.Errors.Count == 0)
        {
            config.PostV12CleanupCompleted = true;
            Plugin.Instance!.SaveConfiguration();
        }

        return Ok(new { OrphanedFoldersDeleted = result.OrphanedFoldersDeleted, BytesFreed = result.BytesFreed, Errors = result.Errors, Dismissed = false });
    }

    /// <summary>
    /// Acknowledges a redownload warning. The row stays in history (savings totals unchanged) but the
    /// banner stops flagging it on subsequent scans.
    /// </summary>
    /// <param name="historyId">The history row id from the RedownloadWarning.</param>
    /// <returns>No content on success, 404 when unknown.</returns>
    [HttpPost("RedownloadWarnings/{historyId}/Acknowledge")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult AcknowledgeRedownloadWarning([FromRoute] long historyId)
    {
        if (!_db.AcknowledgeHistoryEntry(historyId))
        {
            return NotFound();
        }

        Plugin.RedownloadWarnings = RedownloadDetector.Detect(_db, TimeSpan.FromDays(30));
        return NoContent();
    }

    /// <summary>
    /// Restores the "optimized copy" for a redownload-warning row flagged as a pre-0.9.9
    /// SubtitleLanguage bug artefact: finds the smaller twin the bug wrongly moved to the recycle
    /// bin and puts it back at the original path. Acknowledges the row on success.
    /// </summary>
    /// <param name="historyId">The history row id.</param>
    /// <param name="force">When true and a file already exists at the target path, that file is sent
    /// to the recycle bin first so the twin can be restored on top.</param>
    /// <returns>No content on success, 404 when unknown, 409 when unresolvable.</returns>
    [HttpPost("RedownloadWarnings/{historyId}/RestoreOptimized")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult RestoreOptimizedCopy([FromRoute] long historyId, [FromQuery] bool force = false)
    {
        var entry = _db.GetHistoryEntry(historyId);
        if (entry is null)
        {
            return NotFound();
        }

        if (string.IsNullOrEmpty(entry.RecyclePath))
        {
            return Conflict("This history row has no recorded recycle path — the original file wasn't sent to the bin.");
        }

        // Defense in depth: history rows from older MediaDash versions (or a hand-edited DB) can carry
        // a Path outside any currently-configured library. Refuse to touch it — every other user path
        // through this controller validates via LibraryGuard.
        if (!_libraryGuard.IsInsideLibrary(entry.Path))
        {
            return Conflict("Refused: the target path '" + entry.Path + "' is not inside a configured Jellyfin library.");
        }

        var twin = _recycleBin.FindOptimizedTwin(entry.RecyclePath, entry.FixedAtUtc);
        if (twin is null)
        {
            return Conflict("Couldn't find an unambiguous optimized twin in the recycle bin. It may have been purged, or multiple candidates were found. Use the Recycle bin tab to pick manually, or use the History tab's Restore to bring back the original instead.");
        }

        if (System.IO.File.Exists(entry.Path))
        {
            if (!force)
            {
                return Conflict("A file already exists at " + entry.Path + ". Use force=true to send it to the recycle bin first, then restore the optimized copy in its place.");
            }

            try
            {
                _recycleBin.MoveToBin(entry.Path);
            }
            catch (IOException ex)
            {
                return Conflict("Couldn't move the current file to the recycle bin before restoring: " + ex.Message);
            }
        }

        try
        {
            _recycleBin.Restore(twin.Value.BinPath, entry.Path);
        }
        catch (IOException ex)
        {
            return Conflict(ex.Message);
        }

        _db.AcknowledgeHistoryEntry(historyId);
        _libraryMonitor.ReportFileSystemChanged(entry.Path);
        Plugin.RedownloadWarnings = RedownloadDetector.Detect(_db, TimeSpan.FromDays(30));
        return NoContent();
    }

    /// <summary>
    /// Gets recently-recorded plugin errors (system-stats sample failures, scanner/fixer exceptions).
    /// The default view returns the newest 100 from the in-memory ring buffer; pass full=true to
    /// pull the full persisted table (up to 5000 rows) for the Errors tab's "Load older" button.
    /// Persisted across Jellyfin restarts and plugin updates as of 1.0.5.
    /// </summary>
    /// <param name="full">When true, reads directly from the persisted diagnostics table.</param>
    /// <returns>The entries, newest first.</returns>
    [HttpGet("Errors")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DiagnosticEntry>> GetErrors([FromQuery] bool full = false)
    {
        return Ok(full ? Diagnostics.RecentAll() : Diagnostics.Recent());
    }

    /// <summary>
    /// Total number of persisted diagnostic entries. Cheap COUNT(*) so the Errors tab can decide
    /// whether to show the "Load older" button without transferring the full payload.
    /// </summary>
    /// <returns>Persisted row count.</returns>
    [HttpGet("Errors/Count")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetErrorsCount()
    {
        return Ok(new { Total = Diagnostics.PersistedCount() });
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

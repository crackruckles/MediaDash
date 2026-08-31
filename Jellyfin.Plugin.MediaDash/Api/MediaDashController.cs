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
        var binContents = SafeGetBinContents();

        (int FileCount, long SizeBytes) SafeGetBinContents()
        {
            try
            {
                return _recycleBin.GetContents();
            }
            catch (IOException)
            {
                return (0, 0);
            }
            catch (UnauthorizedAccessException)
            {
                return (0, 0);
            }
        }

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
            RecycleBinBytes = binContents.SizeBytes,
            RecycleBinFileCount = binContents.FileCount,
            RecycleBinRetentionDays = config.RecycleBinRetentionDays,
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
        var rows = openOnly
            ? _db.GetIssues(type, IssueStatus.Detected).Concat(_db.GetIssues(type, IssueStatus.Queued)).ToList()
            : _db.GetIssues(type, status).ToList();

        // Enrich each DTO with a "was previously restored" flag so the UI can render the badge that
        // explains why an auto-mode type isn't queuing on its own. Cache the restored path set per
        // IssueType so we do one query per distinct type in the result, not one per row.
        var restoredByType = new Dictionary<IssueType, IReadOnlySet<string>>();
        var dtos = new List<IssueDto>(rows.Count);
        foreach (var issue in rows)
        {
            var dto = IssueDto.FromIssue(issue);
            if (!restoredByType.TryGetValue(issue.Type, out var restored))
            {
                restored = _db.GetRestoredPathsBlockingAutoQueue(issue.Type);
                restoredByType[issue.Type] = restored;
            }

            dto.WasPreviouslyRestored = restored.Contains(issue.Path);
            dtos.Add(dto);
        }

        return Ok(dtos);
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
    /// Reverts a queued or dismissed issue back to Detected so the user can change their mind
    /// after clicking Approve or Dismiss by accident. Refuses when the issue has already Fixed
    /// (the file has already been touched) — the History tab's Restore is the right path for that.
    /// </summary>
    /// <param name="id">The issue id.</param>
    /// <returns>No content on success; 404 for unknown ids; 409 for any status other than Queued or Dismissed.</returns>
    [HttpPost("Issues/{id}/Revert")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult RevertIssue([FromRoute] long id)
    {
        var current = _db.GetIssueStatus(id);
        if (current is null)
        {
            return NotFound();
        }

        if (current != IssueStatus.Queued && current != IssueStatus.Dismissed)
        {
            return Conflict("Can only revert issues in Queued or Dismissed state; this one is " + current + ". If the fix has already run, use the History tab's Restore instead.");
        }

        return _db.UpdateIssueStatus(id, IssueStatus.Detected) ? NoContent() : NotFound();
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
    /// Restores a recycled file. If nothing sits at the original path, restores there. If a
    /// re-encoded / stripped replacement is already at that path, restores alongside it as
    /// <c>&lt;name&gt;-restored&lt;ext&gt;</c> (or -restored-2, -3, … on further collisions) so
    /// the user never risks data loss on restore.
    /// </summary>
    /// <param name="id">The history entry id.</param>
    /// <param name="force">When true and a file already exists at the original location, that file
    /// is moved to the recycle bin (and logged to history for its own restore) before this one is
    /// put back in its place. Default (false) uses the non-destructive -restored suffix path.</param>
    /// <returns>200 with the actual restored path; 404 when unknown; 409 when unrestorable.</returns>
    [HttpPost("History/{id}/Restore")]
    [ProducesResponseType(typeof(RestoreResult), StatusCodes.Status200OK)]
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

        var targetPath = entry.Path;
        var suffixed = false;

        if (System.IO.File.Exists(entry.Path))
        {
            if (force)
            {
                // Explicit destructive path: swap the current file to the bin so the original goes
                // back into its slot. Logged as its own recyclable history row (still reversible).
                try
                {
                    var swappedBinPath = _recycleBin.MoveToBin(entry.Path);
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
                }
                catch (IOException ex)
                {
                    return Conflict("Couldn't move the current file to the recycle bin before restoring: " + ex.Message);
                }
            }
            else
            {
                // Non-destructive default: land next to the current file with a suffix. The user's
                // reasonable concern is "will restoring nuke my re-encoded copy?" — this answers no,
                // both live side-by-side and the user picks which to keep from the File Browser.
                targetPath = ResolveNonCollidingRestorePath(entry.Path);
                suffixed = true;
            }
        }

        try
        {
            _recycleBin.Restore(entry.RecyclePath, targetPath);
        }
        catch (IOException ex)
        {
            return Conflict(ex.Message);
        }

        _db.MarkRestored(id);
        // Record the user's "don't touch this again" signal so the FixTask auto-queue step skips
        // any future re-detection at this (path, type) — the scanner still emits the Issue so the
        // user can see it in the Issues tab, but MediaDash won't automatically re-recycle it.
        // Guard on entry.Path (original) rather than targetPath so a -restored-suffixed collision
        // still protects the ORIGINAL slot — that's the file identity the user cared about.
        _db.MarkPathRestored(entry.Path, entry.Type);
        _libraryMonitor.ReportFileSystemChanged(targetPath);
        if (suffixed)
        {
            // Force is false and we wrote to a different path; still ping the original so any
            // Jellyfin-side listeners rescan the folder and pick up the sibling.
            _libraryMonitor.ReportFileSystemChanged(entry.Path);
        }

        return Ok(new RestoreResult { RestoredTo = targetPath, Suffixed = suffixed });
    }

    // Returns the original path when it's free; otherwise the first non-existing path of the form
    // <dir>/<name>-restored<ext>, <name>-restored-2<ext>, …. Exposed internal for unit-test reach —
    // tests seed collisions on disk in a tmpdir rather than mocking File.Exists.
    internal static string ResolveNonCollidingRestorePath(string originalPath)
    {
        if (!System.IO.File.Exists(originalPath))
        {
            return originalPath;
        }

        var dir = System.IO.Path.GetDirectoryName(originalPath) ?? string.Empty;
        var name = System.IO.Path.GetFileNameWithoutExtension(originalPath);
        var ext = System.IO.Path.GetExtension(originalPath);
        var candidate = System.IO.Path.Combine(dir, name + "-restored" + ext);
        var counter = 2;
        while (System.IO.File.Exists(candidate))
        {
            candidate = System.IO.Path.Combine(dir, name + "-restored-" + counter.ToString(System.Globalization.CultureInfo.InvariantCulture) + ext);
            counter++;
        }

        return candidate;
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
    /// Reports the size / free space of the volume that would own a candidate recycle bin path,
    /// plus a save-time verdict against the 5 GB minimum-free floor and a suggested value for
    /// <see cref="Configuration.PluginConfiguration.RecycleBinPauseFixesAtGb"/>. Called from the
    /// Settings page and the first-run wizard before persisting a new path.
    /// </summary>
    /// <param name="path">The candidate recycle bin path. Empty falls back to the effective default.</param>
    /// <returns>Volume capacity, free space, and derived pause-cap suggestion.</returns>
    [HttpGet("RecycleBin/DiskInfo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Endpoint requires RequiresElevation policy (admin only) and performs read-only probes (Directory.Exists + DriveInfo). No file writes and no path returned back to the client.")]
    public ActionResult<RecycleBinDiskInfo> GetRecycleBinDiskInfo([FromQuery] string? path)
    {
        const long minFreeBytes = 5L * 1024 * 1024 * 1024;
        const long floorBytes = 3L * 1024 * 1024 * 1024;

        var target = string.IsNullOrWhiteSpace(path) ? _recycleBin.GetEffectiveRoot() : path;
        var info = new RecycleBinDiskInfo { PathProbed = target };

        // The path doesn't need to exist yet — DriveInfo works off the parent volume. Walk up until
        // we hit a directory that resolves; when nothing resolves (path on an offline network share,
        // or drive letter that isn't mounted), report Warning so the UI can surface it.
        var probe = target;
        while (!string.IsNullOrEmpty(probe) && !System.IO.Directory.Exists(probe))
        {
            var parent = System.IO.Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, probe, StringComparison.Ordinal))
            {
                break;
            }

            probe = parent;
        }

        var drive = RecycleBin.FindDriveForPath(string.IsNullOrEmpty(probe) ? target : probe);
        if (drive is null)
        {
            info.Warning = "Couldn't resolve '" + target + "' to a mounted volume. Check the path exists and is reachable.";
            return Ok(info);
        }

        try
        {
            info.TotalBytes = drive.TotalSize;
            info.FreeBytes = drive.AvailableFreeSpace;
        }
        catch (IOException ex)
        {
            info.Warning = "Couldn't read free space on '" + drive.RootDirectory.FullName + "': " + ex.Message;
            return Ok(info);
        }

        info.MeetsFiveGbMinimum = info.FreeBytes >= minFreeBytes;
        info.SuggestedPauseCapGb = ComputeSuggestedPauseCapGb(info.TotalBytes, floorBytes);
        return Ok(info);
    }

    /// <summary>
    /// Computes the default value for <see cref="Configuration.PluginConfiguration.RecycleBinPauseFixesAtGb"/>
    /// from a volume's capacity: <c>totalBytes / GiB − 3</c>, clamped to at least 1 GB so a small volume
    /// doesn't save as 0 (which disables the cap altogether). Exposed internal for unit testing.
    /// </summary>
    /// <param name="totalBytes">The volume's total capacity in bytes.</param>
    /// <param name="floorBytes">The reserved free-space floor in bytes (default 3 GB).</param>
    /// <returns>The suggested pause cap in whole GB.</returns>
    internal static int ComputeSuggestedPauseCapGb(long totalBytes, long floorBytes)
    {
        const long gib = 1024L * 1024 * 1024;
        var floorGb = (int)System.Math.Max(1, floorBytes / gib);
        var totalGb = (int)System.Math.Max(0, totalBytes / gib);
        return System.Math.Max(1, totalGb - floorGb);
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
        // History is authoritative for the original destination path AND the reason (issue type +
        // action text) — the bin itself doesn't store either.
        var byRecyclePath = _db.GetHistory()
            .Where(h => !h.Restored && !string.IsNullOrEmpty(h.RecyclePath))
            .GroupBy(h => h.RecyclePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.FixedAtUtc).First(), StringComparer.OrdinalIgnoreCase);

        var retentionDays = Plugin.Instance?.Configuration.RecycleBinRetentionDays ?? 0;
        return Ok(_recycleBin.ListContents()
            .Select(e =>
            {
                var item = new RecycleBinItem { FileName = e.FileName, SizeBytes = e.SizeBytes, RecycledAtUtc = e.RecycledAtUtc };
                // F-207 follow-up: every DTO row carries BinPath. Previously it was set only for
                // manifest-provenance items; History-provenance items had it null, so clients
                // that wanted to restore via POST /RecycleBin/Items/Restore had no field to send
                // and hit the classic user issue #26 "how do I restore this". Frontend can still
                // prefer History/{id}/Restore when HistoryId is populated — this just makes the
                // fallback path always available.
                item.BinPath = e.BinPath;
                if (retentionDays > 0)
                {
                    item.AutoPurgesAtUtc = e.RecycledAtUtc.AddDays(retentionDays);
                }

                if (byRecyclePath.TryGetValue(e.BinPath, out var h))
                {
                    item.HistoryId = h.Id;
                    item.OriginalPath = h.Path;
                    item.Provenance = RecycleProvenance.History;
                    item.IssueType = h.Type.ToString();
                    item.Reason = RecycleReasonMapper.ReasonFor(h.Type);
                    item.ActionText = h.Action ?? string.Empty;
                    item.RestoreHint = RecycleReasonMapper.RestoreHintFor(RecycleProvenance.History, h.Path);
                }
                else if (!string.IsNullOrEmpty(e.OriginalPath))
                {
                    // Manifest-only: no HistoryEntry, but the batch's origin sidecar remembers the
                    // source path. Frontend restores via BinPath instead of HistoryId. Manual delete
                    // via the Files tab is the common trigger — hence the "Manual delete" reason.
                    item.OriginalPath = e.OriginalPath;
                    item.BinPath = e.BinPath;
                    item.Provenance = RecycleProvenance.Manifest;
                    item.Reason = "Manual delete via Files tab";
                    item.ActionText = "Sent to the recycle bin from the Files tab. Not tied to any MediaDash fix.";
                    item.RestoreHint = RecycleReasonMapper.RestoreHintFor(RecycleProvenance.Manifest, e.OriginalPath);
                }
                else
                {
                    // Truly orphaned: no HistoryEntry, no manifest. Recycled by a pre-manifest build.
                    // No safe automatic restore — the Files tab manual move is the only path.
                    item.Provenance = RecycleProvenance.Orphan;
                    item.Reason = "Origin unknown";
                    item.ActionText = "Recycled by an older MediaDash build that didn't record where the file came from.";
                    item.RestoreHint = RecycleReasonMapper.RestoreHintFor(RecycleProvenance.Orphan, null);
                }

                return item;
            })
            .ToList());
    }

    /// <summary>
    /// Restores one or more bin files identified by bin path (used for manifest-only items that no
    /// HistoryEntry references — sidecars, cover-art originals, and any legacy orphan the bin
    /// still remembers the origin of). Same suffix-on-collision semantics as History/{id}/Restore.
    /// <para>
    /// Single-item shape: <c>{"BinPath": "..."}</c> returns a <see cref="RestoreResult"/> body on
    /// success. Batch shape: <c>{"BinPaths": ["...", "..."]}</c> returns a
    /// <see cref="BatchRestoreResult"/> body with a per-entry outcome list — failures are recorded
    /// per row and the batch always returns 200 unless the request body itself is malformed.
    /// </para>
    /// </summary>
    /// <param name="request">The bin file(s) to restore.</param>
    /// <returns>200 with a <see cref="RestoreResult"/> (single) or <see cref="BatchRestoreResult"/> (batch);
    /// 400 when the body lacks any bin path; 404 when a single-item BinPath is unknown; 409 when unrestorable
    /// due to state (missing manifest, outside library).</returns>
    [HttpPost("RecycleBin/Items/Restore")]
    [ProducesResponseType(typeof(RestoreResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BatchRestoreResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult RestoreByBinPath([FromBody] BinRestoreRequest request)
    {
        // F-207 / issue #26: the endpoint used to return 409 for a missing BinPath, which
        // matches "state conflict" semantics but users routinely POST alternative shapes
        // ({ids: [...]}, {itemIds: [...]}, {id: "..."}) inherited from other bin APIs.
        // Distinguish "you sent the wrong shape" (400) from "you sent the right shape but
        // the target can't be restored right now" (409).

        // Batch path: {BinPaths: [...]} with 2+ entries. Always returns 200 with per-entry outcomes
        // so a bad path halfway through doesn't hide the successes. Single-item batches fall through
        // to the single-item path below so backward-compatible clients still get a RestoreResult.
        if (request.BinPaths is { Length: > 1 } batch)
        {
            var body = new BatchRestoreResult();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = _recycleBin.ListContents(limit: 5000);
            foreach (var candidatePath in batch)
            {
                if (string.IsNullOrEmpty(candidatePath) || !seen.Add(candidatePath))
                {
                    // Skip empty strings and de-duplicate — restoring the same bin file twice is a
                    // no-op on attempt two (the source is gone) but users would see a spurious
                    // failure row. Drop silently.
                    continue;
                }

                var row = RestoreOneBinPath(candidatePath, entries);
                body.Results.Add(row);
                if (row.Success)
                {
                    body.Successes++;
                }
                else
                {
                    body.Failures++;
                }
            }

            return Ok(body);
        }

        var binPath = request.BinPath;
        if (string.IsNullOrEmpty(binPath) && request.BinPaths is { Length: 1 } single)
        {
            binPath = single[0];
        }

        if (string.IsNullOrEmpty(binPath))
        {
            return BadRequest("Missing 'BinPath' (or 'BinPaths[]'). Get valid values from GET /MediaDash/RecycleBin/Items — each row's 'BinPath' field is what this endpoint expects. If your client is sending {ids: […]} or {itemIds: […]}, update it to send {BinPath: '…'} instead.");
        }

        var singleEntries = _recycleBin.ListContents(limit: 5000);
        var entry = singleEntries.FirstOrDefault(e =>
            string.Equals(e.BinPath, binPath, StringComparison.OrdinalIgnoreCase));
        if (entry.BinPath is null)
        {
            return NotFound();
        }

        if (string.IsNullOrEmpty(entry.OriginalPath))
        {
            return Conflict("This bin file has no origin manifest — MediaDash doesn't know where it came from. Restore it manually from the Files tab's Recycle bin shortcut.");
        }

        if (!_libraryGuard.IsInsideLibrary(entry.OriginalPath))
        {
            return Conflict("Refused: the origin path '" + entry.OriginalPath + "' is not inside a configured Jellyfin library.");
        }

        var targetPath = System.IO.File.Exists(entry.OriginalPath)
            ? ResolveNonCollidingRestorePath(entry.OriginalPath)
            : entry.OriginalPath;
        var suffixed = !string.Equals(targetPath, entry.OriginalPath, StringComparison.Ordinal);

        try
        {
            _recycleBin.Restore(entry.BinPath, targetPath);
        }
        catch (IOException ex)
        {
            return Conflict(ex.Message);
        }

        // Log the manual restore as a history row so future audits can see it — matches the shape
        // of a normal fixer output but with no IssueId, and marked Restored=true straight away.
        _db.AddHistory(new HistoryEntry
        {
            IssueId = 0,
            Type = Data.IssueType.Duplicate,
            Path = entry.OriginalPath!,
            Action = suffixed
                ? "Restored bin file (with -restored suffix) via Recycle bin tab: " + targetPath
                : "Restored bin file via Recycle bin tab: " + targetPath,
            BytesFreed = 0,
            RecyclePath = entry.BinPath,
            FixedAtUtc = DateTime.UtcNow,
            WasDryRun = false,
            Success = true,
            Restored = true
        });

        // Manifest-only restores don't know the underlying IssueType (the manifest sidecar stores
        // only the origin path). Record the block under the "any type" sentinel so no scanner can
        // auto-fix this path again. If it re-detects, the row sits Detected for manual review.
        _db.MarkPathRestoredForAnyType(entry.OriginalPath!);

        _libraryMonitor.ReportFileSystemChanged(targetPath);
        return Ok(new RestoreResult { RestoredTo = targetPath, Suffixed = suffixed });
    }

    /// <summary>
    /// Per-entry helper for the batch restore path. Same policy as the single-item path but
    /// records failures into a <see cref="BatchRestoreEntry"/> row instead of returning HTTP
    /// status codes, so one bad path doesn't fail the whole batch.
    /// </summary>
    private BatchRestoreEntry RestoreOneBinPath(
        string binPath,
        System.Collections.Generic.IReadOnlyList<(string FileName, string BinPath, long SizeBytes, DateTime RecycledAtUtc, string? OriginalPath)> entries)
    {
        var row = new BatchRestoreEntry { BinPath = binPath };
        var entry = entries.FirstOrDefault(e => string.Equals(e.BinPath, binPath, StringComparison.OrdinalIgnoreCase));
        if (entry.BinPath is null)
        {
            row.Error = "Unknown bin path — not currently in the recycle bin.";
            return row;
        }

        if (string.IsNullOrEmpty(entry.OriginalPath))
        {
            row.Error = "No origin manifest — MediaDash doesn't know where this file came from. Restore manually from the Files tab.";
            return row;
        }

        if (!_libraryGuard.IsInsideLibrary(entry.OriginalPath))
        {
            row.Error = "Origin path '" + entry.OriginalPath + "' is not inside any configured Jellyfin library.";
            return row;
        }

        var targetPath = System.IO.File.Exists(entry.OriginalPath)
            ? ResolveNonCollidingRestorePath(entry.OriginalPath)
            : entry.OriginalPath;
        var suffixed = !string.Equals(targetPath, entry.OriginalPath, StringComparison.Ordinal);

        try
        {
            _recycleBin.Restore(entry.BinPath, targetPath);
        }
        catch (IOException ex)
        {
            row.Error = ex.Message;
            return row;
        }

        _db.AddHistory(new HistoryEntry
        {
            IssueId = 0,
            Type = Data.IssueType.Duplicate,
            Path = entry.OriginalPath!,
            Action = suffixed
                ? "Batch-restored bin file (with -restored suffix) via Recycle bin tab: " + targetPath
                : "Batch-restored bin file via Recycle bin tab: " + targetPath,
            BytesFreed = 0,
            RecyclePath = entry.BinPath,
            FixedAtUtc = DateTime.UtcNow,
            WasDryRun = false,
            Success = true,
            Restored = true
        });
        _db.MarkPathRestoredForAnyType(entry.OriginalPath!);
        _libraryMonitor.ReportFileSystemChanged(targetPath);

        row.Success = true;
        row.RestoredTo = targetPath;
        row.Suffixed = suffixed;
        return row;
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
    /// Lists other bin-root locations the user has historically recycled to (derived from
    /// <c>HistoryEntry.RecyclePath</c>), excluding the currently-configured root. Powers the
    /// Recycle bin tab's "Consolidate legacy locations" banner.
    /// </summary>
    /// <returns>Zero or more other bin locations with file counts + sizes.</returns>
    [HttpGet("RecycleBin/OtherBins")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<OtherBinLocation>> GetOtherBinLocations()
    {
        return Ok(_recycleBin.DiscoverOtherBinRoots(_db)
            .Select(t => new OtherBinLocation { RootPath = t.RootPath, BatchCount = t.BatchCount, SizeBytes = t.SizeBytes })
            .ToList());
    }

    /// <summary>
    /// Moves every MediaDash-shaped batch folder from a legacy bin root into the currently
    /// configured one. Cross-volume safe (falls back to verified copy + delete). Only touches
    /// folders whose leaf matches <c>IsMediaDashBatchName</c>. Returns counts + bytes moved.
    /// </summary>
    /// <param name="request">Body naming the source root.</param>
    /// <returns>Consolidation result summary.</returns>
    [HttpPost("RecycleBin/Consolidate")]
    [ProducesResponseType(typeof(ConsolidateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "Endpoint requires RequiresElevation policy (admin only). SourceRoot must match one of the paths returned by GetOtherBinLocations — validated inside the method against DiscoverOtherBinRoots so an arbitrary path can't be laundered through here.")]
    public ActionResult ConsolidateBin([FromBody] ConsolidateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceRoot))
        {
            return BadRequest("SourceRoot is required.");
        }

        // Gate the source against DiscoverOtherBinRoots — that's the only set of paths the UI is
        // supposed to send. Prevents an admin token from being used to move arbitrary folders on
        // the host; also refuses when the "other" root turns out to be the current one.
        var known = _recycleBin.DiscoverOtherBinRoots(_db)
            .Any(t => string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(t.RootPath)),
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(request.SourceRoot)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (!known)
        {
            return NotFound();
        }

        var result = _recycleBin.ConsolidateFromRoot(request.SourceRoot);
        return Ok(new ConsolidateResult
        {
            BatchesMoved = result.BatchesMoved,
            BatchesSkipped = result.BatchesSkipped,
            BytesMoved = result.BytesMoved,
            Warning = result.Warning
        });
    }

    /// <summary>
    /// Adopts an unowned legacy batch sitting at the top level of the current recycle root by writing
    /// the ownership marker into it. Once adopted, the batch is folded into the managed bin: it will
    /// show up in the Recycle bin tab, be counted by size totals, and honour retention purges.
    /// Rejected paths (wrong root, non-batch shape, missing directory) return 400.
    /// Retained for legacy Errors-tab entries; new installs auto-adopt on startup.
    /// </summary>
    /// <param name="request">The batch to adopt.</param>
    /// <returns>No content on success, or 400 when the path is not adoptable.</returns>
    [HttpPost("RecycleBin/AdoptBatch")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult AdoptRecycleBinBatch([FromBody] AdoptBatchRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest("Path is required.");
        }

        if (!_recycleBin.AdoptBatchByPath(request.Path))
        {
            return BadRequest("That path is not an unowned MediaDash batch directly inside the configured recycle bin root.");
        }

        // Purge the stale LegacyBatchNeedsReview diagnostic that pointed at this batch so the
        // Errors card disappears on the next refresh — the condition is fixed. Match on the
        // leaf (timestamp+GUID) rather than the full path so a Windows separator difference
        // between the recorded message (Path.Combine → backslash) and the request (JSON body,
        // often forward slashes) doesn't miss.
        var batchLeaf = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(request.Path));
        if (!string.IsNullOrEmpty(batchLeaf))
        {
            Diagnostics.RemoveMatching("RecycleBin.LegacyBatchNeedsReview", batchLeaf);
        }

        return NoContent();
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

        // Pass the source's on-disk path so SelectOptimizedTwin can prefer manifest-matched
        // candidates when two same-basename bin files sit in the fix's time window.
        var twin = _recycleBin.FindOptimizedTwin(entry.RecyclePath, entry.FixedAtUtc, entry.Path);
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
                var swappedBinPath = _recycleBin.MoveToBin(entry.Path);
                // Parity with RestoreFromHistory's force=true branch: the swapped-out file gets its
                // own HistoryEntry so it stays restorable from the Recycle bin tab. Without this row
                // the swap silently vanishes from History and only shows up under "no origin".
                _db.AddHistory(new HistoryEntry
                {
                    IssueId = 0,
                    Type = entry.Type,
                    Path = entry.Path,
                    Action = "Swapped out to recycle bin so the optimized copy could be restored in its place.",
                    BytesFreed = 0,
                    RecyclePath = swappedBinPath,
                    FixedAtUtc = DateTime.UtcNow,
                    WasDryRun = false,
                    Success = true
                });
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
    /// Gets environment info used by the Errors tab's "Report an issue" button and by the wizard's
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

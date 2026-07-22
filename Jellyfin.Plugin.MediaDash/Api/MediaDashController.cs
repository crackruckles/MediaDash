using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller.Library;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDashController"/> class.
    /// </summary>
    /// <param name="db">The plugin database.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    public MediaDashController(MediaDashDb db, ITaskManager taskManager, RecycleBin recycleBin, ILibraryMonitor libraryMonitor, ILibraryManager libraryManager)
    {
        _db = db;
        _taskManager = taskManager;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _libraryManager = libraryManager;
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
                    IsLibraryDrive = isLibraryDrive
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
            RecycleBinCrossVolume = ComputeRecycleBinCrossVolume(drives)
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
        return Ok(_db.GetHistory().Select(HistoryDto.FromEntry).ToList());
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
        return Ok(_recycleBin.ListContents()
            .Select(e => new RecycleBinItem { FileName = e.FileName, SizeBytes = e.SizeBytes, RecycledAtUtc = e.RecycledAtUtc })
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

    private IScheduledTaskWorker? GetScanTask()
    {
        return _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is ScanTask);
    }
}

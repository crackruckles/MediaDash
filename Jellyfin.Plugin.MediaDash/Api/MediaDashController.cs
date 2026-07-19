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

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDashController"/> class.
    /// </summary>
    /// <param name="db">The plugin database.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    public MediaDashController(MediaDashDb db, ITaskManager taskManager, RecycleBin recycleBin, ILibraryMonitor libraryMonitor)
    {
        _db = db;
        _taskManager = taskManager;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
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
        return new StatusResponse
        {
            IsScanning = scanTask is not null && scanTask.State != TaskState.Idle,
            ScanProgress = scanTask?.CurrentProgress,
            IsFixing = fixTask is not null && fixTask.State != TaskState.Idle,
            FixProgress = fixTask?.CurrentProgress,
            OpenIssueTotal = summary.Sum(s => s.Count),
            LastScanUtc = summary.Count > 0 ? summary.Max(s => s.NewestDetectedUtc) : null,
            TotalPotentialSavings = summary.Sum(s => s.PotentialSavings),
            Counts = summary.Select(s => new TypeCount
            {
                Type = s.Type.ToString(),
                Count = s.Count,
                PotentialSavings = s.PotentialSavings
            }).ToList()
        };
    }

    /// <summary>
    /// Gets issues, optionally filtered by type and status.
    /// </summary>
    /// <param name="type">Filter by issue type.</param>
    /// <param name="status">Filter by status; defaults to detected.</param>
    /// <returns>The matching issues.</returns>
    [HttpGet("Issues")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<IssueDto>> GetIssues(
        [FromQuery] IssueType? type = null,
        [FromQuery] IssueStatus? status = IssueStatus.Detected)
    {
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
    /// Approves all open issues of a type at once.
    /// </summary>
    /// <param name="type">The issue type to approve.</param>
    /// <returns>The number of issues queued.</returns>
    [HttpPost("Issues/ApproveAll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<int> ApproveAll([FromQuery] IssueType type)
    {
        return _db.QueueDetectedIssues(type);
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
        return new RecycleBinInfo { FileCount = count, SizeBytes = size };
    }

    /// <summary>
    /// Permanently empties the recycle bin. Files can no longer be restored afterwards.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("RecycleBin/Empty")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult EmptyRecycleBin()
    {
        _recycleBin.EmptyAll();
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

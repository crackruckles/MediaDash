using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDashController"/> class.
    /// </summary>
    /// <param name="db">The plugin database.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    public MediaDashController(MediaDashDb db, ITaskManager taskManager)
    {
        _db = db;
        _taskManager = taskManager;
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
        return new StatusResponse
        {
            IsScanning = scanTask is not null && scanTask.State != TaskState.Idle,
            ScanProgress = scanTask?.CurrentProgress,
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
            _taskManager.Execute(fixTask, new TaskOptions());
        }

        return NoContent();
    }

    private IScheduledTaskWorker? GetScanTask()
    {
        return _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is ScanTask);
    }
}

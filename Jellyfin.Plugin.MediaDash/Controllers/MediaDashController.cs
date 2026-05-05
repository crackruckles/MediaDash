using System.Net.Mime;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Api;
using MediaBrowser.Controller.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaDash.Controllers;

/// <summary>
/// MediaDash REST API — all routes are under /mediadash/api/.
/// Every endpoint requires an authenticated Jellyfin admin session.
/// </summary>
[ApiController]
[Route("mediadash/api")]
[Authorize(Policy = "RequiresElevation")]   // Jellyfin built-in admin policy
[Produces(MediaTypeNames.Application.Json)]
public sealed class MediaDashController : ControllerBase
{
    private readonly MediaDashService _svc;

    public MediaDashController(MediaDashService svc) => _svc = svc;

    // ── Read endpoints ──────────────────────────────────────────────────

    [HttpGet("disk")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DiskInfo> GetDisk() => _svc.GetDisk();

    [HttpGet("metrics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MetricsInfo> GetMetrics() => _svc.GetMetrics();

    [HttpGet("streams")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StreamInfo[]> GetStreams() => _svc.GetStreams();

    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusInfo> GetStatus() => _svc.GetStatus();

    [HttpGet("encode_status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<EncodeStatusResponse> GetEncodeStatus()
        => _svc.GetEncodeStatus();

    [HttpGet("encode_remaining")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueueItem[]> GetEncodeRemaining()
        => _svc.GetEncodeRemaining();

    [HttpGet("strip")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StripEntry[]> GetStrip()
        => _svc.ParseStripLog();

    [HttpGet("reencode")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ReencodeEntry[]> GetReencode()
        => _svc.ParseReencodeLog();

    [HttpGet("dupes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DupesReport> GetDupes()
        => _svc.GetDupesReport();

    // ── Action endpoints ────────────────────────────────────────────────

    [HttpPost("pause")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<OkResponse> Pause()
    {
        _svc.Pause();
        return new OkResponse(true, "Encoding paused");
    }

    [HttpPost("resume")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<OkResponse> Resume()
    {
        _svc.Resume();
        return new OkResponse(true, "Force resuming");
    }

    [HttpPost("run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<OkResponse> RunScript([FromBody] RunRequest req)
    {
        var cfg = Plugin.Instance!.Configuration;
        string? service = req.Script switch
        {
            "reencode-bdremux" => cfg.ReencodeServiceName,
            "strip-tracks"     => cfg.StripServiceName,
            _ => null
        };

        if (service == null)
            return BadRequest(new ErrorResponse("Unknown script name"));

        _svc.StartService(service);
        return new OkResponse(true, $"Started {service}");
    }

    [HttpPost("dupes/scan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<OkResponse> ScanDupes()
    {
        if (!_svc.StartDupesScan())
            return NotFound(new ErrorResponse(
                "Dupes scan script not found — check plugin settings"));
        return new OkResponse(true, "Scan started");
    }

    [HttpPost("dupes/delete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DeleteResponse> DeleteDupes(
        [FromBody] DeleteDupesRequest? req)
        => _svc.DeleteDupes(req?.Imdb);

    [HttpPost("schedule")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ScheduleSaveResponse> SaveSchedule(
        [FromBody] ScheduleRequest req)
        => _svc.SaveSchedule(req.PauseStart, req.PauseEnd);
}

// ── Request bodies ────────────────────────────────────────────────────────────

public record RunRequest(string Script);
public record DeleteDupesRequest(string? Imdb);
public record ScheduleRequest(int PauseStart, int PauseEnd);

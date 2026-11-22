using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
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
    private static readonly IReadOnlyCollection<IssueStatus> ApproveFromDetected = new[] { IssueStatus.Detected };
    private static readonly IReadOnlyCollection<IssueStatus> DismissFromOpen = new[] { IssueStatus.Detected, IssueStatus.Queued };
    private static readonly IReadOnlyCollection<IssueStatus> RevertFromQueuedOrDismissed = new[] { IssueStatus.Queued, IssueStatus.Dismissed };

    private readonly MediaDashDb _db;
    private readonly IssueStore _issues;
    private readonly HistoryStore _history;
    private readonly ITaskManager _taskManager;
    private readonly RecycleBin _recycleBin;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;
    private readonly IEnumerable<ISubtitleProvider> _subtitleProviders;
    private readonly IEnumerable<IScanner> _scanners;
    private readonly PostUpgradeCleanup _postUpgradeCleanup;
    private readonly LibraryGuard _libraryGuard;
    private readonly ScheduledTasks.RunState _runState;
    private readonly RestoreService _restoreService;
    private readonly StatusQueryService _statusQuery;
    private readonly Fixers.ProtectionService _protection;
    private readonly IEnumerable<IFixer>? _fixers;
    private readonly MediaMoveService? _moves;
    private readonly MediaMovePlanner? _planner;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDashController"/> class.
    /// </summary>
    /// <param name="db">The plugin database.</param>
    /// <param name="issues">Issue-table store (05-C.2). Issue-family endpoints (approve/dismiss/revert/bulk/queue/summary/list) route through here; cross-store endpoints stay on <paramref name="db"/>.</param>
    /// <param name="history">History-table store (05-C.3). Powers history/lifetime/redownload endpoints; <c>MediaDashDb.RecordFixOutcome</c> stays the cross-store transaction seam.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="recycleBin">The recycle bin.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="appHost">Server application host, used for Jellyfin version in diagnostics.</param>
    /// <param name="subtitleProviders">Registered subtitle providers, used to warn when none are configured.</param>
    /// <param name="scanners">Registered scanners, used by targeted-scan endpoints like the Maintenance virus scan.</param>
    /// <param name="postUpgradeCleanup">The one-shot post-Jellyfin-12 upgrade cleaner.</param>
    /// <param name="libraryGuard">The library boundary guard; used to refuse restores whose stored path escaped the current library set.</param>
    /// <param name="runState">The shared run-state holder (02-A) — used to arm bypass-idle-once and read the fix pause reason.</param>
    /// <param name="restoreService">05-B: single owner of the restore workflow (owner-tokened claim + FS + DB bookkeeping).</param>
    /// <param name="statusQuery">05-C.7: composes the /Status payload. Owns drive scan, SMART probe, aggregate history reads, recycle-bin contents and library-drive classification.</param>
    /// <param name="protection">Roadmap i-110-protected-media: applied to targeted rescans; also backs the /Protection CRUD endpoints.</param>
    /// <param name="fixers">Registered handlers used to explain unsupported approvals.</param>
    /// <param name="moves">Complete move recovery and Undo.</param>
    /// <param name="planner">Re-plans one move after the user supplies a missing season.</param>
    public MediaDashController(
        MediaDashDb db,
        IssueStore issues,
        HistoryStore history,
        ITaskManager taskManager,
        RecycleBin recycleBin,
        ILibraryMonitor libraryMonitor,
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        IEnumerable<ISubtitleProvider> subtitleProviders,
        IEnumerable<IScanner> scanners,
        PostUpgradeCleanup postUpgradeCleanup,
        LibraryGuard libraryGuard,
        ScheduledTasks.RunState runState,
        RestoreService restoreService,
        StatusQueryService statusQuery,
        Fixers.ProtectionService protection,
        IEnumerable<IFixer>? fixers = null,
        MediaMoveService? moves = null,
        MediaMovePlanner? planner = null)
    {
        _planner = planner;
        _db = db;
        _issues = issues;
        _history = history;
        _taskManager = taskManager;
        _recycleBin = recycleBin;
        _libraryMonitor = libraryMonitor;
        _libraryManager = libraryManager;
        _appHost = appHost;
        _subtitleProviders = subtitleProviders;
        _scanners = scanners;
        _postUpgradeCleanup = postUpgradeCleanup;
        _libraryGuard = libraryGuard;
        _runState = runState;
        _restoreService = restoreService;
        _statusQuery = statusQuery;
        _protection = protection;
        _fixers = fixers;
        _moves = moves;
        // 02-A: bridge to Plugin.AttachedRunState so the display-only Plugin.CurrentActivity /
        // ScanScannersDone/Total facades resolve to this same instance. Idempotent — ScanTask
        // and FixTask do the same on their own resolution.
        Plugin.AttachedRunState = runState;
    }

    /// <summary>
    /// Gets the dashboard status: issue counts, potential savings and scan state.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusResponse> GetStatus()
    {
        // Status aggregates ~10 DB queries + drive scan + SMART + system stats. On loaded servers
        // during a scan this competes with scanner DB writes and the Overview polling loop can
        // fire a new request before the previous one serializes. Same request-teardown class that
        // crashed /Issues in #80 — bail out early if the client has already disconnected.
        HttpContext?.RequestAborted.ThrowIfCancellationRequested();
        return _statusQuery.BuildStatus();
    }

    /// <summary>
    /// Lightweight live-progress payload for Overview polling during an active scan/fix. Does zero
    /// DB work so it never contends with scanner writes. The Overview tab polls this on fast ticks
    /// and falls back to full /Status on the slower cold-tick cadence. See <see cref="ProgressSnapshot"/>.
    /// </summary>
    /// <returns>The progress snapshot.</returns>
    [HttpGet("Status/Progress")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ProgressSnapshot> GetStatusProgress()
    {
        HttpContext?.RequestAborted.ThrowIfCancellationRequested();
        return _statusQuery.BuildProgress();
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
        if (!ModelState.IsValid || (type.HasValue && !Enum.IsDefined(type.Value))
            || (status.HasValue && !Enum.IsDefined(status.Value)))
        {
            return BadRequest("Choose a valid issue type and status.");
        }

        var cancellationToken = HttpContext?.RequestAborted ?? default;
        cancellationToken.ThrowIfCancellationRequested();
        var rows = openOnly
            ? _issues.GetIssues(type, IssueStatus.Detected).Concat(_issues.GetIssues(type, IssueStatus.Queued)).ToList()
            : _issues.GetIssues(type, status).ToList();

        // Enrich each DTO with a "was previously restored" flag so the UI can render the badge that
        // explains why an auto-mode type isn't queuing on its own. Cache the restored path set per
        // IssueType so we do one query per distinct type in the result, not one per row.
        var restoredByType = new Dictionary<IssueType, IReadOnlySet<string>>();
        // Per-row last-attempt lookup: one batched query for the whole page, keyed by issue id.
        // Empty on a Detected-only page (nothing's been attempted yet); populated when Queued
        // rows keep failing silently so the UI can render "last attempt: X, Y ago".
        var lastAttempts = _history.GetLatestAttemptsByIssueIds(rows.Select(r => r.Id).ToList(), cancellationToken);
        var dtos = new List<IssueDto>(rows.Count);
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var enabledLocations = _libraryGuard?.GetEnabledLibraryLocations(config.EnabledLibraries);
        var protectionCheck = _protection?.CreateFixerCheck();
        var typeReasons = rows.Select(issue => issue.Type).Distinct().ToDictionary(type => type, type =>
            GetFixBlockedReason(config, type) ?? (_fixers is not null && !_fixers.Any(fixer => fixer.CanFix(type))
                ? "No fixer is available for this issue type on this server." : null));
        foreach (var issue in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dto = IssueDto.FromIssue(issue);
            dto.FixBlockedReason = GetFixBlockedReason(config, issue, out var requiresRescan, enabledLocations, typeReasons, protectionCheck);
            dto.FixRequiresRescan = requiresRescan;
            dto.CanMoveAnyway = issue.Status == IssueStatus.Detected && IsBlockedOnlyByParkedMove(issue);
            dto.NeedsSeason = issue.Status == IssueStatus.Detected && IsBlockedOnlyByUnknownSeason(issue);
            if (!restoredByType.TryGetValue(issue.Type, out var restored))
            {
                restored = _db.GetRestoredPathsBlockingAutoQueue(issue.Type);
                restoredByType[issue.Type] = restored;
            }

            dto.WasPreviouslyRestored = restored.Contains(issue.Path);
            if (lastAttempts.TryGetValue(issue.Id, out var lastAttempt))
            {
                dto.LastAttemptMessage = lastAttempt.Action;
                dto.LastAttemptAtUtc = lastAttempt.FixedAtUtc;
                dto.LastAttemptSuccess = lastAttempt.Success;
            }

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
        => StartScanCore(null);

    /// <summary>Starts only the ungrouped-media scanner.</summary>
    /// <returns>No content when the scan is scheduled.</returns>
    [HttpPost("Scan/Ungrouped")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult StartUngroupedScan()
        => StartScanCore(new HashSet<IssueType> { IssueType.Ungrouped });

    /// <summary>Starts only the scanners for the chosen issue types (Scan now's right-click / long-press picker).</summary>
    /// <param name="request">The issue type names to scan.</param>
    /// <returns>No content when the scan is scheduled; bad request when the selection is empty or unknown.</returns>
    [HttpPost("Scan/Selected")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult StartSelectedScan([FromBody] ScanSelectionRequest request)
    {
        if (!TryParseScanSelection(request?.Types, out var types, out var error))
        {
            return BadRequest(new { Error = error });
        }

        return StartScanCore(types);
    }

    /// <summary>
    /// Parses a scan-picker selection into scanner issue types. Rejects an empty selection (an
    /// empty filter would mean "run everything", the opposite of what the user ticked), unknown
    /// names, and record-only types that no scanner produces.
    /// </summary>
    /// <param name="names">The submitted issue type names.</param>
    /// <param name="types">The parsed types.</param>
    /// <param name="error">The validation error, when parsing fails.</param>
    /// <returns>True when at least one valid scanner type was selected.</returns>
    internal static bool TryParseScanSelection(IEnumerable<string>? names, out HashSet<IssueType> types, out string? error)
    {
        types = new HashSet<IssueType>();
        error = null;
        foreach (var name in names ?? Array.Empty<string>())
        {
            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (int.TryParse(trimmed, out _)
                || !Enum.TryParse<IssueType>(trimmed, true, out var type)
                || !Enum.IsDefined(type)
                || type is IssueType.ManualDelete or IssueType.ManualMove)
            {
                error = "Unknown scan type '" + trimmed + "'.";
                return false;
            }

            types.Add(type);
        }

        if (types.Count == 0)
        {
            error = "Choose at least one scan to run.";
            return false;
        }

        return true;
    }

    private ActionResult StartScanCore(IReadOnlySet<IssueType>? types)
    {
        var scanTask = GetScanTask();
        if (scanTask is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "The scan task is unavailable.");
        }

        if (scanTask.State != TaskState.Idle)
        {
            return Conflict("A scan is already running or cancelling.");
        }

        _runState.ArmNextScanTypeFilter(types);
        _runState.ArmBypassIdleCheck(ScheduledTasks.RunRole.Scan);
        _taskManager.Execute(scanTask, new TaskOptions());
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
        var scanTask = GetScanTask();
        if (scanTask is not null && scanTask.State != TaskState.Idle)
        {
            return Conflict("Wait for the current scan to finish.");
        }

        var scanner = _scanners.OfType<SuspiciousFileScanner>().FirstOrDefault();
        if (scanner is null)
        {
            return NotFound(new { Error = "SuspiciousFileScanner is not registered." });
        }

        var start = DateTime.UtcNow;
        var progress = new Progress<double>();
        var completedRoots = new List<string>();
        var issues = await scanner.RunScanAsync(progress, HttpContext.RequestAborted, completedRoots).ConfigureAwait(false);
        // Roadmap i-110-protected-media: apply protection to targeted rescans too.
        issues = _protection.FilterEmittable(issues, scanner.Type);
        var replacementPaths = _issues.GetIssues(scanner.Type, IssueStatus.Detected)
            .Where(issue => completedRoots.Any(root => LibraryGuard.IsUnder(issue.Path, root)))
            .Select(issue => issue.Path).Concat(issues.Select(issue => issue.Path))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
        _issues.ReplaceDetectedIssues(scanner.Type, issues, replacementPaths);
        return Ok(new
        {
            Detected = issues.Count,
            ElapsedMs = (long)(DateTime.UtcNow - start).TotalMilliseconds,
        });
    }

    /// <summary>
    /// "Move anyway" for a move blocked by an earlier move left needing review: each such parked move is
    /// finished forward when that is safe, otherwise its reservation is released with every file left
    /// in place. Ungrouped/Misplaced media is then rescanned so the item gets a fresh, movable plan.
    /// </summary>
    /// <param name="id">The issue id.</param>
    /// <returns>What was done.</returns>
    [HttpPost("Issues/{id}/MoveAnyway")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<string> MoveAnyway([FromRoute] long id)
    {
        var issue = _issues.GetIssue(id);
        if (issue is null)
        {
            return NotFound();
        }

        if (issue.Type is not (IssueType.Ungrouped or IssueType.Misplaced) || MediaMovePlan.ReadIssue(issue) is not { } plan || _moves is null)
        {
            return Conflict("Move anyway only applies to a move blocked by an earlier move.");
        }

        var parked = _db.Moves.FindParkedBlocking(plan);
        var outcomes = parked.Select(operation => _moves.ResolveParked(operation)).ToArray();
        var scan = StartScanCore(new HashSet<IssueType> { issue.Type });
        var rescanning = scan is NoContentResult;
        var message = (outcomes.Length == 0 ? "Nothing reserves this item any more." : string.Join(" ", outcomes))
            + (rescanning ? " Scanning again now; approve the item when it reappears with a fresh plan." : " Scan again when the current scan finishes, then approve the item.");
        return Ok(message);
    }

    /// <summary>
    /// "Enter season number" for an episode Jellyfin could not place in a season: the number is kept
    /// for the episode (later scans use it too), the item is planned again into that season folder,
    /// approved, and a fix run starts.
    /// </summary>
    /// <param name="id">The issue id.</param>
    /// <param name="season">The season number the user entered (0 for specials).</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>What happens next, or why the item still cannot move.</returns>
    [HttpPost("Issues/{id}/Season")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<string>> SetSeason([FromRoute] long id, [FromQuery] int season, CancellationToken cancellationToken)
    {
        if (season is < 0 or > 999)
        {
            return BadRequest("Enter a season number from 0 (specials) to 999.");
        }

        var issue = _issues.GetIssue(id);
        if (issue is null)
        {
            return NotFound();
        }

        if (_planner is null || issue.Status != IssueStatus.Detected || !IsBlockedOnlyByUnknownSeason(issue) || MediaMovePlan.ReadIssue(issue) is not { } current)
        {
            return Conflict("A season number can only be entered for a move waiting on an unknown season.");
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        _db.SetState(MediaMovePlanner.SeasonOverrideKey(current.ItemId), season.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var items = _libraryManager.GetItemList(MediaMovePlanner.CatalogQuery());
        var planned = await _planner.PlanItemAsync(issue.Type, items, config, current.ItemId, cancellationToken).ConfigureAwait(false);
        if (planned is null || MediaMovePlan.ReadIssue(planned) is not { } plan)
        {
            return Conflict("Season " + season + " was saved, but this item no longer needs a move. Scan again to refresh the list.");
        }

        _issues.ReplacePlan(issue.Id, planned);
        if (plan.BlockingReasons.Length > 0)
        {
            return Conflict("Season " + season + " was saved, but the move still needs review: " + plan.BlockingReasons[0]);
        }

        _issues.TryTransitionIssueStatus(issue.Id, [IssueStatus.Detected], IssueStatus.Queued);
        var started = StartFix() is NoContentResult;
        return Ok("Season " + season + " saved. The move to '" + plan.Entries[0].Destination + "' is approved"
            + (started ? " and the fix run has started." : "; it runs with the next fix run."));
    }

    private static bool IsBlockedOnlyByUnknownSeason(Issue issue)
        => issue.Type is IssueType.Ungrouped or IssueType.Misplaced
            && MediaMovePlan.ReadIssue(issue) is { BlockingReasons.Length: > 0 } plan
            && plan.BlockingReasons.All(reason => reason.StartsWith("The season is unknown", StringComparison.Ordinal));

    private bool IsBlockedOnlyByParkedMove(Issue issue)
        => issue.Type is IssueType.Ungrouped or IssueType.Misplaced
            && MediaMovePlan.ReadIssue(issue) is { BlockingReasons.Length: > 0 } plan
            && plan.BlockingReasons.All(reason => reason.StartsWith("A move operation has reserved", StringComparison.Ordinal)
                || reason.StartsWith("Another operation or a previous move attempt reserves", StringComparison.Ordinal));

    /// <summary>
    /// Approves an issue: it is queued for the next fix run. Only legal from Detected —
    /// re-approving a queued or fixed row is a stale request from an out-of-date UI and returns
    /// 409 so the client refreshes rather than resurrecting an already-completed fix.
    /// </summary>
    /// <param name="id">The issue id.</param>
    /// <returns>No content on success; 404 for unknown ids; 409 when the row isn't Detected.</returns>
    [HttpPost("Issues/{id}/Approve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult ApproveIssue([FromRoute] long id)
    {
        var issue = _issues.GetIssue(id);
        if (issue is null)
        {
            return NotFound();
        }

        if (issue.Status != IssueStatus.Detected)
        {
            return Conflict("Approve is only legal from Detected. Refresh the Issues tab to see its current status.");
        }

        var blocked = GetFixBlockedReason(Plugin.Instance?.Configuration ?? new PluginConfiguration(), issue);
        if (blocked is not null)
        {
            return Conflict(blocked);
        }

        // For FormatCompatibility issues in a mixed library, patch approvedInMixedLibrary=true
        // so FormatCompatibilityFixer will honor the user's explicit approval.
        if (issue is { Type: IssueType.FormatCompatibility, DetailsJson: { } json })
        {
            var patched = PatchApprovedInMixedLibrary(json);
            if (patched is not null)
            {
                _issues.UpdateDetails(id, patched);
            }
        }

        return _issues.TryTransitionIssueStatus(id, ApproveFromDetected, IssueStatus.Queued) switch
        {
            StatusTransitionResult.Applied => NoContent(),
            StatusTransitionResult.NotFound => NotFound(),
            _ => Conflict("Approve is only legal from Detected. The issue is no longer in that state — refresh the Issues tab to see its current status.")
        };
    }

    /// <summary>
    /// Creates a Detected issue from the file-browser context menu. Request fields and allowed types
    /// documented on <see cref="ManualIssueRequest"/> and <see cref="ManualIssueType"/>. Routes through
    /// the same <see cref="Data.IssueStore.ReplaceDetectedIssues"/> path scanners use, so Queued and
    /// Dismissed rows for the same (type, path) suppress the insert (user asked never to see it, or
    /// already approved it — manual add respects both).
    /// </summary>
    /// <param name="request">The manual-issue payload.</param>
    /// <returns>201 with the new issue id, or an appropriate error status.</returns>
    [HttpPost("Issues/Manual")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> CreateManualIssue([FromBody] ManualIssueRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest("Request body is missing a Path.");
        }

        // Guard: path must be inside a configured library root. Same policy every mutating file op
        // uses — see FileBrowserController. Keeps us from recording issues against arbitrary disk paths.
        try
        {
            if (!Path.IsPathFullyQualified(request.Path))
            {
                return BadRequest("An absolute file path is required.");
            }

            request.Path = Path.GetFullPath(request.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BadRequest("That file path is invalid.");
        }

        if (!_libraryGuard.IsInsideLibrary(request.Path))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "That path is outside your library folders.");
        }

        var item = _libraryManager.FindByPath(request.Path, false);
        if (item is null)
        {
            return NotFound("Jellyfin doesn't have that file indexed — run a Jellyfin library scan first.");
        }

        if (item.IsFolder)
        {
            return BadRequest("Select a file, rather than a folder.");
        }

        var (issue, problem) = BuildManualIssue(request, item.Id);
        if (issue is null)
        {
            return BadRequest(problem);
        }

        if (_protection.IsScannerBlocked(issue.Path, issue.Type) || _protection.IsFixerBlocked(issue.Path, issue.Type))
        {
            return Conflict("This file is protected. Remove its protection in Settings before adding it to Issues.");
        }

        // Pass scannedPaths so ReplaceDetectedIssues scopes its DELETE to this single path.
        _issues.ReplaceDetectedIssues(issue.Type, new[] { issue }, scannedPaths: new[] { issue.Path });

        // Round-trip to read back the id (the single-item insert either wrote exactly one row or was
        // suppressed by an existing Queued/Dismissed row).
        var existing = _issues.GetIssues(type: issue.Type, status: null);
        long id = 0;
        foreach (var row in existing)
        {
            if (row.Path == issue.Path && (row.Status == Data.IssueStatus.Detected || row.Status == Data.IssueStatus.Queued))
            {
                id = row.Id;
                break;
            }
        }

        if (id == 0)
        {
            return Conflict("This issue was dismissed. Restore it from the dismissed Issues list before adding it again.");
        }

        return StatusCode(StatusCodes.Status201Created, new { Id = id });
    }

    /// <summary>
    /// Returns a new DetailsJson with <c>approvedInMixedLibrary: true</c> added when the input
    /// has <c>mixedLibrary: true</c>; returns null when no patch is needed.
    /// </summary>
    /// <param name="detailsJson">The existing DetailsJson string to inspect and patch.</param>
    /// <returns>Patched JSON string, or null when no patch is required.</returns>
    internal static string? PatchApprovedInMixedLibrary(string detailsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("mixedLibrary", out var m) || m.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            // Already approved — no-op.
            if (root.TryGetProperty("approvedInMixedLibrary", out var a) && a.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            // Copy all existing properties, append the flag.
            using var ms = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                prop.WriteTo(writer);
            }

            writer.WriteBoolean("approvedInMixedLibrary", true);
            writer.WriteEndObject();
            writer.Flush();
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the <see cref="Data.Issue"/> that <see cref="ManualIssueRequest"/> describes, or returns
    /// a human-readable problem message when the request is invalid for the picked type. Pure function —
    /// no DB, no library manager — so it unit-tests directly.
    /// </summary>
    /// <param name="request">The deserialized request body. Must be non-null.</param>
    /// <param name="itemId">The Jellyfin item id the caller resolved from <see cref="ManualIssueRequest.Path"/>.</param>
    /// <returns>A tuple of (issue, problem). Exactly one is non-null.</returns>
    public static (Data.Issue? Issue, string? Problem) BuildManualIssue(
        ManualIssueRequest request,
        Guid itemId)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Path))
        {
            return (null, "Request body is missing a Path.");
        }

        var now = DateTime.UtcNow;
        switch (request.Type)
        {
            case ManualIssueType.Quality:
                if (!MediaFormats.Video.Contains(Path.GetExtension(request.Path)) || MediaFormats.NonProbable.Contains(Path.GetExtension(request.Path)))
                {
                    return (null, "Re-encoding to shrink is available for video files only.");
                }

                return (new Data.Issue
                {
                    Type = Data.IssueType.Quality,
                    ItemId = itemId,
                    Path = request.Path,
                    DetailsJson = "{}",
                    SuggestedFix = "Re-encode to the configured quality ceiling.",
                    SizeSavings = 0,
                    Status = Data.IssueStatus.Detected,
                    DetectedAtUtc = now
                }, null);

            case ManualIssueType.FormatCompatibility:
                var row = FormatMatrix.Classify(request.Path);
                if (row is null || string.IsNullOrEmpty(row.TargetExtension) || row.ConverterKey == "none")
                {
                    var ext = Path.GetExtension(request.Path);
                    return (null, "No converter is registered for '" + ext + "' files.");
                }

                var fcDetails = JsonSerializer.Serialize(new
                {
                    converterKey = row.ConverterKey,
                    targetExt = row.TargetExtension
                });
                return (new Data.Issue
                {
                    Type = Data.IssueType.FormatCompatibility,
                    ItemId = itemId,
                    Path = request.Path,
                    DetailsJson = fcDetails,
                    SuggestedFix = "Convert " + Path.GetExtension(request.Path) + " to " + row.TargetExtension + ".",
                    SizeSavings = 0,
                    Status = Data.IssueStatus.Detected,
                    DetectedAtUtc = now
                }, null);

            default:
                return (null, "Unknown manual issue type: " + request.Type + ".");
        }
    }

    /// <summary>
    /// Dismisses an issue: it will not be re-reported by future scans. Legal from Detected or
    /// Queued (change of mind before the fix runs). Refuses Dismissed (already ignored) and Fixed
    /// (the file has already been touched) so the audit trail can't be silently rewritten.
    /// </summary>
    /// <param name="id">The issue id.</param>
    /// <returns>No content on success; 404 for unknown ids; 409 when the row is Dismissed or Fixed.</returns>
    [HttpPost("Issues/{id}/Dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult DismissIssue([FromRoute] long id)
    {
        return _issues.TryTransitionIssueStatus(id, DismissFromOpen, IssueStatus.Dismissed) switch
        {
            StatusTransitionResult.Applied => NoContent(),
            StatusTransitionResult.NotFound => NotFound(),
            _ => Conflict("Dismiss is only legal from Detected or Queued. The issue has already been fixed or dismissed — refresh the Issues tab.")
        };
    }

    /// <summary>
    /// Reverts a queued or dismissed issue back to Detected so the user can change their mind
    /// after clicking Approve or Dismiss by accident. Refuses when the issue has already Fixed
    /// (the file has already been touched) — the History tab's Restore is the right path for that.
    /// The transition is atomic: the source-status check happens inside the UPDATE so a concurrent
    /// fixer completion or dismiss can't slip through a read-then-write race.
    /// </summary>
    /// <param name="id">The issue id.</param>
    /// <returns>No content on success; 404 for unknown ids; 409 for any status other than Queued or Dismissed.</returns>
    [HttpPost("Issues/{id}/Revert")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult RevertIssue([FromRoute] long id)
    {
        return _issues.TryTransitionIssueStatus(id, RevertFromQueuedOrDismissed, IssueStatus.Detected) switch
        {
            StatusTransitionResult.Applied => NoContent(),
            StatusTransitionResult.NotFound => NotFound(),
            _ => Conflict("Can only revert issues in Queued or Dismissed state. If the fix has already run, use the History tab's Restore instead.")
        };
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
        if (fixTask is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "The MediaDash fix task is unavailable. Check Jellyfin's startup log and Dashboard → Scheduled Tasks for 'Apply approved fixes'.");
        }

        if (fixTask.State != TaskState.Idle)
        {
            return Conflict("The fix task is already running or stopping. Wait for it to finish, then try again.");
        }

        _runState.ArmBypassIdleCheck(ScheduledTasks.RunRole.Fix);
        try
        {
            var execution = _taskManager.Execute(fixTask, new TaskOptions());
            if (execution.IsCompleted)
            {
                execution.GetAwaiter().GetResult();
            }
            else
            {
                // Execute represents the entire run; observe dispatch faults without holding the HTTP request open.
                var runState = _runState;
                _ = execution.ContinueWith(
                    failed => RecordFixDispatchFailure(runState, failed.Exception!.GetBaseException()),
                    default,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            RecordFixDispatchFailure(_runState, ex);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Could not start the fix task. See the Errors tab and Jellyfin log for details.");
        }

        return NoContent();
    }

    private static void RecordFixDispatchFailure(RunState runState, Exception error)
    {
        runState.ConsumeBypassIdleCheck(RunRole.Fix);
        runState.SetPauseReason(RunRole.Fix, "Could not start the fix task. See the Errors tab and Jellyfin log for details.");
        Diagnostics.Record("FixTask.Start", "Could not start the fix task: " + error.Message);
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
        _runState.SetFixIgnoreActivity();
        return NoContent();
    }

    /// <summary>
    /// Clears every persisted Errors-tab diagnostic whose source starts with <paramref name="prefix"/>.
    /// Called by the SMART pill's "Disable SMART monitoring" context-menu item (prefix
    /// <c>SmartHealth.</c>) so existing SmartHealth.* rows vanish immediately after the user turns
    /// the subsystem off; the paired config flag prevents new ones from ever being recorded.
    /// Generic-by-design so other subsystem-off toggles can reuse it without new endpoints.
    /// </summary>
    /// <param name="prefix">Source prefix to purge (e.g. <c>SmartHealth.</c>). Required.</param>
    /// <returns>No content.</returns>
    [HttpPost("Diagnostics/PurgeByPrefix")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult PurgeDiagnosticsByPrefix([FromQuery] string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return BadRequest("prefix is required");
        }

        Diagnostics.RemoveBySourcePrefix(prefix);
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
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (type is not null && GetFixBlockedReason(config, type.Value) is { } blocked)
        {
            return Conflict(blocked);
        }

        // Consent gate: blocking-warning rows (bitmap-sub removals, low-space overrides, etc.)
        // require an explicit per-row Approve from the Issues tab and must NOT be auto-approved
        // by a bulk "Approve all of type X" click — the user can't see the warning bubble on a
        // collapsed row. Mirror the FixTask auto-queue pattern: collect the just-transitioned IDs,
        // then run RollbackAutoQueuedBlockingWarnings against that exact set so blocking rows
        // bounce back to Detected. The count we return excludes the reverts so the toast tells
        // the truth about what's actually queued to run.
        var enabledLocations = _libraryGuard?.GetEnabledLibraryLocations(config.EnabledLibraries);
        var candidates = _issues.GetIssues(type, IssueStatus.Detected);
        var ids = candidates
            .Where(issue => GetFixBlockedReason(config, issue, enabledLocations) is null)
            .Select(issue => issue.Id).ToList();
        if (type is not null && ids.Count == 0 && candidates.Count > 0)
        {
            return Conflict(GetFixBlockedReason(config, candidates[0], enabledLocations));
        }

        var autoQueuedIds = _issues.BulkUpdateOpenIssueStatus(ids, IssueStatus.Queued);

        var reverted = _issues.RollbackAutoQueuedBlockingWarnings(autoQueuedIds);
        return autoQueuedIds.Count - reverted;
    }

    /// <summary>
    /// Dismisses every Queued issue whose current path isn't inside a MediaDash-enabled library
    /// location. Companion to the Overview "Last fix run" summary's <c>SkippedLibraryDisabled</c>
    /// count: gives users a one-click way to clean up a queue that outlives library-selection
    /// changes. Dismissed rows are recoverable via the standard Revert flow, so this is not
    /// destructive. Silently no-ops on an empty queue or when every queued row is in scope.
    /// </summary>
    /// <returns>The number of queued rows just dismissed.</returns>
    [HttpPost("Issues/DismissStaleQueued")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<int> DismissStaleQueued()
    {
        var config = Plugin.Instance!.Configuration;
        var enabledLocations = _libraryGuard.GetEnabledLibraryLocations(config.EnabledLibraries);
        var staleIds = _issues.GetIssues(status: IssueStatus.Queued)
            .Where(i => !_libraryGuard.IsIssueInEnabledLocations(i, enabledLocations))
            .Select(i => i.Id)
            .ToList();
        if (staleIds.Count == 0)
        {
            return Ok(0);
        }

        var dismissed = _issues.BulkUpdateOpenIssueStatus(staleIds, IssueStatus.Dismissed);
        return Ok(dismissed.Count);
    }

    /// <summary>Bulk-updates a set of open issues (Approve → Queued, Dismiss → Dismissed) or reverts a dismissed set.</summary>
    /// <param name="request">Bulk action + ids to apply it to.</param>
    /// <returns>The number of rows actually transitioned.</returns>
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

        // Revert (bulk un-ignore) takes a different path — source status filter is Dismissed, not
        // Detected/Queued — so branch before the target lookup.
        if (string.Equals(request.Action, "Revert", StringComparison.OrdinalIgnoreCase))
        {
            return _issues.BulkRevertDismissedIssues(request.Ids);
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
            return BadRequest("Action must be 'Approve', 'Dismiss', or 'Revert'.");
        }

        // Guarded transition — only Detected/Queued rows are touched. Prevents a stale client
        // snapshot from un-ignoring items via bulk-approve, or un-fixing them via bulk-dismiss.
        // For the Approve path, blocking-warning rows must not bypass the per-row consent gate —
        // the bulk endpoint's "approve all shown" click can catch rows whose warning bubble was
        // never expanded, so run the RollbackAutoQueuedBlockingWarnings pass against the exact
        // just-transitioned IDs (same pattern the FixTask auto-queue uses). Dismiss action has no
        // consent gate — bulk-dismissing a blocking-warning row is a valid "never touch this" click.
        IReadOnlyList<long> ids = request.Ids;
        if (target == IssueStatus.Queued)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var requested = request.Ids.ToHashSet();
            var candidates = _issues.GetIssues(status: IssueStatus.Detected).Where(i => requested.Contains(i.Id)).ToList();
            var enabledLocations = _libraryGuard?.GetEnabledLibraryLocations(config.EnabledLibraries);
            ids = candidates.Where(i => GetFixBlockedReason(config, i, enabledLocations) is null).Select(i => i.Id).ToList();
            if (ids.Count == 0 && candidates.Count > 0)
            {
                return Conflict(GetFixBlockedReason(config, candidates[0], enabledLocations));
            }
        }

        var transitioned = _issues.BulkUpdateOpenIssueStatus(ids, target);
        if (target == IssueStatus.Queued)
        {
            var reverted = _issues.RollbackAutoQueuedBlockingWarnings(transitioned);
            return transitioned.Count - reverted;
        }

        return transitioned.Count;
    }

    private string? GetFixBlockedReason(
        PluginConfiguration config,
        Issue issue,
        IReadOnlyList<string>? enabledLocations = null,
        Dictionary<IssueType, string?>? typeReasons = null,
        Func<string, IssueType, bool>? protectionCheck = null)
        => GetFixBlockedReason(config, issue, out _, enabledLocations, typeReasons, protectionCheck);

    private string? GetFixBlockedReason(
        PluginConfiguration config,
        Issue issue,
        out bool requiresRescan,
        IReadOnlyList<string>? enabledLocations = null,
        Dictionary<IssueType, string?>? typeReasons = null,
        Func<string, IssueType, bool>? protectionCheck = null)
    {
        requiresRescan = false;
        if (issue.RequiresRescan)
        {
            requiresRescan = true;
            return "This finding changed after a move. Scan again and review the current finding before approval.";
        }

        if (issue.IsReportOnly)
        {
            return "This finding is report-only. No automatic repair is available; Ignore it or manage the file from Files.";
        }

        var typeReason = typeReasons is null ? GetFixBlockedReason(config, issue.Type) : typeReasons[issue.Type];
        if (typeReason is not null)
        {
            return typeReason;
        }

        if (typeReasons is null && _fixers is not null && !_fixers.Any(fixer => fixer.CanFix(issue.Type)))
        {
            return "No fixer is available for this issue type on this server.";
        }

        if (issue.Type is IssueType.Ungrouped or IssueType.Misplaced)
        {
            if (MediaMovePlan.ReadIssue(issue) is not { } movePlan)
            {
                requiresRescan = true;
                return "Re-scan this item before approval so you can review its complete, current move preview.";
            }

            if (movePlan.BlockingReasons.Length > 0)
            {
                // No setting changes a move plan's blockers: offer a fresh scan (or Move anyway, below).
                requiresRescan = !IsBlockedOnlyByParkedMove(issue) && !IsBlockedOnlyByUnknownSeason(issue);
                return "This move needs review: " + movePlan.BlockingReasons[0];
            }

            if (MediaMovePlanner.PolicyStamp(config, issue.Type) != movePlan.PolicyStamp)
            {
                requiresRescan = true;
                return "Move settings changed after this preview. Scan again and review the new plan before approval.";
            }
        }

        if (issue.Type == IssueType.OrphanedDebris && OrphanCleanupFixer.ReadKind(issue.DetailsJson) == Scanners.OrphanCleanupScanner.KindOrphanMetadata
            && !config.OrphanScanMetadata)
        {
            return "Orphan metadata cleanup is disabled. Enable it in Settings, then scan again before approving this folder.";
        }

        if (_libraryGuard is not null && !_libraryGuard.IsIssueInEnabledLocations(issue, enabledLocations ?? _libraryGuard.GetEnabledLibraryLocations(config.EnabledLibraries)))
        {
            return "This issue's library or artwork owner is not enabled. Check Settings → Libraries, then scan again.";
        }

        if ((protectionCheck?.Invoke(issue.Path, issue.Type) ?? _protection?.IsFixerBlocked(issue.Path, issue.Type)) == true)
        {
            return "This file is protected from modification. Review its protection in Settings before approving it.";
        }

        if (issue.Type == IssueType.OrphanedDebris && OrphanCleanupFixer.ReadKind(issue.DetailsJson) == Scanners.OrphanCleanupScanner.KindOrphanMetadata
            && _protection?.IsAnyDescendantProtected(issue.Path, issue.Type) == true)
        {
            return "This metadata folder contains protected content. Review its protection in Settings before approving it.";
        }

        if (issue.Type == IssueType.CorruptArtwork && _protection is not null)
        {
            try
            {
                var owner = _libraryManager.GetItemById(issue.ItemId);
                if (owner is null || _protection.IsFixerBlocked(owner.Path, issue.Type))
                {
                    return "The artwork owner is unavailable or protected from modification. Review its protection in Settings before approving it.";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return "The artwork owner could not be verified. Scan again before approving this issue.";
            }
        }

        return null;
    }

    private static string? GetFixBlockedReason(PluginConfiguration config, IssueType type)
    {
        var policy = FixPolicyEvaluator.Evaluate(config, type, IssueStatus.Detected, FixIntent.Approved, withinEnabledLibrary: true, autoQueueRestoredBlocked: false, hasApprovedCompanionOnPath: false);
        if (policy.Allowed)
        {
            return null;
        }

        return policy.Reason switch
        {
            FixReason.TypeDisabled when config.GetFixMode(type) == FixMode.Off => "This type is Off. In Settings → What to fix, choose Ask me first and save, then approve the issue.",
            FixReason.TypeDisabled => "This fixer is disabled. Enable it in Fix order, then approve the issue.",
            _ => "No fix is available for this issue type."
        };
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
        var libraryLocations = GetHistoryLibraryLocations();

        return Ok(_history.GetHistory().Select(entry =>
        {
            var dto = HistoryDto.FromEntry(entry);
            if (entry.MoveOperationId is { } moveId && _db.Moves.Read(moveId) is { } operation)
            {
                dto.MoveState = operation.State;
                dto.CanUndoMove = MoveOperationDto.FromOperation(operation).CanUndo;
            }

            dto.Library = ResolveLibraryName(entry.Path, libraryLocations);
            return dto;
        }).ToList());
    }

    /// <summary>Gets durable moves, including interrupted work without a History row.</summary>
    /// <returns>Recent operations with incomplete work first.</returns>
    [HttpGet("Moves")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MoveOperationDto>> GetMoves()
        => Ok(_db.Moves.GetVisible(Plugin.Instance!.Configuration.HistoryHiddenBeforeUtcTicks).Select(MoveOperationDto.FromOperation).ToList());

    /// <summary>Undoes a complete recorded move after verifying current payloads and original-path vacancy.</summary>
    /// <param name="operationId">Move selected by the administrator.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>Real completion, preview, or a visible refusal preserving existing payloads.</returns>
    [HttpPost("Moves/{operationId}/Undo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FixResult>> UndoMove([FromRoute] string operationId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(operationId, "N", out _))
        {
            return BadRequest("Select a recorded move operation.");
        }

        if (_moves is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Move recovery is unavailable on this host.");
        }

        var result = await _moves.UndoAsync(operationId, null, cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result) : Conflict(result);
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
        var libraryLocations = GetHistoryLibraryLocations();

        long total = 0;
        var byLibrary = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (path, bytesFreed) in _history.GetLifetimePathTotals())
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

    private List<(string Name, string Root)> GetHistoryLibraryLocations()
    {
        var locations = new List<(string Name, string Root)>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            foreach (var location in folder.Locations ?? [])
            {
                try
                {
                    locations.Add((folder.Name, Path.TrimEndingDirectorySeparator(Path.GetFullPath(location))));
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    Diagnostics.Record("History.LibraryLocation", "Could not resolve a library location: " + ex.Message);
                }
            }
        }

        return locations;
    }

    private static string ResolveLibraryName(string path, List<(string Name, string Root)> libraries)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
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
    /// Advances the history-hidden watermark so existing history rows and recorded moves disappear from the History tab.
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
        var outcome = _restoreService.RestoreFromHistory(id, force);
        return MapRestoreOutcome(outcome);
    }

    // Thin forwarder to <see cref="RestoreService.ResolveNonCollidingRestorePath"/>. The real
    // implementation moved to RestoreService in 05-B so all three restore routes share the
    // directory-aware suffix resolver. Existing tests reach the resolver via this symbol; the
    // forwarder keeps the test-visible surface stable without duplicating logic.
    internal static string ResolveNonCollidingRestorePath(string originalPath)
        => RestoreService.ResolveNonCollidingRestorePath(originalPath);

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
                    using var entries = System.IO.Directory.EnumerateFileSystemEntries(location).GetEnumerator();
                    _ = entries.MoveNext();
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
            using var entries = System.IO.Directory.EnumerateFileSystemEntries(effectiveRoot).GetEnumerator();
            _ = entries.MoveNext();
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
    /// Lists every persistent "Never modify" protection rule. Roadmap i-110-protected-media.
    /// </summary>
    /// <returns>The full ruleset in insertion order.</returns>
    [HttpGet("Protection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ProtectionRuleDto>> ListProtection()
    {
        var rules = _protection.GetRules()
            .Select(r => new ProtectionRuleDto
            {
                Id = r.Id,
                Kind = r.Kind,
                Target = r.Target,
                ScannersMask = r.ScannersMask,
                FixersMask = r.FixersMask,
                Note = r.Note,
                CreatedAtUtc = r.CreatedAtUtc,
            })
            .ToList();
        return Ok(rules);
    }

    /// <summary>Creates a new "Never modify" rule for a folder or library.</summary>
    /// <param name="request">The rule to add.</param>
    /// <returns>The created rule with its assigned id.</returns>
    [HttpPost("Protection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<ProtectionRuleDto> CreateProtection([FromBody] ProtectionRuleDto request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Target))
        {
            return BadRequest(new { Error = "Target is required." });
        }

        var kind = string.Equals(request.Kind, "library", StringComparison.OrdinalIgnoreCase) ? "library" : "folder";
        if (kind == "folder" && !Path.IsPathFullyQualified(request.Target))
        {
            return BadRequest(new { Error = "Folder target must be an absolute path." });
        }

        // A rule that blocks nothing is a no-op stored forever — reject to keep the list honest.
        if (string.IsNullOrWhiteSpace(request.ScannersMask) && string.IsNullOrWhiteSpace(request.FixersMask))
        {
            return BadRequest(new { Error = "At least one of ScannersMask or FixersMask must be non-empty (use '*' to block all)." });
        }

        // Reject unrecognised mask tokens up front. An unknown IssueType name (e.g. "NotARealIssue")
        // otherwise persists as an ineffective rule that never matches anything.
        if (ValidateMask(request.ScannersMask, "ScannersMask") is { } scanErr)
        {
            return scanErr;
        }

        if (ValidateMask(request.FixersMask, "FixersMask") is { } fixErr)
        {
            return fixErr;
        }

        // Canonicalize folder targets so `/`, `\`, `.` segments and trailing separators all collapse
        // to a single stored form. ProtectionService.PathIsUnder now canonicalizes at compare time
        // too — this store-side pass keeps the persisted list clean going forward.
        string canonicalFolderTarget = string.Empty;
        if (kind == "folder")
        {
            try
            {
                canonicalFolderTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Target));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return BadRequest(new { Error = "Folder target is not a valid absolute path: " + ex.Message });
            }
        }
        else
        {
            // Library-kind: the target must resolve to a real virtual folder id. Without this check
            // a rule for "no-such-library" persists forever and silently protects nothing.
            var idLookup = Scanners.VirtualFolderIdentity.BuildIdLookup(_libraryManager);
            var known = _libraryManager.GetVirtualFolders()
                .Select(f => Scanners.VirtualFolderIdentity.GetId(f, idLookup))
                .Any(id => !string.IsNullOrEmpty(id) && string.Equals(id, request.Target, StringComparison.OrdinalIgnoreCase));
            if (!known)
            {
                return BadRequest(new { Error = "Library target '" + request.Target + "' does not match any known library id." });
            }
        }

        var rule = new Data.ProtectionRule
        {
            Kind = kind,
            Target = kind == "folder" ? canonicalFolderTarget : request.Target,
            ScannersMask = (request.ScannersMask ?? string.Empty).Trim(),
            FixersMask = (request.FixersMask ?? string.Empty).Trim(),
            Note = (request.Note ?? string.Empty).Trim(),
            CreatedAtUtc = DateTime.UtcNow,
        };

        var id = _db.Protection.Add(rule);
        _protection.Invalidate();

        return Ok(new ProtectionRuleDto
        {
            Id = id,
            Kind = rule.Kind,
            Target = rule.Target,
            ScannersMask = rule.ScannersMask,
            FixersMask = rule.FixersMask,
            Note = rule.Note,
            CreatedAtUtc = rule.CreatedAtUtc,
        });
    }

    private BadRequestObjectResult? ValidateMask(string? mask, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(mask))
        {
            return null;
        }

        foreach (var raw in mask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*")
            {
                continue;
            }

            if (!Enum.TryParse<IssueType>(raw, ignoreCase: true, out _))
            {
                return BadRequest(new { Error = fieldName + " contains unknown issue type '" + raw + "'. Use '*' or one of the values from GET /MediaDash/Protection/IssueTypes." });
            }
        }

        return null;
    }

    /// <summary>
    /// Lists every <see cref="IssueType"/> the UI can offer as a per-type block target. Ordered
    /// by ordinal so the enum's stable numeric values also drive the display order.
    /// </summary>
    /// <returns>Enum names in declaration order.</returns>
    [HttpGet("Protection/IssueTypes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> GetProtectableIssueTypes()
    {
        var names = Enum.GetValues<IssueType>()
            .OrderBy(v => (int)v)
            .Select(v => v.ToString())
            .ToList();
        return Ok(names);
    }

    /// <summary>Removes a "Never modify" rule by id.</summary>
    /// <param name="id">The rule id returned by <see cref="ListProtection"/> or <see cref="CreateProtection"/>.</param>
    /// <returns>No content on success; 404 when the id is unknown.</returns>
    [HttpDelete("Protection/{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteProtection(long id)
    {
        if (!_db.Protection.Remove(id))
        {
            return NotFound();
        }

        _protection.Invalidate();
        return NoContent();
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
        // Honour Settings → Libraries → Which libraries to scan. Empty list = all (existing
        // ScanTask semantic); non-empty = restrict the Overview breakdown to the user's chosen set.
        var enabledLibraries = Plugin.Instance!.Configuration.EnabledLibraries ?? Array.Empty<string>();
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
            .Where(f => enabledLibraries.Length == 0
                || enabledLibraries.Contains(f.Id, StringComparer.OrdinalIgnoreCase))
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

        // E2E audit E2E-05: Audio, Book and Photo were missing from this query so the Overview
        // library breakdown reported Music, Books and Photos libraries as "0 items / 0 B" even
        // when they held real content. Added so the breakdown row matches the Jellyfin
        // library's actual contents.
        var items = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            IncludeItemTypes = new[]
            {
                Jellyfin.Data.Enums.BaseItemKind.Movie,
                Jellyfin.Data.Enums.BaseItemKind.Episode,
                Jellyfin.Data.Enums.BaseItemKind.MusicVideo,
                Jellyfin.Data.Enums.BaseItemKind.AudioBook,
                Jellyfin.Data.Enums.BaseItemKind.Audio,
                Jellyfin.Data.Enums.BaseItemKind.Book,
                Jellyfin.Data.Enums.BaseItemKind.Photo
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

            var matchedFolder = folders
                .SelectMany(f => f.Locations.Select(loc => new { Folder = f, Location = loc }))
                .Where(f => LibraryGuard.IsUnder(item.Path, f.Location))
                .OrderByDescending(f => f.Location.Length)
                .FirstOrDefault();
            var stat = matchedFolder is null ? null : stats[matchedFolder.Folder.Id];

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
        var byRecyclePath = _history.GetActiveRecycleEntries()
            .Where(h => !h.Restored && !string.IsNullOrEmpty(h.RecyclePath))
            .GroupBy(h => h.RecyclePath!, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.FixedAtUtc).First(), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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
                    item.TechnicalDetail = h.TechnicalDetail;
                    item.RestoreHint = RecycleReasonMapper.RestoreHintFor(RecycleProvenance.History, h.Path);
                }
                else if (!string.IsNullOrEmpty(e.OriginalPath))
                {
                    // Manifest-only: origin sidecar remembers the source path but no HistoryEntry
                    // joined to this bin path. Post-1.0.8.0 this only happens to older-version
                    // auto-fix recycles that predate the history-write path — the Files-tab Delete
                    // endpoint now writes an IssueType.ManualDelete history row so real manual
                    // deletes land in the history-match branch above. Wording stays neutral so a
                    // user upgrading from an old build isn't accused of manual deletes they never made.
                    item.OriginalPath = e.OriginalPath;
                    item.BinPath = e.BinPath;
                    item.Provenance = RecycleProvenance.Manifest;
                    item.Reason = "Recycled by MediaDash — origin not recorded";
                    item.ActionText = "Recycled by an older MediaDash build that didn't record which fix removed it. Restore uses the origin sidecar to send it back.";
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
        // F-207 / issue #26: the endpoint distinguishes "you sent the wrong shape" (400) from
        // "you sent the right shape but the target can't be restored right now" (409).

        // Batch path: {BinPaths: [...]} with 2+ entries. Always returns 200 with per-entry outcomes
        // so a bad path halfway through doesn't hide the successes. Single-item batches fall
        // through to the single-item path below so backward-compatible clients still get a
        // RestoreResult body.
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

                var outcome = _restoreService.RestoreManifestEntryFromListing(candidatePath, entries);
                body.Results.Add(new BatchRestoreEntry
                {
                    BinPath = candidatePath,
                    Success = outcome.Success,
                    RestoredTo = outcome.RestoredTo,
                    Suffixed = outcome.Suffixed,
                    Error = outcome.Success ? null : outcome.TechnicalDetail
                });
                if (outcome.Success)
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

        var singleOutcome = _restoreService.RestoreManifestEntry(binPath);
        return MapRestoreOutcome(singleOutcome);
    }

    // Shared HTTP mapping for RestoreOutcome (single-item shapes). RestoreOptimizedCopy has its
    // own 204/404/409 shape and doesn't use this; batch uses the outcome fields directly.
    private ActionResult MapRestoreOutcome(RestoreOutcome outcome)
    {
        if (outcome.Success)
        {
            return Ok(new RestoreResult { RestoredTo = outcome.RestoredTo!, Suffixed = outcome.Suffixed });
        }

        if (outcome.IsNotFound)
        {
            return NotFound();
        }

        return Conflict(outcome.TechnicalDetail ?? "Restore refused.");
    }

    /// <summary>
    /// Permanently deletes a single recycled file, identified by bin path. The path is whitelisted
    /// against <see cref="Fixers.RecycleBin.ListContents"/> so callers can't pass an arbitrary host
    /// path. Irreversible — the UI confirms with the user before calling.
    /// </summary>
    /// <param name="request">Body naming the bin path to delete.</param>
    /// <returns>200 on success; 400 when the body lacks BinPath; 404 when the path isn't a known bin entry.</returns>
    [HttpPost("RecycleBin/Items/Delete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "BinPath is whitelisted against ListContents() before deletion — arbitrary host paths cannot pass the equality check.")]
    public ActionResult DeleteBinItem([FromBody] BinDeleteRequest request)
    {
        if (string.IsNullOrEmpty(request.BinPath))
        {
            return BadRequest("Missing 'BinPath'. Send the value from RecycleBinItem.BinPath (GET /MediaDash/RecycleBin/Items).");
        }

        var entry = _recycleBin.ListContents().FirstOrDefault(e =>
            string.Equals(e.BinPath, request.BinPath, StringComparison.OrdinalIgnoreCase));
        if (entry.BinPath is null)
        {
            return NotFound();
        }

        try
        {
            _recycleBin.DeleteFile(entry.BinPath);
        }
        catch (FileNotFoundException)
        {
            // Already gone (raced a purge / another delete). Treat as success — the caller wanted it gone.
        }
        catch (UnauthorizedAccessException ex)
        {
            Diagnostics.Record("RecycleBin.DeletePermissionDenied", "Could not delete '" + entry.BinPath + "': " + ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, "Permission denied deleting the file. Check the Jellyfin user has write access to the recycle bin folder.");
        }
        catch (IOException ex)
        {
            Diagnostics.Record("RecycleBin.DeleteIOError", "Could not delete '" + entry.BinPath + "': " + ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, "Could not delete the file: " + ex.Message);
        }

        return Ok();
    }

    /// <summary>
    /// Kicks off a background empty of the recycle bin. Returns immediately with the current progress so the
    /// UI can poll <c>RecycleBin</c> for a bar; older builds ran this synchronously and appeared frozen for
    /// large bins.
    /// </summary>
    /// <param name="everywhere">When true, also empties every older recycle location MediaDash used (see <c>RecycleBin/EmptyPlan</c>).</param>
    /// <returns>The recycle bin state with <c>IsEmptying=true</c> when the run started.</returns>
    [HttpPost("RecycleBin/Empty")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RecycleBinInfo> EmptyRecycleBin([FromQuery] bool everywhere = false)
    {
        var alreadyRunning = _recycleBin.GetEmptyingProgress().IsRunning;
        if (!alreadyRunning)
        {
            var history = _history;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (everywhere)
                    {
                        _recycleBin.EmptyEverywhere(history);
                    }
                    else
                    {
                        _recycleBin.EmptyAll();
                    }
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
    /// Every recycle location MediaDash created files in (current bin, default bin, earlier bin paths
    /// and batches from older MediaDash versions), with what "empty every recycle bin" would delete.
    /// Read-only; the Recycle bin tab shows it before asking which empty the user wants.
    /// </summary>
    /// <returns>The plan.</returns>
    [HttpGet("RecycleBin/EmptyPlan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RecycleBinEmptyPlan> GetRecycleBinEmptyPlan()
    {
        var locations = _recycleBin.PlanEmptyEverywhere(_history)
            .Select(l => new RecycleBinEmptyPlanLocation { RootPath = l.RootPath, BatchCount = l.Batches.Count, SizeBytes = l.SizeBytes, IsCurrent = l.IsCurrent })
            .OrderByDescending(l => l.IsCurrent)
            .ToList();
        return new RecycleBinEmptyPlan
        {
            Locations = locations,
            TotalBatches = locations.Sum(l => l.BatchCount),
            TotalBytes = locations.Sum(l => l.SizeBytes),
            CurrentBinBatches = _recycleBin.CountEmptyAllBatches()
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
        return Ok(_recycleBin.DiscoverOtherBinRoots(_history)
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
        var known = _recycleBin.DiscoverOtherBinRoots(_history)
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
        if (_appHost.ApplicationVersion.Major < 12 || config.PostV12CleanupCompleted)
        {
            return Conflict("Post-upgrade cleanup is unavailable or already completed.");
        }

        if (_taskManager.ScheduledTasks.Any(task => task.ScheduledTask.Key == "RefreshLibrary" && task.State != TaskState.Idle))
        {
            return Conflict("Wait for the library scan to finish before cleanup.");
        }

        if (dismissOnly)
        {
            config.PostV12CleanupCompleted = true;
            Plugin.Instance!.SaveConfiguration();
            return Ok(new { OrphanedFoldersDeleted = 0, BytesFreed = 0L, Errors = Array.Empty<string>(), Dismissed = true });
        }

        var result = await _postUpgradeCleanup.RunAsync(config.DryRun).ConfigureAwait(false);
        _history.AddHistory(new Data.HistoryEntry
        {
            Type = Data.IssueType.Stale,
            Path = "(post-Jellyfin-12 cleanup)",
            Action = config.DryRun
                ? $"Preview: would remove {result.OrphanedFoldersDeleted} orphaned trickplay folder(s), reclaiming {result.BytesFreed} bytes."
                : $"Removed {result.OrphanedFoldersDeleted} orphaned trickplay folder(s) reclaiming {result.BytesFreed} bytes.",
            FixedAtUtc = DateTime.UtcNow,
            BytesFreed = result.BytesFreed,
            Success = result.Errors.Count == 0,
            WasDryRun = config.DryRun
        });
        // Only mark the sweep permanently completed when nothing failed. If some folders errored the
        // banner stays available so the user can retry, otherwise the failed folders would be
        // unreachable through the UI.
        if (!config.DryRun && result.Errors.Count == 0)
        {
            config.PostV12CleanupCompleted = true;
            Plugin.Instance!.SaveConfiguration();
        }

        return Ok(new { OrphanedFoldersDeleted = result.OrphanedFoldersDeleted, BytesFreed = result.BytesFreed, Errors = result.Errors, Dismissed = false, WasDryRun = config.DryRun });
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
        if (!_history.AcknowledgeHistoryEntry(historyId))
        {
            return NotFound();
        }

        Plugin.RedownloadWarnings = RedownloadDetector.Detect(_history, TimeSpan.FromDays(30));
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
        var outcome = _restoreService.RestoreOptimizedCopy(historyId, force);
        if (outcome.Success)
        {
            // Endpoint contract is 204 NoContent (not 200 + body). Also refresh the banner cache.
            Plugin.RedownloadWarnings = RedownloadDetector.Detect(_history, TimeSpan.FromDays(30));
            return NoContent();
        }

        if (outcome.IsNotFound)
        {
            return NotFound();
        }

        return Conflict(outcome.TechnicalDetail ?? "Could not restore optimized copy.");
    }

    /// <summary>
    /// Gets the latest scan and fix recordings while diagnostics mode is enabled.
    /// </summary>
    /// <returns>The latest recordings, or null for runs not recorded yet.</returns>
    [HttpGet("Diagnostics/LastScan")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetLastScanDiagnostics()
    {
        return Plugin.Instance?.Configuration.DiagnosticsEnabled == true
            ? Ok(new { Scan = ScheduledTasks.RunDiagnosticsRecording.Read(_db), Fix = ScheduledTasks.RunDiagnosticsRecording.Read(_db, "fix") })
            : NotFound();
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Composes the payload behind <c>GET /MediaDash/Status</c>. Extracted from
/// <see cref="MediaDashController.GetStatus"/> in 05-C.7 per plan §05-C: move status/library/history
/// aggregate composition out of the controller where it mixes volume and DB lookups; keep DTO
/// mapping and auth/status handling in HTTP layer.
/// </summary>
/// <remarks>
/// The controller keeps ownership of routing + auth + <c>ActionResult&lt;T&gt;</c> wrapping;
/// this service owns the actual gathering (issue summary + history aggregates + drive scan +
/// SMART probe + recycle-bin contents + config reads + plugin static state). Every consumer of
/// <see cref="StatusResponse"/> ships through <see cref="BuildStatus"/> so the payload shape stays
/// centralised.
/// </remarks>
public sealed class StatusQueryService
{
    private readonly MediaDashDb _db;
    private readonly IssueStore _issues;
    private readonly HistoryStore _history;
    private readonly Fixers.RecycleBin _recycleBin;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly RunState _runState;

    // Static so the reading cache and the one-measurement-in-flight-per-share guarantee hold across
    // the dashboard's 3-15 s polls, however many StatusQueryService instances the host creates.
    private static readonly UncVolumeProbe ShareProbe = new(MeasureShare, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));

    /// <summary>Initializes a new instance of the <see cref="StatusQueryService"/> class.</summary>
    /// <param name="db">Plugin DB — source of the Playability RepairSummary aggregation (history table).</param>
    /// <param name="issues">Issue-table store (05-C.2), source of open-issue summary + auto-queueable count.</param>
    /// <param name="history">History-table store (05-C.3), source of lifetime aggregates + failed-count badge.</param>
    /// <param name="recycleBin">Recycle bin — GetEffectiveRoot + GetContents.</param>
    /// <param name="libraryManager">Jellyfin library manager, used to enumerate library root paths for drive classification.</param>
    /// <param name="taskManager">Jellyfin task manager, used to look up the ScanTask and FixTask workers for progress reporting.</param>
    /// <param name="runState">Shared run-state holder (02-A), used to surface the fix-pause reason.</param>
    public StatusQueryService(
        MediaDashDb db,
        IssueStore issues,
        HistoryStore history,
        Fixers.RecycleBin recycleBin,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        RunState runState)
    {
        _db = db;
        _issues = issues;
        _history = history;
        _recycleBin = recycleBin;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _runState = runState;
    }

    /// <summary>Builds the full <see cref="StatusResponse"/> payload for the dashboard.</summary>
    /// <returns>The status response.</returns>
    public StatusResponse BuildStatus()
    {
        var summary = _issues.GetSummary();
        var scanTask = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is ScanTask);
        var fixTask = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is FixTask);
        long freeDisk = 0, totalDisk = 0;
        var drives = new List<DriveUsage>();

        // Roots that host a library folder — used to mark library drives and to keep the aggregated
        // FreeDiskBytes/TotalDiskBytes fields scoped to what MediaDash actually cares about.
        // FindDriveForPath is the right resolver on both Windows AND Linux — Path.GetPathRoot
        // returns "/" for every Linux path, which would collapse every library folder to root
        // and never match any mount point (leaving all drives unmarked as library drives on Linux,
        // which suppressed the SMART probe for every library drive there).
        var libraryLocations = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .ToList();
        var libraryRoots = libraryLocations
            .Select(l => Fixers.RecycleBin.FindDriveForPath(l))
            .Where(d => d is not null)
            .Select(d => Path.TrimEndingDirectorySeparator(d!.RootDirectory.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recycleDrive = Fixers.RecycleBin.FindDriveForPath(_recycleBin.GetEffectiveRoot());
        var recycleRoot = recycleDrive is null ? null : Path.TrimEndingDirectorySeparator(recycleDrive.RootDirectory.FullName);

        var pluginDataPath = MediaDashDb.DataDirectory;
        var pluginDataDrive = pluginDataPath is null ? null : Fixers.RecycleBin.FindDriveForPath(pluginDataPath);
        var pluginDataRoot = pluginDataDrive is null ? null : Path.TrimEndingDirectorySeparator(pluginDataDrive.RootDirectory.FullName);

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                // Fixed only — skip CDs, network shares (which may not be ready or may be huge remote mounts we
                // don't want to poll), and RAM disks. IsReady guards against USB drives that are attached but
                // not yet mounted.
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                var trimmedName = Path.TrimEndingDirectorySeparator(drive.Name);
                var isLibraryDrive = libraryRoots.Contains(trimmedName);
                var isRecycleDrive = recycleRoot is not null && string.Equals(trimmedName, recycleRoot, StringComparison.OrdinalIgnoreCase);
                var isPluginDataDrive = pluginDataRoot is not null && string.Equals(trimmedName, pluginDataRoot, StringComparison.OrdinalIgnoreCase);
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
                    IsRecycleBinDrive = isRecycleDrive,
                    IsPluginDataDrive = isPluginDataDrive,
                    LowSpaceWarning = ComputeLowSpaceWarning(drive, isLibraryDrive || isRecycleDrive || isPluginDataDrive)
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

        // Libraries on a NAS addressed by UNC path (\\nas\share) never match a drive letter above, so
        // without this they'd be missing from the card (and from the free-space total and the
        // low-space warning). Measured with a short timeout + cache so an offline NAS can't stall
        // the dashboard; a share that can't be read is simply left out until it answers again.
        // Several shares on one NAS volume report the same space, so they're merged into one entry
        // instead of listing the disk twice and double-counting its free space.
        var shareReadings = new List<(string Root, long FreeBytes, long TotalBytes)>();
        foreach (var shareRoot in UncLibraryRoots.From(libraryLocations))
        {
            if (ShareProbe.TryGet(shareRoot, out var shareFree, out var shareTotal))
            {
                shareReadings.Add((shareRoot, shareFree, shareTotal));
            }
        }

        foreach (var volume in UncVolumeGrouping.Group(shareReadings))
        {
            freeDisk += volume.FreeBytes;
            totalDisk += volume.TotalBytes;
            drives.Add(new DriveUsage
            {
                Root = volume.Label,
                FreeBytes = volume.FreeBytes,
                TotalBytes = volume.TotalBytes,
                IsLibraryDrive = true,
                IsNetworkShare = true,
                LowSpaceWarning = ComputeShareLowSpaceWarning(volume.FreeBytes, volume.TotalBytes)
            });
        }

        // Docker overlay / btrfs subvolume / etc. often report as DriveType.Unknown and get filtered out
        // above. If the recycle bin lives on one of those, its drive wouldn't appear on the Overview and
        // the user would run out of space with no warning. Force-add it here.
        if (recycleDrive is not null && !drives.Any(d =>
            string.Equals(Path.TrimEndingDirectorySeparator(d.Root), recycleRoot, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var recycleUsage = new DriveUsage
                {
                    Root = recycleDrive.Name,
                    FreeBytes = recycleDrive.AvailableFreeSpace,
                    TotalBytes = recycleDrive.TotalSize,
                    IsLibraryDrive = false,
                    IsRecycleBinDrive = true,
                    IsPluginDataDrive = pluginDataRoot is not null && string.Equals(Path.TrimEndingDirectorySeparator(recycleDrive.RootDirectory.FullName), pluginDataRoot, StringComparison.OrdinalIgnoreCase),
                    LowSpaceWarning = ComputeLowSpaceWarning(recycleDrive, alwaysCheck: true)
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

        // Same force-add for the plugin-data drive — DB + caches live there and a full volume can
        // stall SQLite writes long before it starves Jellyfin.
        if (pluginDataDrive is not null && !drives.Any(d =>
            string.Equals(Path.TrimEndingDirectorySeparator(d.Root), pluginDataRoot, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var pluginUsage = new DriveUsage
                {
                    Root = pluginDataDrive.Name,
                    FreeBytes = pluginDataDrive.AvailableFreeSpace,
                    TotalBytes = pluginDataDrive.TotalSize,
                    IsLibraryDrive = false,
                    IsRecycleBinDrive = false,
                    IsPluginDataDrive = true,
                    LowSpaceWarning = ComputeLowSpaceWarning(pluginDataDrive, alwaysCheck: true)
                };
                drives.Add(pluginUsage);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var config = Plugin.Instance!.Configuration;
        var queuedCount = _issues.GetIssues(status: IssueStatus.Queued).Count;
        var autoQueueableCount = _issues.GetIssues(status: IssueStatus.Detected)
            .Count(i => config.GetFixMode(i.Type) == Configuration.FixMode.Automatic);
        var binContents = SafeGetBinContents();
        var fixSnapshot = _runState.ReadSnapshot(RunRole.Fix);

        return new StatusResponse
        {
            IsScanning = scanTask is not null && scanTask.State != TaskState.Idle,
            ScanProgress = scanTask?.CurrentProgress,
            IsFixing = fixTask is not null && fixTask.State != TaskState.Idle,
            FixProgress = fixTask?.CurrentProgress,
            OpenIssueTotal = summary.Sum(s => s.Count),
            FailedHistoryTotal = _history.GetFailedHistoryCount(config.HistoryHiddenBeforeUtcTicks),
            FreeDiskBytes = freeDisk,
            TotalDiskBytes = totalDisk,
            LastScanUtc = summary.Count > 0 ? summary.Max(s => s.NewestDetectedUtc) : null,
            LastScanCompletedUtc = ParseStateUtc(_db.GetState("LastScanCompletedUtc")),
            TotalPotentialSavings = summary.Sum(s => s.PotentialSavings),
            LifetimeBytesReclaimed = _history.GetLifetimeBytesFreed(),
            LifetimeCounts = _history.GetLifetimeSummary().Select(s => new TypeCount
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
            ScanScannersDone = Plugin.ScanScannersDone,
            ScanScannersTotal = Plugin.ScanScannersTotal,
            Drives = drives,
            CurrentActivity = Plugin.CurrentActivity,
            CurrentActivityLabel = Plugin.CurrentActivityLabel,
            DataDirectory = MediaDashDb.DataDirectory,
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
            FixPauseReason = fixSnapshot.PauseReason,
            CurrentOperationProgress = fixSnapshot.CurrentOperationProgress,
            RedownloadWarnings = Plugin.RedownloadWarnings,
            Repair = _db.GetRepairSummary(DateTime.UtcNow.AddDays(-30).Ticks)
        };
    }

    private (int FileCount, long SizeBytes) SafeGetBinContents()
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

    // Parse the ISO-8601 timestamp written by ScanTask into the plugin_state table. Returns null
    // for missing / malformed values so the UI (UI-15) can gracefully fall back to LastScanUtc.
    private static DateTime? ParseStateUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
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

    /// <summary>
    /// Computes the plain-language low-space warning for a drive, or null when the drive isn't
    /// worth warning about (below threshold, not a monitored drive, or feature disabled).
    /// Reads the current plugin config for the threshold percent + master enable flag.
    /// </summary>
    /// <param name="drive">The drive to evaluate.</param>
    /// <param name="alwaysCheck">When false, non-monitored drives return null immediately. The main enumeration passes true for library/recycle/plugin-data drives and false for the rest.</param>
    /// <returns>The warning message, or null.</returns>
    internal static string? ComputeLowSpaceWarning(DriveInfo drive, bool alwaysCheck)
    {
        if (!alwaysCheck)
        {
            return null;
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.LibraryDriveLowSpaceWarningEnabled)
        {
            return null;
        }

        return ComputeLowSpaceWarning(drive.AvailableFreeSpace, drive.TotalSize, drive.DriveFormat, config.LibraryDriveWarnThresholdPercent);
    }

    private static UncVolumeProbe.Reading MeasureShare(string shareRoot)
    {
        return Fixers.FreeSpaceGuard.TryGetDiskSpace(shareRoot, out var free, out var total)
            ? new UncVolumeProbe.Reading(true, free, total)
            : default;
    }

    // Same config gating as the DriveInfo overload (feature flag + threshold), for a share that has
    // no DriveInfo. The filesystem format is unknown over SMB, so no CoW tightening applies.
    private static string? ComputeShareLowSpaceWarning(long freeBytes, long totalBytes)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.LibraryDriveLowSpaceWarningEnabled)
        {
            return null;
        }

        return ComputeLowSpaceWarning(freeBytes, totalBytes, string.Empty, config.LibraryDriveWarnThresholdPercent);
    }

    /// <summary>
    /// Pure computation split out so tests can drive it without a real <see cref="DriveInfo"/>.
    /// </summary>
    /// <param name="freeBytes">Bytes free on the drive.</param>
    /// <param name="totalBytes">Total bytes on the drive.</param>
    /// <param name="driveFormat">Filesystem format string (e.g. "NTFS", "ext4", "zfs", "btrfs"). Case-insensitive.</param>
    /// <param name="thresholdPercent">Configured warning threshold (percent used, 0..100). 0 disables.</param>
    /// <returns>Human warning message, or null when below threshold.</returns>
    internal static string? ComputeLowSpaceWarning(long freeBytes, long totalBytes, string driveFormat, int thresholdPercent)
    {
        if (thresholdPercent <= 0 || totalBytes <= 0)
        {
            return null;
        }

        // Copy-on-write filesystems fragment sooner as free extents shrink; tighten by 5 %.
        var effectiveThreshold = thresholdPercent;
        var fmt = driveFormat?.ToLowerInvariant() ?? string.Empty;
        if (fmt.Contains("zfs", StringComparison.Ordinal) || fmt.Contains("btrfs", StringComparison.Ordinal))
        {
            effectiveThreshold = Math.Max(1, thresholdPercent - 5);
        }

        var percentUsed = (int)((totalBytes - freeBytes) * 100L / totalBytes);
        if (percentUsed < effectiveThreshold)
        {
            return null;
        }

        var freeGb = freeBytes / (1024.0 * 1024.0 * 1024.0);
        return "Drive is " + percentUsed + "% full (" + freeGb.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB free) — free some space before fixes start failing.";
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

        var recycleRoot = Path.TrimEndingDirectorySeparator(recycleDrive.RootDirectory.FullName);
        return !libraryDrives.Any(d => string.Equals(
            Path.TrimEndingDirectorySeparator(d.Root),
            recycleRoot,
            StringComparison.OrdinalIgnoreCase));
    }
}

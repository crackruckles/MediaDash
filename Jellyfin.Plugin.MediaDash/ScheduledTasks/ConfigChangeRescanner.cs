using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Subscribes to <see cref="MediaBrowser.Common.Plugins.BasePlugin{TConfiguration}.ConfigurationChanged"/>
/// and, when the user edits a setting a scanner reads, wipes the affected Detected rows and
/// enqueues a targeted rescan for just that scanner. Fixes issue #64: the SuggestedFix /
/// DetailsJson stored on each row is baked at scan time, so pre-change rows kept saying
/// "remove jpn" after the user added Japanese to <c>AllowedAudioLanguages</c>.
/// </summary>
internal sealed class ConfigChangeRescanner : IHostedService, IDisposable
{
    // Small debounce so a single Save doesn't fire N-property N-scan calls when the client
    // POSTs the whole config in one shot (Jellyfin fires one ConfigurationChanged per save,
    // but the delay also collapses two saves inside the debounce window into one scan).
    private static readonly TimeSpan RescanDebounce = TimeSpan.FromMilliseconds(750);

    private readonly ITaskManager _taskManager;
    private readonly IssueStore _issues;
    private readonly RunState _runState;
    private readonly ILogger<ConfigChangeRescanner> _logger;
    private readonly object _stateLock = new();
    private PluginConfiguration? _lastSeen;
    private CancellationTokenSource? _pendingCts;
    private HashSet<IssueType>? _pendingTypes;
    private Task _pendingTask = Task.CompletedTask;
    private bool _stopping;

    public ConfigChangeRescanner(ITaskManager taskManager, IssueStore issues, RunState runState, ILogger<ConfigChangeRescanner> logger)
    {
        _taskManager = taskManager;
        _issues = issues;
        _runState = runState;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            _stopping = false;
        }

        if (Plugin.Instance is null)
        {
            _logger.LogWarning("ConfigChangeRescanner: Plugin.Instance null at startup; config-change rescans disabled for this session.");
            return Task.CompletedTask;
        }

        _lastSeen = ConfigChangeInvalidation.Clone(Plugin.Instance.Configuration);
        Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        Task pending;
        Task cancellation;
        lock (_stateLock)
        {
            _stopping = true;
            cancellation = _pendingCts?.CancelAsync() ?? Task.CompletedTask;
            _pendingCts = null;
            _pendingTypes = null;
            pending = _pendingTask;
        }

        await cancellation.ConfigureAwait(false);
        await pending.ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _stopping = true;
            _pendingCts?.Cancel();
            _pendingCts?.Dispose();
            _pendingCts = null;
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        if (e is not PluginConfiguration next)
        {
            return;
        }

        IReadOnlyList<string> changed;
        lock (_stateLock)
        {
            if (_lastSeen is null)
            {
                _lastSeen = ConfigChangeInvalidation.Clone(next);
                return;
            }

            changed = ConfigChangeInvalidation.ChangedFields(_lastSeen, next);
            _lastSeen = ConfigChangeInvalidation.Clone(next);
        }

        if (changed.Count == 0)
        {
            return;
        }

        var affected = ConfigChangeInvalidation.AffectedTypes(changed);
        if (affected is not null && affected.Count == 0)
        {
            // User-visible setting change (e.g. RecycleBinRetentionDays, DryRun) that no scanner
            // reads — nothing in the issues table is now stale.
            return;
        }

        // affected == null means "full rescan". Wipe every Detected row we know how to invalidate.
        var toWipe = affected ?? new HashSet<IssueType>(Enum.GetValues<IssueType>());

        try
        {
            var removed = _issues.WipeDetectedForTypes(toWipe);
            _logger.LogInformation(
                "ConfigChangeRescanner: config edit changed {ChangedCount} field(s) ({Changed}); wiped {Removed} Detected row(s) across {TypeCount} type(s); scheduling targeted rescan.",
                changed.Count,
                string.Join(", ", changed),
                removed,
                toWipe.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ConfigChangeRescanner: failed to wipe stale Detected rows; the next scheduled scan will refresh them.");
            Api.Diagnostics.Record("ConfigChangeRescanner.WipeFailed", "After your settings change, MediaDash could not clear the old findings, so the Issues tab may show outdated items until the next scan. Details: " + ex.Message);
            return;
        }

        ScheduleRescan(affected);
    }

    private void ScheduleRescan(IReadOnlySet<IssueType>? typeFilter)
    {
        lock (_stateLock)
        {
            if (_stopping)
            {
                return;
            }

            // Every save already invalidated its rows. Keep all pending types; null means
            // a full scan and must dominate any subsequent narrower request.
            if (_pendingCts is null)
            {
                _pendingTypes = typeFilter is null ? null : new HashSet<IssueType>(typeFilter);
            }
            else if (typeFilter is null)
            {
                _pendingTypes = null;
            }
            else
            {
                _pendingTypes?.UnionWith(typeFilter);
            }

            _pendingCts?.Cancel();
            var cts = new CancellationTokenSource();
            _pendingCts = cts;
            _pendingTask = RunPendingScanAsync(cts);
        }
    }

    private async Task RunPendingScanAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(RescanDebounce, cts.Token).ConfigureAwait(false);
            lock (_stateLock)
            {
                if (_stopping || _pendingCts != cts)
                {
                    return;
                }

                var types = _pendingTypes;
                _pendingCts = null;
                _pendingTypes = null;
                TriggerScan(types);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer save owns the union of pending types, or the host is stopping.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ConfigChangeRescanner: targeted rescan failed to start; the next scheduled scan will still fire.");
            Api.Diagnostics.Record("ConfigChangeRescanner.TriggerFailed", "After your settings change, MediaDash could not start a fresh scan. Click Scan now, or the next scheduled scan will update the results. Details: " + ex.Message);
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void TriggerScan(IReadOnlySet<IssueType>? typeFilter)
    {
        var scanWorker = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is ScanTask);
        if (scanWorker is null)
        {
            _logger.LogWarning("ConfigChangeRescanner: ScanTask not registered; skipping targeted rescan.");
            return;
        }

        if (scanWorker.State != TaskState.Idle)
        {
            // A scan is already running. Setting NextRunTypeFilter here would attach to the CURRENT
            // run's post-config state after it clears (BypassIdleCheckOnce is only consumed at start).
            // Skip — the next scheduled/manual scan will honour the wipe we just did.
            _logger.LogInformation("ConfigChangeRescanner: ScanTask busy (state={State}); skipping opportunistic rescan.", scanWorker.State);
            return;
        }

        _runState.ArmNextScanTypeFilter(typeFilter);
        _runState.ArmBypassIdleCheck(RunRole.Scan);
        _taskManager.Execute(scanWorker, new TaskOptions());
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Decides whether the server is in use: someone is playing media, or a session was active recently.
/// </summary>
public static class IdleCheck
{
    private static readonly TimeSpan ActiveSessionWindow = TimeSpan.FromMinutes(15);

    // Key of Jellyfin's own library-scan scheduled task (RefreshMediaLibraryTask). Matches
    // the registered worker name — stable across 10.x and 12.x.
    internal const string JellyfinLibraryScanKey = "RefreshLibrary";

    /// <summary>
    /// Checks whether anyone is watching something or was recently doing so.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <returns>True when the server is busy and scheduled work should wait.</returns>
    public static bool IsServerBusy(ISessionManager sessionManager)
    {
        var now = DateTime.UtcNow;
        return sessionManager.Sessions.Any(s => IsSessionActive(s.NowPlayingItem is not null, s.LastPlaybackCheckIn, now));
    }

    /// <summary>
    /// Returns true when Jellyfin's own library-scan task is currently running. MediaDash's
    /// scanner/fixer both read the same files Jellyfin is probing/thumbnailing, so overlapping
    /// the two pins the disk and makes the whole host feel unresponsive — exactly the symptom
    /// reported in issue #72 ("completely blocking disk I/O" right after importing 4K media).
    /// Fail-safe: a null manager or a lookup exception reports "not running" so the plugin
    /// doesn't deadlock itself if Jellyfin's task API shape ever changes.
    /// </summary>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface, or null.</param>
    /// <returns>True when the library scan worker is in any non-idle state.</returns>
    public static bool IsLibraryScanRunning(ITaskManager? taskManager)
    {
        if (taskManager is null)
        {
            return false;
        }

        try
        {
            return taskManager.ScheduledTasks.Any(w =>
                string.Equals(w.ScheduledTask.Key, JellyfinLibraryScanKey, StringComparison.Ordinal)
                && w.State != TaskState.Idle);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool IsSessionActive(bool hasNowPlaying, DateTime lastPlaybackCheckIn, DateTime now)
    {
        // Only playback-related activity counts. LastActivityDate updates on EVERY session poll —
        // including an admin sitting on the Jellyfin dashboard, whose browser pings every few
        // seconds. Using it here misreported the admin as "someone is watching" (2026-08-07 bug
        // report). LastPlaybackCheckIn only updates while a client is actually playing something.
        return hasNowPlaying || lastPlaybackCheckIn > now - ActiveSessionWindow;
    }
}

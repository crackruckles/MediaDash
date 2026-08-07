using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Decides whether the server is in use: someone is playing media, or a session was active recently.
/// </summary>
public static class IdleCheck
{
    private static readonly TimeSpan ActiveSessionWindow = TimeSpan.FromMinutes(15);

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

    internal static bool IsSessionActive(bool hasNowPlaying, DateTime lastPlaybackCheckIn, DateTime now)
    {
        // Only playback-related activity counts. LastActivityDate updates on EVERY session poll —
        // including an admin sitting on the Jellyfin dashboard, whose browser pings every few
        // seconds. Using it here misreported the admin as "someone is watching" (2026-08-07 bug
        // report). LastPlaybackCheckIn only updates while a client is actually playing something.
        return hasNowPlaying || lastPlaybackCheckIn > now - ActiveSessionWindow;
    }
}

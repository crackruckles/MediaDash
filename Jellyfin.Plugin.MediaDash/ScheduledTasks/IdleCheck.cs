using System;
using System.Linq;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Decides whether the server is in use: someone is playing media, or a session was active recently.
/// </summary>
public static class IdleCheck
{
    private static readonly TimeSpan ActiveSessionWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Checks whether anyone is watching something or has used the server recently.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <returns>True when the server is busy and scheduled work should wait.</returns>
    public static bool IsServerBusy(ISessionManager sessionManager)
    {
        var cutoff = DateTime.UtcNow - ActiveSessionWindow;
        return sessionManager.Sessions.Any(s => s.NowPlayingItem is not null || s.LastActivityDate > cutoff);
    }
}

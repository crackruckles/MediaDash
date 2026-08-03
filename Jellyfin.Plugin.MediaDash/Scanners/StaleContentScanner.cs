using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Flags media that has sat unwatched on the server longer than the configured threshold — either
/// nobody has ever played it (and it was added more than threshold days ago), or the most recent
/// play across every user account is older than the threshold. Detect-only: MediaDash doesn't ship
/// a stale-content fixer, because deciding what to do with old-but-unwatched media is subjective.
/// </summary>
/// <remarks>
/// The <c>User</c> entity moved namespaces between Jellyfin 10.11 (<c>Jellyfin.Data.Entities</c>) and 12.0
/// (<c>Jellyfin.Database.Implementations.Entities</c>), and <c>IUserManager.Users</c> was renamed to
/// <c>GetUsers()</c>. Because MediaDash ships one binary targeting the 10.11 SDK for both host lines,
/// touching User by static reference (as a method arg type or property return type) throws
/// <c>MissingMethodException</c> on 12.0. Every User-typed API hop below is invoked via reflection so the
/// same DLL loads on both versions.
/// </remarks>
public sealed class StaleContentScanner : IScanner
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StaleContentScanner> _logger;
    private readonly Lazy<UserApiBridge> _bridge;

    /// <summary>
    /// Initializes a new instance of the <see cref="StaleContentScanner"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface, used to resolve excluded library ids to paths.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{StaleContentScanner}"/> interface.</param>
    public StaleContentScanner(IUserManager userManager, IUserDataManager userDataManager, ILibraryManager libraryManager, ILogger<StaleContentScanner> logger)
    {
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _bridge = new Lazy<UserApiBridge>(() => new UserApiBridge(userManager, userDataManager));
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.Stale;

    private static Configuration.PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var thresholdDays = Config.StaleThresholdDays;
        if (Config.StaleFixMode == FixMode.Off || thresholdDays <= 0)
        {
            _logger.LogInformation("Scanner Stale skipped: not configured yet");
            progress.Report(100);
            return Task.FromResult<IReadOnlyList<Issue>>([]);
        }

        var cutoff = DateTime.UtcNow.AddDays(-thresholdDays);

        // Excluded-library path prefixes: resolved once so the per-item check is a linear scan over a
        // small list rather than a per-item ILibraryManager round trip.
        var excludedIds = Config.StaleExcludedLibraryIds ?? [];
        var idLookup = excludedIds.Length == 0 ? null : VirtualFolderIdentity.BuildIdLookup(_libraryManager);
        var excludedPrefixes = excludedIds.Length == 0
            ? []
            : _libraryManager.GetVirtualFolders()
                .Where(f => excludedIds.Contains(VirtualFolderIdentity.GetId(f, idLookup), StringComparer.OrdinalIgnoreCase))
                .SelectMany(f => f.Locations ?? [])
                .Select(l => System.IO.Path.TrimEndingDirectorySeparator(l) + System.IO.Path.DirectorySeparatorChar)
                .ToList();
        var excludedGenres = (Config.StaleExcludedGenres ?? [])
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<object> users;
        try
        {
            users = _bridge.Value.GetUsers();
        }
        catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException or InvalidOperationException)
        {
            Api.Diagnostics.Record("StaleScanner.UserApi", ex.GetType().Name + ": " + ex.Message);
            _logger.LogWarning(ex, "StaleContentScanner: could not enumerate users on this Jellyfin version; scanner disabled for this run");
            progress.Report(100);
            return Task.FromResult<IReadOnlyList<Issue>>([]);
        }

        var issues = new List<Issue>();

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            if (excludedPrefixes.Count > 0 && excludedPrefixes.Any(p => item.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (excludedGenres.Count > 0 && item.Genres is { Length: > 0 } genres && genres.Any(g => excludedGenres.Contains(g)))
            {
                continue;
            }

            DateTime? mostRecentPlay = null;
            foreach (var user in users)
            {
                var ud = _bridge.Value.GetUserData(user, item);
                if (ud?.LastPlayedDate is DateTime lpd && (mostRecentPlay is null || lpd > mostRecentPlay))
                {
                    mostRecentPlay = lpd;
                }
            }

            if (!IsStale(item.DateCreated, mostRecentPlay, cutoff))
            {
                continue;
            }

            long size = 0;
            try
            {
                size = new System.IO.FileInfo(item.Path).Length;
            }
            catch (System.IO.IOException)
            {
            }

            var daysUnwatched = (int)(DateTime.UtcNow - (mostRecentPlay ?? item.DateCreated)).TotalDays;
            issues.Add(new Issue
            {
                Type = Type,
                ItemId = item.Id,
                Path = item.Path,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow,
                SizeSavings = size,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    daysUnwatched,
                    neverPlayed = mostRecentPlay is null,
                    lastPlayedUtc = mostRecentPlay?.ToString("O"),
                    addedUtc = item.DateCreated.ToString("O"),
                    thresholdDays
                }),
                SuggestedFix = mostRecentPlay is null
                    ? "Nobody has ever played this. Delete it if you don't want it, or watch it to keep it."
                    : "Not played in a while. Delete it if you're done with it, or play it again to reset the timer."
            });

            progress.Report((i + 1) * 100.0 / items.Count);
        }

        progress.Report(100);
        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    /// <summary>
    /// Pure age/last-played arithmetic, extracted so it can be unit-tested without stubbing Jellyfin DI.
    /// Stale iff both conditions hold: the item has been on the server past the cutoff (been-there-long-enough
    /// guard), AND nobody has played it since the cutoff (either never, or long ago).
    /// </summary>
    /// <param name="dateCreated">When the item was added to the server (UTC).</param>
    /// <param name="mostRecentPlay">Most recent play across all users, or null when never played.</param>
    /// <param name="cutoff">The threshold instant: anything newer than this is fresh.</param>
    /// <returns>True when the item should be flagged as stale.</returns>
    internal static bool IsStale(DateTime dateCreated, DateTime? mostRecentPlay, DateTime cutoff)
    {
        if (dateCreated > cutoff)
        {
            return false;
        }

        return mostRecentPlay is null || mostRecentPlay <= cutoff;
    }

    // ponytail: reflection-based bridge for User-touching APIs. Avoids the compile-time dependency on the
    // User entity type, whose namespace changed between Jellyfin 10.11 and 12.0. One resolve at construction,
    // then plain MethodInfo.Invoke per call — no per-call reflection lookups.
    private sealed class UserApiBridge
    {
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly MethodInfo _getUsers;
        private readonly MethodInfo _getUserData;

        internal UserApiBridge(IUserManager userManager, IUserDataManager userDataManager)
        {
            _userManager = userManager;
            _userDataManager = userDataManager;

            var umType = userManager.GetType();
            _getUsers = umType.GetMethod("get_Users", BindingFlags.Public | BindingFlags.Instance)
                ?? umType.GetMethod("GetUsers", BindingFlags.Public | BindingFlags.Instance, System.Type.EmptyTypes)
                ?? throw new InvalidOperationException("No IUserManager users accessor found (checked Users and GetUsers).");

            _getUserData = userDataManager.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetUserData"
                    && !m.IsGenericMethod
                    && m.GetParameters().Length == 2
                    && m.GetParameters()[1].ParameterType == typeof(BaseItem)
                    && m.ReturnType == typeof(UserItemData))
                ?? throw new InvalidOperationException("No IUserDataManager.GetUserData(User, BaseItem) method found.");
        }

        internal List<object> GetUsers()
        {
            var raw = _getUsers.Invoke(_userManager, null);
            if (raw is not IEnumerable enumerable)
            {
                return [];
            }

            var list = new List<object>();
            foreach (var u in enumerable)
            {
                if (u is not null)
                {
                    list.Add(u);
                }
            }

            return list;
        }

        internal UserItemData? GetUserData(object user, BaseItem item)
        {
            return _getUserData.Invoke(_userDataManager, [user, item]) as UserItemData;
        }
    }
}

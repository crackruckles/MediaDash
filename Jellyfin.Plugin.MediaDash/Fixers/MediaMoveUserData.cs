using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Preserves user state when native indexing replaces a moved item's path-derived ID.</summary>
public sealed class MediaMoveUserData : IHostedService
{
    private readonly ILibraryManager _library;
    private readonly MediaMoveStore _moves;
    private readonly ILogger<MediaMoveUserData> _logger;
    private readonly Lazy<UserBridge> _bridge;
    private readonly ITaskManager? _tasks;
    private readonly object _gate = new();
    private bool _applying;

    /// <summary>Initializes a new instance of the <see cref="MediaMoveUserData"/> class.</summary>
    /// <param name="library">Native indexing events and item lookup.</param>
    /// <param name="moves">Durable move snapshots.</param>
    /// <param name="users">Users whose item state must survive a move.</param>
    /// <param name="data">Native user-data storage.</param>
    /// <param name="logger">Recovery diagnostics.</param>
    /// <param name="tasks">Native library-scan completion boundary.</param>
    public MediaMoveUserData(ILibraryManager library, MediaMoveStore moves, IUserManager users, IUserDataManager data, ILogger<MediaMoveUserData> logger, ITaskManager? tasks = null)
        : this(library, moves, (object)users, data, logger, tasks)
    {
    }

    internal MediaMoveUserData(ILibraryManager library, MediaMoveStore moves, object users, object data, ILogger<MediaMoveUserData> logger, ITaskManager? tasks = null)
    {
        _library = library;
        _moves = moves;
        _logger = logger;
        _tasks = tasks;
        _bridge = new Lazy<UserBridge>(() => new UserBridge(users, data));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_tasks is not null)
        {
            _tasks.TaskCompleted += OnTaskCompleted;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_tasks is not null)
        {
            _tasks.TaskCompleted -= OnTaskCompleted;
        }

        return Task.CompletedTask;
    }

    internal Snapshot[] Capture(MediaMovePlan plan, Snapshot[]? fallback = null)
    {
        if (plan.ItemId == Guid.Empty && plan.ItemBindings.Length == 0)
        {
            return [];
        }

        try
        {
            var users = _bridge.Value.Users();
            var result = new List<Snapshot>();
            var bindings = plan.ItemBindings.AsEnumerable();
            if (plan.ItemId != Guid.Empty && !plan.ItemBindings.Any(binding => binding.ItemId == plan.ItemId))
            {
                bindings = bindings.Append(new(plan.ItemId, plan.ItemPath, plan.Identity));
            }

            foreach (var binding in bindings)
            {
                var entry = plan.Entries.FirstOrDefault(candidate => LibraryGuard.IsUnder(binding.Path, candidate.Source));
                if (entry is null)
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(entry.Destination, Path.GetRelativePath(entry.Source, binding.Path)));
                var item = _library.FindByPath(binding.Path, null) ?? _library.GetItemById(binding.ItemId);
                foreach (var user in users)
                {
                    var userId = UserBridge.Id(user);
                    var data = item is null ? null : _bridge.Value.Read(user, item);
                    var saved = fallback?.FirstOrDefault(snapshot => snapshot.ItemId == binding.ItemId && snapshot.UserId == userId);
                    if (saved is not null && (data is null || (!HasState(data) && !saved.Applied)))
                    {
                        data = saved.Data;
                    }

                    if (data is null)
                    {
                        throw new IOException("Not moved: MediaDash could not save this item's watched status before moving it, so nothing was moved. Try again.");
                    }

                    var copy = JsonSerializer.Deserialize<UserItemData>(JsonSerializer.Serialize(data, MediaMovePlan.JsonOptions), MediaMovePlan.JsonOptions)!;
                    result.Add(new(binding.ItemId, userId, binding.Path, destination, copy, !HasState(copy) && (saved is null || !HasState(saved.Data)), saved?.Data));
                    if (result.Count > 65536)
                    {
                        throw new IOException("Not moved: this selection holds too much watched-status data to move at once. Move it in smaller parts, for example one season at a time.");
                    }
                }
            }

            return result.ToArray();
        }
        catch (Exception error) when (error is not IOException and not OperationCanceledException)
        {
            throw new IOException("Not moved: MediaDash could not safely save watched status first, so nothing was moved. Try again.", error);
        }
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs args)
    {
        if (args.Task.ScheduledTask.Key == "RefreshLibrary" && args.Result.Status == TaskCompletionStatus.Completed)
        {
            Reconcile();
        }
    }

    internal void Reconcile(BaseItem? indexedItem = null, string? operationId = null)
    {
        lock (_gate)
        {
            if (_applying)
            {
                return;
            }

            _applying = true;
            try
            {
                foreach (var operation in _moves.GetPendingUserData(indexedItem?.Path))
                {
                    if (operationId is not null && operation.Id != operationId)
                    {
                        continue;
                    }

                    if (MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id) is not { } manifest)
                    {
                        continue;
                    }

                    var snapshots = manifest.UserData.ToArray();
                    var changed = false;
                    for (var index = 0; index < snapshots.Length; index++)
                    {
                        var snapshot = snapshots[index];
                        if (snapshot.Applied || (indexedItem is not null && !SamePath(indexedItem.Path, snapshot.DestinationPath)))
                        {
                            continue;
                        }

                        var found = indexedItem ?? _library.FindByPath(snapshot.DestinationPath, null);
                        // Path queries may return a detached item. Native v12 DTOs read user data
                        // from the canonical cached instance, which SaveUserData must update.
                        var target = found is null ? null : _library.GetItemById(found.Id);
                        if (target is null || !SamePath(target.Path, snapshot.DestinationPath) || !MatchesFinalPayload(manifest, target.Path))
                        {
                            continue;
                        }

                        var user = _bridge.Value.Users().FirstOrDefault(candidate => UserBridge.Id(candidate) == snapshot.UserId);
                        if (user is null)
                        {
                            continue;
                        }

                        var current = _bridge.Value.Read(user, target) ?? throw new IOException("Not moved: MediaDash could not read this item's watched status and other user data, so it could not keep them safe during the move. Try again; if it keeps happening, restart Jellyfin.");
                        if (RestoreState(snapshot.Data, current, snapshot.TargetBaseline))
                        {
                            _bridge.Value.Save(user, target, current);
                        }

                        snapshots[index] = snapshot with { Applied = true };
                        changed = true;
                    }

                    if (changed && !_moves.SaveUserDataProgress(operation, manifest with { UserData = snapshots }))
                    {
                        Api.Diagnostics.Record("MediaMove.UserData", "Watched status and other user data for a moved item were kept, but their backup copy changed unexpectedly. Nothing was lost; the backup stays with the move record.");
                    }
                }
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Move user-state restoration is pending native indexing or review");
                Api.Diagnostics.Record("MediaMove.UserData", "Watched status and other user data for a moved item could not be put back yet. They are saved with the move record and MediaDash will try again. Details: " + error.Message);
            }
            finally
            {
                _applying = false;
            }
        }
    }

    private static bool SamePath(string first, string second) => string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool MatchesFinalPayload(MediaMoveManifest manifest, string path)
    {
        for (var index = 0; index < manifest.Plan.Entries.Length; index++)
        {
            var entry = manifest.Plan.Entries[index];
            if (!LibraryGuard.IsUnder(path, entry.Destination))
            {
                continue;
            }

            var relative = SamePath(path, entry.Destination) ? string.Empty : Path.GetRelativePath(entry.Destination, path);
            var payload = manifest.Steps[index].FinalInventory.FirstOrDefault(candidate => SamePath(candidate.RelativePath, relative));
            if (payload is null || !PhysicalFileIdentity.TryRead(path, out var identity) || identity.IsReparsePoint
                || identity.Volume != payload.Volume || identity.FileId != payload.FileId)
            {
                return false;
            }

            return payload.IsDirectory || (new FileInfo(path) is var file && file.Length == payload.Length && file.LastWriteTimeUtc.Ticks == payload.LastWriteUtcTicks);
        }

        return false;
    }

    private static bool HasState(UserItemData data) => data.Played || data.PlayCount != 0 || data.PlaybackPositionTicks != 0
        || data.IsFavorite || data.LastPlayedDate is not null || data.Rating is not null || data.Likes is not null
        || data.AudioStreamIndex is not null || data.SubtitleStreamIndex is not null;

    internal static bool RestoreState(UserItemData source, UserItemData target, UserItemData? targetBaseline = null)
    {
        if (HasState(target) && (targetBaseline is null || StateSignature(target) != StateSignature(targetBaseline)))
        {
            return false;
        }

        target.Played = source.Played;
        target.PlayCount = source.PlayCount;
        target.PlaybackPositionTicks = source.PlaybackPositionTicks;
        target.IsFavorite = source.IsFavorite;
        target.LastPlayedDate = source.LastPlayedDate;
        target.Rating = source.Rating;
        target.AudioStreamIndex = source.AudioStreamIndex;
        target.SubtitleStreamIndex = source.SubtitleStreamIndex;
        return true;
    }

    private static string StateSignature(UserItemData data) => JsonSerializer.Serialize(
        new
        {
            data.Played, data.PlayCount, data.PlaybackPositionTicks, data.IsFavorite, data.LastPlayedDate,
            data.Rating, data.Likes, data.AudioStreamIndex, data.SubtitleStreamIndex
        },
        MediaMovePlan.JsonOptions);

    internal sealed record Snapshot(Guid ItemId, Guid UserId, string SourcePath, string DestinationPath, UserItemData Data, bool Applied, UserItemData? TargetBaseline = null);

    // Resolve the relocated User entity at runtime, as in StaleContentScanner's ABI bridge.
    private sealed class UserBridge
    {
        private readonly object _users;
        private readonly object _data;
        private readonly MethodInfo _getUsers;
        private readonly MethodInfo _read;
        private readonly MethodInfo _save;

        internal UserBridge(object users, object data)
        {
            _users = users;
            _data = data;
            _getUsers = users.GetType().GetMethod("get_Users") ?? users.GetType().GetMethod("GetUsers", Type.EmptyTypes)
                ?? throw new IOException("Not moved: this Jellyfin version does not let MediaDash read user data, so it cannot keep watched status during a move. Updating MediaDash may fix this.");
            _read = data.GetType().GetMethods().First(method => method.Name == "GetUserData" && !method.IsGenericMethod
                && method.GetParameters() is { Length: 2 } parameters && parameters[1].ParameterType == typeof(BaseItem) && method.ReturnType == typeof(UserItemData));
            _save = data.GetType().GetMethods().First(method => method.Name == "SaveUserData" && !method.IsGenericMethod
                && method.GetParameters() is { Length: 5 } parameters && parameters[1].ParameterType == typeof(BaseItem) && parameters[2].ParameterType == typeof(UserItemData));
        }

        internal object[] Users() => (_getUsers.Invoke(_users, null) as IEnumerable ?? throw new IOException("Not moved: MediaDash could not read Jellyfin's user list, so it could not save watched status first. Try again; if it keeps happening, restart Jellyfin.")).Cast<object>().ToArray();

        internal static Guid Id(object user) => user.GetType().GetProperty("Id")?.GetValue(user) is Guid id && id != Guid.Empty
            ? id : throw new IOException("Not moved: MediaDash could not read one Jellyfin user's details, so it could not save their watched status first. Try again; if it keeps happening, restart Jellyfin.");

        internal UserItemData? Read(object user, BaseItem item) => _read.Invoke(_data, [user, item]) as UserItemData;

        internal void Save(object user, BaseItem item, UserItemData data)
        {
            if (_save.Invoke(_data, [user, item, data, UserDataSaveReason.Import, CancellationToken.None]) is Task pending)
            {
                pending.GetAwaiter().GetResult();
            }
        }
    }
}

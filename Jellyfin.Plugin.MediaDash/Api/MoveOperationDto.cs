using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>A move and its recorded recovery locations, without private ownership tokens.</summary>
public sealed class MoveOperationDto
{
    /// <summary>Gets or sets the durable operation id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the originating issue id.</summary>
    public long IssueId { get; set; }

    /// <summary>Gets or sets the recorded execution or recovery state.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets the explanation for work requiring review.</summary>
    public string? Detail { get; set; }

    /// <summary>Gets or sets the linked History row when persistence succeeded.</summary>
    public long? HistoryId { get; set; }

    /// <summary>Gets or sets the approved original locations.</summary>
    public IReadOnlyList<string> Sources { get; set; } = [];

    /// <summary>Gets or sets the approved destination locations.</summary>
    public IReadOnlyList<string> Destinations { get; set; } = [];

    /// <summary>Gets or sets recorded staging and recovery locations that may need review.</summary>
    public IReadOnlyList<string> RecoveryPaths { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether cleanup durably completed.</summary>
    public bool CleanupComplete { get; set; }

    /// <summary>Gets or sets a value indicating whether complete Undo is available for this operation.</summary>
    public bool CanUndo { get; set; }

    /// <summary>Gets or sets a value indicating whether this record reverses an earlier move.</summary>
    public bool IsUndo { get; set; }

    /// <summary>Gets or sets a value indicating whether watched state awaits a completed native library scan.</summary>
    public bool UserStatePending { get; set; }

    internal static MoveOperationDto FromOperation(MediaMoveStore.Operation operation)
    {
        var manifest = MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id);
        return new MoveOperationDto
        {
            Id = operation.Id, IssueId = operation.IssueId, State = operation.State, Detail = operation.Detail,
            HistoryId = operation.HistoryId, CleanupComplete = operation.CleanupComplete,
            CanUndo = operation is { State: "Completed", CleanupComplete: true } && manifest is { UndoOf: null } && manifest.HasCompletePayloads(),
            IsUndo = manifest?.UndoOf is not null,
            UserStatePending = operation.State == "Completed" && manifest?.UserData.Any(snapshot => !snapshot.Applied) == true,
            Sources = manifest?.Plan.Entries.Select(entry => entry.Source).ToArray() ?? [],
            Destinations = manifest?.Plan.Entries.Select(entry => entry.Destination).ToArray() ?? [],
            RecoveryPaths = operation.CleanupComplete ? [] : manifest?.Steps.SelectMany(step => new[] { step.Stage, step.Recovery }).ToArray() ?? []
        };
    }
}

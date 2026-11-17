using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Persisted progress and uniquely owned staging/recovery names for one operation.</summary>
internal sealed record MediaMoveManifest
{
    public int Version { get; init; } = 1;

    public MediaMovePlan Plan { get; init; } = new();

    public Step[] Steps { get; init; } = [];

    public OwnedDirectory[] CreatedDirectories { get; init; } = [];

    public OwnedDirectory[] DestinationParents { get; init; } = [];

    public MediaMovePlan.DirectoryScope[] FinalScopes { get; init; } = [];

    public MediaMoveUserData.Snapshot[] UserData { get; init; } = [];

    public bool CleanupComplete { get; init; }

    public string? UndoOf { get; init; }

    public MediaMovePathClaims.Claim[]? NativeClaims { get; init; }

    internal static MediaMoveManifest Create(MediaMovePlan plan, string id)
    {
        var manifest = new MediaMoveManifest
        {
            Plan = plan,
            DestinationParents = plan.DestinationParents.Concat(CaptureDestinationParents(plan.Entries))
                .DistinctBy(directory => directory.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray(),
            Steps = plan.Entries.Select((entry, index) => new Step(
                NamedPath(entry.Destination, id, index, "stage"),
                NamedPath(entry.Source, id, index, "source"),
                "Prepared",
                [],
                [],
                [])).ToArray()
        };
        return manifest with { NativeClaims = MediaMovePathClaims.Capture(manifest.NativeClaimPaths()) };
    }

    internal string[] NativeClaimPaths() => Plan.Entries.SelectMany(entry => new[] { entry.Source, entry.Destination })
        .Concat(Steps.SelectMany(step => new[] { step.Stage, step.Recovery }))
        .Append(Plan.DestinationContainer).Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.Ordinal).ToArray();

    internal static OwnedDirectory[] CaptureDestinationParents(MediaMovePlan.Entry[] entries) => entries.Select(entry => ExistingParent(entry.Destination)).Distinct(StringComparer.Ordinal)
            .Select(path => Probing.PhysicalFileIdentity.TryRead(path, out var identity) && !identity.IsReparsePoint
                ? new OwnedDirectory(path, identity.Volume, identity.FileId)
                : throw new IOException("The destination parent identity could not be verified.")).ToArray();

    internal static string ClaimContainer(string destination, bool directory)
    {
        var container = directory ? destination : Path.GetDirectoryName(destination)!;
        while (Path.GetDirectoryName(container) is { } parent && !Directory.Exists(parent))
        {
            container = parent;
        }

        return container;
    }

    internal string Serialize() => JsonSerializer.Serialize(this, MediaMovePlan.JsonOptions);

    internal bool HasCompletePayloads() => Steps.Length == Plan.Entries.Length && Steps.Select((step, index) =>
        step.Phase == "Complete" && SamePayload(Plan.Entries[index].Inventory, step.FinalInventory)).All(complete => complete);

    private static bool SamePayload(MediaMovePlan.Payload[] expected, MediaMovePlan.Payload[] actual) =>
        expected.Length == actual.Length && expected.Zip(actual).All(pair => pair.First is not null && pair.Second is not null
            && pair.First with { Volume = 0, FileId = 0 } == pair.Second with { Volume = 0, FileId = 0 });

    internal string[] ClaimPaths() => Plan.Entries.SelectMany(entry => new[] { entry.Source, entry.Destination })
        .Concat(Steps.SelectMany(step => new[] { step.Stage, step.Recovery }))
        .Concat(CreatedDirectories.Select(directory => directory.Path))
        .Append(Plan.DestinationContainer).Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.Ordinal).ToArray();

    internal static MediaMoveManifest? TryRead(string json, string id)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<MediaMoveManifest>(json, MediaMovePlan.JsonOptions);
            if (manifest is null || manifest.Version != 1 || manifest.Plan is null || !manifest.Plan.HasValidDefinition()
                || manifest.Steps is null || manifest.CreatedDirectories is null || manifest.DestinationParents is null || manifest.FinalScopes is null || manifest.UserData is null || manifest.UserData.Length > 65536 || manifest.Steps.Length != manifest.Plan.Entries.Length
                || !Guid.TryParseExact(id, "N", out _))
            {
                return null;
            }

            for (var index = 0; index < manifest.Steps.Length; index++)
            {
                var step = manifest.Steps[index];
                var entry = manifest.Plan.Entries[index];
                if (step is null || step.Stage != NamedPath(entry.Destination, id, index, "stage")
                    || step.Recovery != NamedPath(entry.Source, id, index, "source")
                    || step.Phase is not ("Prepared" or "Copying" or "StageVerified" or "Publishing" or "Published" or "SourceCapturing" or "SourceCaptured" or "Complete" or "RollingBack" or "RolledBack")
                    || step.StageInventory is null || step.RecoveryInventory is null || step.FinalInventory is null)
                {
                    return null;
                }
            }

            if (manifest.CreatedDirectories.Any(directory => directory is null || !Path.IsPathFullyQualified(directory.Path)
                || string.IsNullOrEmpty(manifest.Plan.DestinationContainer) || !LibraryGuard.IsUnder(directory.Path, manifest.Plan.DestinationContainer)))
            {
                return null;
            }

            if (manifest.NativeClaims is not null && !MediaMovePathClaims.Valid(manifest.NativeClaims, manifest.NativeClaimPaths()))
            {
                return null;
            }

            if (manifest.DestinationParents.Any(directory => directory is null || !Path.IsPathFullyQualified(directory.Path)
                || !manifest.Plan.Entries.Any(entry => LibraryGuard.IsUnder(Path.GetDirectoryName(entry.Destination)!, directory.Path))))
            {
                return null;
            }

            if (manifest.FinalScopes.Any(scope => scope is null || !Path.IsPathFullyQualified(scope.Path) || string.IsNullOrEmpty(scope.Fingerprint)
                || !manifest.Plan.Entries.Any(entry => !entry.IsDirectory
                    && string.Equals(Path.GetDirectoryName(entry.Destination), scope.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))))
            {
                return null;
            }

            if (manifest.UserData.Any(snapshot => snapshot is null || snapshot.ItemId == Guid.Empty || snapshot.UserId == Guid.Empty || snapshot.Data is null
                || !Path.IsPathFullyQualified(snapshot.SourcePath) || !Path.IsPathFullyQualified(snapshot.DestinationPath)
                || !(manifest.Plan.ItemBindings.Any(binding => binding.ItemId == snapshot.ItemId && binding.Path == snapshot.SourcePath)
                    || (manifest.Plan.ItemId == snapshot.ItemId && manifest.Plan.ItemPath == snapshot.SourcePath))
                || !manifest.Plan.Entries.Any(entry => LibraryGuard.IsUnder(snapshot.SourcePath, entry.Source)
                    && string.Equals(snapshot.DestinationPath, Path.GetFullPath(Path.Combine(entry.Destination, Path.GetRelativePath(entry.Source, snapshot.SourcePath))), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))))
            {
                return null;
            }

            return manifest;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static string NamedPath(string path, string id, int index, string suffix) => Path.Combine(
        Path.GetDirectoryName(path)!,
        ".mediaDash.move-" + id + "-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + suffix);

    private static string ExistingParent(string destination)
    {
        var path = Path.GetDirectoryName(destination);
        while (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
        {
            path = Path.GetDirectoryName(path);
        }

        if (string.IsNullOrEmpty(path))
        {
            throw new IOException("The destination has no verifiable existing parent.");
        }

        MediaMoveSnapshot.ValidateAncestors(path);
        return path;
    }

    internal sealed record Step(string Stage, string Recovery, string Phase, MediaMovePlan.Payload[] StageInventory, MediaMovePlan.Payload[] RecoveryInventory, MediaMovePlan.Payload[] FinalInventory);

    internal sealed record OwnedDirectory(string Path, ulong Volume, ulong FileId);
}

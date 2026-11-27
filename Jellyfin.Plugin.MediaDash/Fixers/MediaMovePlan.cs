using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>The complete source and destination inventory shown for one move approval.</summary>
internal sealed record MediaMovePlan
{
    /// <summary>Container identity of a film leaving a show's folder; the show's own files stay behind.</summary>
    internal const string FilmFromShowPrefix = "film-from-show:";

    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Version { get; init; } = 1;

    public Guid ItemId { get; init; }

    public string ItemPath { get; init; } = string.Empty;

    public string CatalogPath { get; init; } = string.Empty;

    public IssueType Type { get; init; }

    public string Identity { get; init; } = string.Empty;

    public string ContainerIdentity { get; init; } = string.Empty;

    public string DestinationContainer { get; init; } = string.Empty;

    public MediaMoveManifest.OwnedDirectory[] DestinationParents { get; init; } = [];

    public DirectoryScope[] SourceScopes { get; init; } = [];

    public ItemBinding[] ItemBindings { get; init; } = [];

    public string PolicyStamp { get; init; } = string.Empty;

    public Entry[] Entries { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Entry? SourceCleanup { get; init; }

    public string[] BlockingReasons { get; init; } = [];

    public string Fingerprint { get; init; } = string.Empty;

    public bool IndependentFiles { get; init; }

    internal bool IsGenericSelection => Type == IssueType.ManualMove && IndependentFiles;

    internal bool KeepsSharedDirectories => !Entries[0].IsDirectory
        && ((Type == IssueType.Ungrouped && ContainerIdentity.StartsWith("series-folder:", StringComparison.Ordinal))
            || ContainerIdentity.StartsWith(FilmFromShowPrefix, StringComparison.Ordinal));

    internal MediaMovePlan Seal() => this with { Fingerprint = Hash(JsonSerializer.Serialize(this with { Fingerprint = string.Empty }, JsonOptions)) };

    internal bool HasValidFingerprint() => Version == 1 && Fingerprint.Length == 64 && Seal().Fingerprint == Fingerprint;

    internal static MediaMovePlan? ReadIssue(Issue issue)
    {
        try
        {
            using var document = JsonDocument.Parse(issue.DetailsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("movePlan", out var value))
            {
                return null;
            }

            var plan = value.Deserialize<MediaMovePlan>(JsonOptions);
            return plan is not null && plan.HasValidDefinition() && plan.ItemId == issue.ItemId && plan.Type == issue.Type
                && string.Equals(plan.Entries[0].Source, issue.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? plan : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    internal bool HasValidDefinition()
    {
        try
        {
            if (Fingerprint is null || ItemPath is null || CatalogPath is null || Identity is null || ContainerIdentity is null || PolicyStamp is null || DestinationContainer is null
                || Entries is not { Length: > 0 and <= 4096 } || BlockingReasons is null || DestinationParents is null || SourceScopes is null || ItemBindings is null
                || !HasValidFingerprint() || Type is not (IssueType.Misplaced or IssueType.Ungrouped or IssueType.ManualMove)
                || (IndependentFiles && (Type != IssueType.ManualMove || ItemId != Guid.Empty)))
            {
                return false;
            }

            if (CatalogPath.Length > 0 && (Type != IssueType.ManualMove || ItemId == Guid.Empty || !Canonical(CatalogPath)))
            {
                return false;
            }

            if ((!string.IsNullOrEmpty(ItemPath) && (!Canonical(ItemPath) || !Entries.Any(entry => entry is not null && LibraryGuard.IsUnder(ItemPath, entry.Source))))
                || DestinationParents.Any(parent => parent is null || !Canonical(parent.Path)
                    || !Entries.Any(entry => entry is not null && Canonical(entry.Destination) && LibraryGuard.IsUnder(Path.GetDirectoryName(entry.Destination)!, parent.Path))))
            {
                return false;
            }

            var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (ItemBindings.Length > 4096 || ItemBindings.Select(binding => binding?.ItemId).Distinct().Count() != ItemBindings.Length
                || ItemBindings.Any(binding => binding is null || binding.ItemId == Guid.Empty || !Canonical(binding.Path) || !HexHash(binding.Identity)
                    || !Entries.Any(entry => entry is not null && MediaMovePlanner.Overlap(binding.Path, entry.Source))))
            {
                return false;
            }

            if (SourceScopes.Any(scope => scope is null || !Canonical(scope.Path) || !HexHash(scope.Fingerprint)
                || !Entries.Any(entry => entry is not null && !entry.IsDirectory && paths.Equals(Path.GetDirectoryName(entry.Source), scope.Path))))
            {
                return false;
            }

            if (SourceCleanup is { } cleanup && (Type != IssueType.Ungrouped || !KeepsSharedDirectories || !cleanup.IsDirectory
                || !LibraryGuard.IsUnder(Entries[0].Source, cleanup.Source)
                || !Entries.All(entry => LibraryGuard.IsUnder(entry.Destination, cleanup.Destination))))
            {
                return false;
            }

            foreach (var entry in SourceCleanup is null ? Entries : Entries.Append(SourceCleanup))
            {
                if (entry is null || !Canonical(entry.Source) || !Canonical(entry.Destination)
                    || MediaMovePlanner.Overlap(entry.Source, entry.Destination)
                    || entry.Inventory is not { Length: > 0 } || entry.Inventory[0] is not { RelativePath.Length: 0 } root
                    || root.IsDirectory != entry.IsDirectory || (!entry.IsDirectory && entry.Inventory.Length != 1))
                {
                    return false;
                }

                var names = new System.Collections.Generic.HashSet<string>(paths);
                foreach (var payload in entry.Inventory)
                {
                    if (payload is null || payload.RelativePath is null || !names.Add(payload.RelativePath)
                        || payload.Metadata is null || payload.Length < 0 || payload.LastWriteUtcTicks < 0 || payload.LastWriteUtcTicks > DateTime.MaxValue.Ticks
                        || (payload.IsDirectory ? payload.Sha256 is not { Length: 0 } : !(payload.Sha256 is { Length: 0 } || HexHash(payload.Sha256))))
                    {
                        return false;
                    }

                    if (payload.RelativePath.Length > 0 && (Path.IsPathRooted(payload.RelativePath)
                        || Path.GetRelativePath(entry.Source, Path.GetFullPath(Path.Combine(entry.Source, payload.RelativePath))) != payload.RelativePath
                        || !LibraryGuard.IsUnder(Path.GetFullPath(Path.Combine(entry.Source, payload.RelativePath)), entry.Source)))
                    {
                        return false;
                    }
                }
            }

            for (var first = 0; first < Entries.Length; first++)
            {
                for (var second = first + 1; second < Entries.Length; second++)
                {
                    var left = Entries[first];
                    var right = Entries[second];
                    if (MediaMovePlanner.Overlap(left.Source, right.Source) || MediaMovePlanner.Overlap(left.Destination, right.Destination)
                        || MediaMovePlanner.Overlap(left.Source, right.Destination) || MediaMovePlanner.Overlap(left.Destination, right.Source))
                    {
                        return false;
                    }
                }
            }

            return string.IsNullOrEmpty(DestinationContainer) || (Canonical(DestinationContainer)
                && Entries.All(entry => LibraryGuard.IsUnder(entry.Destination, DestinationContainer)));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool Canonical(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && Path.GetFullPath(path) == path;

    private static bool HexHash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal sealed record Entry(string Source, string Destination, bool IsDirectory, Payload[] Inventory);

    internal sealed record DirectoryScope(string Path, string Fingerprint);

    internal sealed record ItemBinding(Guid ItemId, string Path, string Identity);

    internal sealed record Payload(string RelativePath, bool IsDirectory, long Length, long LastWriteUtcTicks, ulong Volume, ulong FileId, string Sha256, string Metadata = "");
}

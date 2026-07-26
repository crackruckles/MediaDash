using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Result of a post-upgrade sweep — the numbers the UI shows to the user.</summary>
/// <param name="OrphanedFoldersDeleted">How many trickplay folders had no matching item.</param>
/// <param name="BytesFreed">Total disk reclaimed.</param>
/// <param name="Errors">Per-folder errors, if any (permission denied on one folder, etc.).</param>
public sealed record PostUpgradeCleanupResult(int OrphanedFoldersDeleted, long BytesFreed, IReadOnlyList<string> Errors);

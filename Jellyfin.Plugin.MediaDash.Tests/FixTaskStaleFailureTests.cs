using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Presentation-only inventory of the canonical "stale-failure" phrases each fixer emits to the
/// user. Before 01-B this file drove control flow via <c>FixTask.IsStaleFailure(message)</c>;
/// that classifier is gone, and scheduling now consults <see cref="Jellyfin.Plugin.MediaDash.Fixers.FixResultPolicy"/>
/// through the fixer's typed <c>Outcome</c>+<c>Reason</c>. What remains here is a documentation
/// contract: for each canonical phrase, at least one production fixer file must still emit it
/// verbatim so the user-facing message the audit trail was written against does not silently
/// disappear during a rewording. If a phrase legitimately moves out of a fixer, delete its row
/// (and the accompanying audit log entry) rather than weakening the assertion.
/// </summary>
public sealed class FixTaskStaleFailureTests
{
    // The exact substrings the deleted IsStaleFailure classifier used to key off, mapped to the
    // production files that emit them. Each substring must appear literally in at least one of the
    // listed files. Reordering / rewording either side without also updating this table means the
    // audit-trail wording is drifting silently — the test fails and forces a bilateral update.
    private static readonly (string Substring, string[] OwnerFiles)[] CanonicalPhrases =
    {
        ("no longer exists", new[]
        {
            "Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/EmbeddedCoverArtFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/MediaGrouperFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/MediaSorterFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/NfoFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/OrphanCleanupFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/SubtitleFontFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/SuspiciousFileFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/TrickplayOptimizeFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/DuplicateFixer.cs"
        }),
        ("Nothing to remove any more", new[]
        {
            "Jellyfin.Plugin.MediaDash/Fixers/SubtitleFontFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/OrphanCleanupFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/TrickplayOptimizeFixer.cs"
        }),
        ("plays fine now", new[] { "Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs" }),
        // Reworded 2026-09-25 from "same name already exists" to be clearer about which copy is which
        // and offer explicit next steps. The pinned substring is the durable part — "already exists at"
        // — which both fixers still emit when a destination file/folder occupies the target path.
        ("already exists at", new[]
        {
            "Jellyfin.Plugin.MediaDash/Fixers/MediaMoveService.cs"
        }),
        ("Resolve the collision manually and re-scan", new[] { "Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs" }),
        ("folder cover already exists at", new[] { "Jellyfin.Plugin.MediaDash/Fixers/EmbeddedCoverArtFixer.cs" }),
        ("DRM-protected", new[] { "Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs" }),
        ("Refused: not an .ass", new[] { "Jellyfin.Plugin.MediaDash/Fixers/SubtitleFontFixer.cs" }),
        ("not a recognised Jellyfin trickplay folder", new[] { "Jellyfin.Plugin.MediaDash/Fixers/TrickplayOptimizeFixer.cs" }),
        ("requires re-encoding the audio", new[] { "Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs" }),
        ("Combined pass requires exactly one AudioLanguage", new[] { "Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs" }),
        ("Combined pass requires both issues to point at the same file", new[] { "Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs" }),
        ("outside your library", new[]
        {
            "Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/EmbeddedCoverArtFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/MediaGrouperFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/MediaSorterFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/OrphanCleanupFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/SubtitleFontFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/SuspiciousFileFixer.cs",
            "Jellyfin.Plugin.MediaDash/Fixers/DuplicateFixer.cs"
        }),
        ("not inside a configured library", new[] { "Jellyfin.Plugin.MediaDash/Fixers/NfoFixer.cs" })
    };

    [Fact]
    public void EveryCanonicalStalePhrase_IsStillEmittedByAtLeastOneOwningFixer()
    {
        var root = RepoRoot();
        var missing = new List<string>();

        foreach (var (substring, ownerFiles) in CanonicalPhrases)
        {
            var found = false;
            foreach (var relativePath in ownerFiles)
            {
                var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                {
                    continue;
                }

                if (File.ReadAllText(full).Contains(substring, System.StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                missing.Add(substring);
            }
        }

        Assert.True(
            missing.Count == 0,
            "Canonical stale-failure phrase(s) no longer appear in any of the listed fixer files: "
            + string.Join(" | ", missing)
            + ". If the wording was intentionally changed, update CanonicalPhrases (and the audit trail) at the same time.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.MediaDash.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

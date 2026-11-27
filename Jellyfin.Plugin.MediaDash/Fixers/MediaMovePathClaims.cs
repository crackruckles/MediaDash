using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Probing;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Native anchors for move/restore names, including destinations that do not exist yet.</summary>
internal static class MediaMovePathClaims
{
    private static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Canonical metadata-only existence and native identity checks; callers validate library/restore scope. No payload is read or mutated. Capturing ancestors is required to reserve absent destination names and refuse aliases.")]
    internal static Claim[] Capture(IEnumerable<string> paths)
    {
        var identities = new Dictionary<string, PhysicalFileIdentity.Value>(Comparer);
        var claims = new List<Claim>();
        foreach (var path in paths.Distinct(Comparer))
        {
            if (!MediaMovePlan.Canonical(path))
            {
                throw new IOException("Not moved: one of this move's paths is written in an unexpected form (for example through a shortcut). Run a scan to get an up-to-date plan, then approve it again.");
            }

            var count = claims.Count;
            for (var ancestor = path; !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
            {
                if (!identities.TryGetValue(ancestor, out var identity))
                {
                    if (!File.Exists(ancestor) && !Directory.Exists(ancestor))
                    {
                        continue;
                    }

                    if (!PhysicalFileIdentity.TryRead(ancestor, out identity) || identity.IsReparsePoint || identity.FileId == 0)
                    {
                        throw new IOException("Not moved: MediaDash could not confirm that one of this move's folders is the same one it checked earlier. Run a scan to get an up-to-date plan, then approve it again.");
                    }

                    identities.Add(ancestor, identity);
                }

                claims.Add(new(path, ancestor, identity.Volume, identity.FileId, Relative(ancestor, path)));
            }

            if (claims.Count == count)
            {
                throw new IOException("Not moved: one of the folders this move needs could not be read. Check that the drive is connected and that Jellyfin can read it, then run a scan.");
            }
        }

        return claims.ToArray();
    }

    internal static bool Valid(Claim[]? claims, IEnumerable<string> paths)
    {
        if (claims is not { Length: > 0 } || claims.Length > 1048576)
        {
            return false;
        }

        try
        {
            var allowed = paths.ToHashSet(Comparer);
            return claims.All(claim => claim is not null && claim.FileId != 0 && allowed.Contains(claim.Path)
                && MediaMovePlan.Canonical(claim.Anchor) && LibraryGuard.IsUnder(claim.Path, claim.Anchor)
                && claim.RelativePath == Relative(claim.Anchor, claim.Path))
                && allowed.SetEquals(claims.Select(claim => claim.Path));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    internal static Claim[]? Read(string? json, IEnumerable<string> paths)
    {
        try
        {
            var claims = json is null ? null : JsonSerializer.Deserialize<Claim[]>(json, MediaMovePlan.JsonOptions);
            return Valid(claims, paths) ? claims : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    internal static bool Overlap(Claim[]? first, Claim[]? second)
    {
        if (first is null || second is null)
        {
            return true;
        }

        var anchors = first.ToLookup(claim => (claim.Volume, claim.FileId));
        return second.Any(right => anchors[(right.Volume, right.FileId)].Any(left => Within(left.RelativePath, right.RelativePath) || Within(right.RelativePath, left.RelativePath)));
    }

    private static bool Within(string path, string parent) => parent.Length == 0 || Comparer.Equals(path, parent)
        || path.StartsWith(parent + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Relative(string anchor, string path)
    {
        var relative = Path.GetRelativePath(anchor, path);
        return relative == "." ? string.Empty : relative;
    }

    internal sealed record Claim(string Path, string Anchor, ulong Volume, ulong FileId, string RelativePath);
}

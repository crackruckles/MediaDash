using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Safety invariant #1: MediaDash never modifies or deletes a file outside the configured library folders.
/// Every fixer must pass its target through <see cref="IsInsideLibrary"/> before touching it.
/// </summary>
public sealed class LibraryGuard
{
    private static readonly string[] SidecarPatterns =
    [
        "*.mediadash.tmp*",
        "*.mediadash.new*",
        "*.mediadash.swap*",
        "*.mediadash.strip*",
        "*.mediadash.upload.tmp",
        "mediadash.tmp.*",
        "mediadash.new.*"
    ];

    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryGuard"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    public LibraryGuard(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Checks whether a path is inside one of the server's library folders.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True when the path is inside a library.</returns>
    public bool IsInsideLibrary(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var inSomeLibrary = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Any(location => IsUnder(fullPath, location));
        if (!inSomeLibrary)
        {
            return false;
        }

        // Lexical check passed; now defend against symlinks / NTFS junctions. Path.GetFullPath is purely
        // lexical, so a link planted inside a library that targets /etc or C:\Windows slips through.
        // Cheapest safe stance: refuse when any ancestor is a reparse point. Legitimate library trees
        // don't need links; hostile multi-tenant hosts (the actual threat model) never should.
        // ponytail: refuse-any-link is conservative; upgrade to per-target resolution if a real
        // deployment needs symlinks inside a library.
        try
        {
            return !HasReparsePointAncestor(fullPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "This method IS the security check — inspecting the caller-supplied path for reparse points is its purpose. Callers pass here specifically to validate the path before touching it.")]
    private static bool HasReparsePointAncestor(string fullPath)
    {
        var current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            FileSystemInfo? info = File.Exists(current) ? new FileInfo(current)
                : Directory.Exists(current) ? new DirectoryInfo(current)
                : null;

            if (info is not null && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>
    /// Walks every configured library root and deletes any MediaDash sidecar file
    /// (<c>*.mediadash.tmp*</c>, <c>*.mediadash.new*</c>, or the hash-fallback variants).
    /// Intended to run at end of a fix cycle when no encode is active — anything present is orphaned
    /// from a crash or a hard-killed plugin instance where <c>TranscodeFixer.FixAsync</c>'s finally
    /// block didn't get to run.
    /// </summary>
    /// <returns>Path and byte-count of each deleted orphan, empty on a clean library.</returns>
    public IReadOnlyList<(string Path, long Bytes)> SweepOrphanSidecars()
    {
        var deleted = new List<(string, long)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _libraryManager.GetVirtualFolders().SelectMany(f => f.Locations))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            // AttributesToSkip = ReparsePoint defends against symlink cycles under a library root —
            // /movies/all -> /movies would otherwise spin the sweep until thread-pool starvation on
            // every FixTask cycle. IgnoreInaccessible ensures one unreadable subfolder doesn't abort
            // the whole sweep mid-walk.
            var enumOpts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            };
            foreach (var pattern in SidecarPatterns)
            {
                IEnumerable<string> matches;
                try
                {
                    matches = Directory.EnumerateFiles(root, pattern, enumOpts);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var path in matches)
                {
                    if (!seen.Add(path))
                    {
                        continue;
                    }

                    // Defense in depth: enumeration is scoped to library roots, but re-check on the
                    // exact path before deletion. Every other destructive path in the plugin passes
                    // through IsInsideLibrary; leaving this one out is the only inconsistency.
                    if (!IsInsideLibrary(path))
                    {
                        continue;
                    }

                    try
                    {
                        var size = new FileInfo(path).Length;
                        File.Delete(path);
                        deleted.Add((path, size));
                    }
                    catch (Exception)
                    {
                        // Best effort; file may vanish between enumeration and delete, or a permission bump
                        // may block us. Nothing to do — the next cycle picks it up.
                    }
                }
            }
        }

        return deleted;
    }

    internal static bool IsUnder(string fullPath, string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!fullPath.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return false;
        }

        // If fullRoot already ends with a separator (i.e. it's a Windows drive root like "C:\" — .NET's
        // TrimEndingDirectorySeparator preserves that trailing slash because "C:" alone isn't a valid
        // rooted path), any StartsWith match is inherently a boundary match. Skipping the boundary
        // check here fixes SMART/library-drive detection on Windows: previously
        // IsUnder("C:\\Users\\...", "C:\\") returned false because fullPath[3] was 'U' (from "Users"),
        // not '\\', so FindDriveForPath returned null for every subpath of a drive root.
        if (fullRoot.Length > 0
            && (fullRoot[^1] == Path.DirectorySeparatorChar || fullRoot[^1] == Path.AltDirectorySeparatorChar))
        {
            return true;
        }

        // "D:\Movies2\x" must not match root "D:\Movies".
        return fullPath.Length == fullRoot.Length || fullPath[fullRoot.Length] == Path.DirectorySeparatorChar || fullPath[fullRoot.Length] == Path.AltDirectorySeparatorChar;
    }
}

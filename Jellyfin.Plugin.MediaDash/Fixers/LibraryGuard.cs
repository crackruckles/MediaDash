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
    private static readonly string[] SidecarPatterns = ["*.mediadash.tmp*", "*.mediadash.new*", "mediadash.tmp.*", "mediadash.new.*"];

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
        return _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Any(location => IsUnder(fullPath, location));
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

            foreach (var pattern in SidecarPatterns)
            {
                IEnumerable<string> matches;
                try
                {
                    matches = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
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

        // "D:\Movies2\x" must not match root "D:\Movies".
        return fullPath.Length == fullRoot.Length || fullPath[fullRoot.Length] == Path.DirectorySeparatorChar || fullPath[fullRoot.Length] == Path.AltDirectorySeparatorChar;
    }
}

using System;
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

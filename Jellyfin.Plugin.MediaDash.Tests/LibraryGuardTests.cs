using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class LibraryGuardTests
{
    private static string P(params string[] parts) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), Path.Combine(parts)));

    [Fact]
    public void FileInsideLibraryIsAllowed()
    {
        Assert.True(LibraryGuard.IsUnder(P("movies", "film", "film.mkv"), P("movies")));
    }

    [Fact]
    public void SiblingFolderWithSamePrefixIsRejected()
    {
        // "…\movies2\x" must not match library root "…\movies".
        Assert.False(LibraryGuard.IsUnder(P("movies2", "film.mkv"), P("movies")));
    }

    [Fact]
    public void CompletelyOutsidePathIsRejected()
    {
        Assert.False(LibraryGuard.IsUnder(P("other", "film.mkv"), P("movies")));
    }

    [Fact]
    public void TrailingSeparatorOnRootIsHandled()
    {
        Assert.True(LibraryGuard.IsUnder(P("movies", "film.mkv"), P("movies") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void PathTraversalAttemptIsRejected()
    {
        // File browser: user posts a path with ".." to escape the library. GetFullPath must be called first
        // so that '../etc/passwd' relative to a library folder resolves to somewhere outside the root before check.
        var libraryRoot = P("movies");
        var attackerPath = Path.GetFullPath(Path.Combine(libraryRoot, "..", "..", "etc", "passwd"));
        Assert.False(LibraryGuard.IsUnder(attackerPath, libraryRoot));
    }
}

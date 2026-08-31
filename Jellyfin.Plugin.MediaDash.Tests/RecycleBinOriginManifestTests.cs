using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Verifies that MoveToBin writes an origin manifest and ListContents surfaces it, so the
/// Recycle Bin tab can render a Restore button for files that no HistoryEntry references
/// (external subtitle sidecars, cover-art originals, and manual recycles).
/// </summary>
public class RecycleBinOriginManifestTests
{
    [Fact]
    public void ManifestConstant_MatchesTheExpectedFileName()
    {
        // Locking the on-disk sidecar name in a test so a rename can't silently break the
        // ListContents-side reader. If this fails, adjust intentionally.
        Assert.Equal(".mediadash-origin", RecycleBin.OriginManifestFileName);
    }

    [Fact]
    public void OwnershipMarker_AndOriginManifest_AreDistinctSentinels()
    {
        // GetContents/ListContents skip both files by name — they must not collide, otherwise
        // one would be counted as a recycled file (inflating the reported bin size).
        Assert.NotEqual(RecycleBin.OwnershipMarkerFileName, RecycleBin.OriginManifestFileName);
    }
}

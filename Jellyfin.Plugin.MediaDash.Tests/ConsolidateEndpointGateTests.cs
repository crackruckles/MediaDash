using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// The Consolidate endpoint takes a filesystem path from an admin-only body — safe by policy,
/// but defence-in-depth requires the controller to gate the path against DiscoverOtherBinRoots.
/// These source-shape checks make sure the gate stays wired the next time somebody refactors.
/// </summary>
public class ConsolidateEndpointGateTests
{
    [Fact]
    public void EndpointGatesSourceRootAgainstDiscoverOtherBinRoots()
    {
        var src = File.ReadAllText(RepoFile("Jellyfin.Plugin.MediaDash", "Api", "MediaDashController.cs"));
        var start = src.IndexOf("ActionResult ConsolidateBin(", StringComparison.Ordinal);
        Assert.True(start > 0, "ConsolidateBin endpoint not found — has it been renamed?");
        var body = ExtractMethodBody(src, start);

        // Gate: must call DiscoverOtherBinRoots inside the endpoint before invoking the mover.
        Assert.Contains("DiscoverOtherBinRoots", body);
        // Must return NotFound() for unknown sources — protects against arbitrary paths.
        Assert.Contains("return NotFound()", body);
        // Must refuse empty SourceRoot with a BadRequest — no silent no-op.
        Assert.Contains("BadRequest", body);
    }

    [Fact]
    public void EndpointReturnsConsolidateResult_NotArbitraryDto()
    {
        // The frontend banner reads r.BatchesMoved / r.BatchesSkipped / r.BytesMoved. Pin the
        // response type so a rename would fail the build and be caught in review.
        var src = File.ReadAllText(RepoFile("Jellyfin.Plugin.MediaDash", "Api", "MediaDashController.cs"));
        var start = src.IndexOf("ActionResult ConsolidateBin(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = ExtractMethodBody(src, start);
        Assert.Contains("new ConsolidateResult", body);
        Assert.Contains("BatchesMoved", body);
        Assert.Contains("BatchesSkipped", body);
        Assert.Contains("BytesMoved", body);
    }

    [Fact]
    public void DiscoverEndpointExists_ForFrontendBannerFetch()
    {
        var src = File.ReadAllText(RepoFile("Jellyfin.Plugin.MediaDash", "Api", "MediaDashController.cs"));
        Assert.Contains("[HttpGet(\"RecycleBin/OtherBins\")]", src);
        Assert.Contains("GetOtherBinLocations()", src);
    }

    private static string ExtractMethodBody(string src, int startIndex)
    {
        var braceStart = src.IndexOf('{', startIndex);
        var depth = 0;
        for (var i = braceStart; i < src.Length; i++)
        {
            if (src[i] == '{')
            {
                depth++;
            }
            else if (src[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return src.Substring(braceStart, i - braceStart + 1);
                }
            }
        }

        return src.Substring(braceStart);
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.MediaDash.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }
}

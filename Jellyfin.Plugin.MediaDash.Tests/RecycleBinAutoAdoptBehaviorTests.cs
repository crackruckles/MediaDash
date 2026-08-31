using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// The startup adoption pass is Plugin.Instance-bound so can't be exercised end-to-end without a
/// full plugin harness. These source-shape assertions pin the behavior change from
/// "emit LegacyBatchNeedsReview diagnostic and wait" to "write the ownership marker directly".
/// If someone reverts to the review workflow, the test fires.
/// </summary>
public class RecycleBinAutoAdoptBehaviorTests
{
    [Fact]
    public void AdoptLegacyCustomBatches_NoLongerEmitsReviewDiagnostic()
    {
        var src = File.ReadAllText(RepoFile("Jellyfin.Plugin.MediaDash", "Fixers", "RecycleBin.cs"));

        // Locate the AdoptLegacyCustomBatches method by name; then assert the LegacyBatchNeedsReview
        // string doesn't appear inside its body. Cheap approximate scan — the whole method fits
        // between "AdoptLegacyCustomBatches" and the next top-level "private" / "public".
        var start = src.IndexOf("private void AdoptLegacyCustomBatches", StringComparison.Ordinal);
        Assert.True(start > 0, "AdoptLegacyCustomBatches method not found — has it been renamed?");
        // Rough method-end delimiter: the closing "}" of the method. Walk forward and count braces.
        var body = ExtractMethodBody(src, start);
        Assert.DoesNotContain("LegacyBatchNeedsReview", body);
    }

    [Fact]
    public void AdoptLegacyCustomBatches_WritesOwnershipMarker_ForShapeMatchingBatches()
    {
        var src = File.ReadAllText(RepoFile("Jellyfin.Plugin.MediaDash", "Fixers", "RecycleBin.cs"));
        var start = src.IndexOf("private void AdoptLegacyCustomBatches", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = ExtractMethodBody(src, start);

        // Positive assertion: the auto-adopt path writes the ownership marker file.
        Assert.Contains("File.Create(marker)", body);
        Assert.Contains("Auto-adopted legacy recycle batch", body);
    }

    [Fact]
    public void PurgeObsolete_SweepsStaleLegacyBatchNeedsReviewRows()
    {
        // Users upgrading from a prior build have the diagnostic persisted in their SQLite
        // table. Startup must remove those so the Errors tab isn't polluted with rows that no
        // longer describe reality (the batches have been auto-adopted by the new pass).
        var src = File.ReadAllText(RepoFile("Jellyfin.Plugin.MediaDash", "Api", "Diagnostics.cs"));
        var start = src.IndexOf("public static void PurgeObsolete", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = ExtractMethodBody(src, start);
        Assert.Contains("RecycleBin.LegacyBatchNeedsReview", body);
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

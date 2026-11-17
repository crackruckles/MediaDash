using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// MD-010 regression: the tri-state revalidation contract that keeps the playability fixer from
/// disposing a healthy file when ffprobe is temporarily unavailable. These tests pin the record's
/// factory shape and the source-level contract that FixAsync consumes the Verdict via a switch and
/// reuses the returned probe in the repair ladder (previously the fix path re-probed and treated
/// null as "still broken" — the whole class of data-loss bugs this finding closes).
/// </summary>
public sealed class PlayabilityFixerRevalidationResultTests
{
    [Fact]
    public void IndeterminateCarriesDiagnosticAndNullProbe()
    {
        var r = RevalidationResult.Indeterminate("ffprobe missing");

        Assert.Equal(RevalidationVerdict.Indeterminate, r.Verdict);
        Assert.Null(r.Probe);
        Assert.Equal("ffprobe missing", r.Diagnostic);
    }

    [Fact]
    public void BrokenCarriesProbeAndNullDiagnostic()
    {
        var probe = new FfprobeData();
        var r = RevalidationResult.Broken(probe);

        Assert.Equal(RevalidationVerdict.Broken, r.Verdict);
        Assert.Same(probe, r.Probe);
        Assert.Null(r.Diagnostic);
    }

    [Fact]
    public void HealthyCarriesProbeAndNullDiagnostic()
    {
        var probe = new FfprobeData();
        var r = RevalidationResult.Healthy(probe);

        Assert.Equal(RevalidationVerdict.Healthy, r.Verdict);
        Assert.Same(probe, r.Probe);
        Assert.Null(r.Diagnostic);
    }

    // Source-level pins. Same fragile-but-effective pattern the FixTaskTranscodeCompanionTests
    // use — asserting the specific call shape that closes the MD-010 disposal path. If someone
    // regresses these, the fixer can silently start recycling healthy files again the next time
    // ffprobe stalls.
    [Fact]
    public void FixAsyncBranchesOnIndeterminateBeforeAnyDisposal()
    {
        var src = File.ReadAllText(PlayabilityFixerSourcePath());

        // Must switch on the tri-state verdict, not the old bool.
        Assert.Contains("case RevalidationVerdict.Indeterminate:", src);
        Assert.Contains("case RevalidationVerdict.Healthy:", src);
        Assert.Contains("PlayabilityFixer.RevalidateIndeterminate", src);

        // Must NOT still call the old bool-returning helper.
        Assert.DoesNotContain("IsStillBrokenAsync(", src);

        // Must record a diagnostic and refuse the fix on Indeterminate before touching the file.
        var indeterminateIndex = src.IndexOf("case RevalidationVerdict.Indeterminate:", System.StringComparison.Ordinal);
        var moveIndex = src.IndexOf("MoveToBin(issue.Path)", System.StringComparison.Ordinal);
        Assert.True(indeterminateIndex > 0 && moveIndex > indeterminateIndex, "Indeterminate branch must precede any disposal call.");
    }

    [Fact]
    public void RepairLadderReusesRevalidationProbeInsteadOfReprobing()
    {
        var src = File.ReadAllText(PlayabilityFixerSourcePath());

        // Repair ladder now takes the probe from RevalidateAsync — a second ProbeAsync call in
        // this branch is the specific bug MD-010 closes (a healthy revalidate followed by a null
        // repair probe used to slip the file straight through to disposal).
        Assert.Contains("revalidation.Probe is not null", src);
        Assert.Contains("TryRepairAsync(issue, revalidation.Probe", src);
    }

    [Fact]
    public void ScannerRevalidationAndRepairVerificationShareAllAudioCheck()
    {
        var source = File.ReadAllText(PlayabilityFixerSourcePath());
        Assert.Contains("FindBrokenAudioStreamAsync(issue.Path, probe, cancellationToken)", source);
        Assert.Contains("FindBrokenAudioStreamAsync(outputPath, outputProbe, cancellationToken)", source);
        var scanner = Path.Combine(Path.GetDirectoryName(PlayabilityFixerSourcePath())!, "..", "Scanners", "PlayabilityScanner.cs");
        Assert.Contains("FindBrokenAudioStreamAsync(path, probe, useCache: true, cancellationToken)", File.ReadAllText(scanner));
    }

    private static string PlayabilityFixerSourcePath()
    {
        var here = Path.GetDirectoryName(typeof(PlayabilityFixerRevalidationResultTests).Assembly.Location)!;
        // bin/Debug/net9.0 → repo root → source file
        var repo = Path.GetFullPath(Path.Combine(here, "..", "..", "..", ".."));
        return Path.Combine(repo, "Jellyfin.Plugin.MediaDash", "Fixers", "PlayabilityFixer.cs");
    }
}

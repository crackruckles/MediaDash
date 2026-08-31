using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Regression guard: every fixer that recycles a file must feed that RecyclePath into either
/// FixResult.RecyclePath (primary file) or FixResult.AdditionalRecycled (sidecars / additional
/// originals). Without a captured path there's no HistoryEntry row, and the Recycle Bin tab
/// renders "no history" instead of a Restore button — the user report class that motivated the
/// AdditionalRecycled plumbing.
/// </summary>
public class FixerMoveToBinParityTests
{
    private static readonly Regex StatementLevel =
        new(@"^\s*_recycleBin\.MoveToBin\(", RegexOptions.Compiled);

    // Api/FileBrowserController.Delete legitimately fires MoveToBin without a HistoryEntry — the
    // manifest sidecar covers restore, and user-initiated deletes shouldn't appear in the History
    // tab which is scoped to fix-run activity. Any NEW api-layer offender should still be reviewed.
    // Content-based instead of line-based so unrelated line shifts don't flake the test.
    private static readonly (string File, string Snippet)[] AllowedApiFireAndForget =
    {
        ("FileBrowserController.cs", "_recycleBin.MoveToBin(full);"),
    };

    [Fact]
    public void EveryMoveToBinCallInFixersCapturesTheReturnValue()
    {
        var offenders = FindFireAndForgetOffenders(Path.Combine(RepoRoot(), "Jellyfin.Plugin.MediaDash", "Fixers"));
        Assert.True(
            offenders.Count == 0,
            "Fire-and-forget _recycleBin.MoveToBin(...) call(s) in a fixer. The return value must be " +
            "captured and routed to FixResult.RecyclePath or FixResult.AdditionalRecycled so a " +
            "HistoryEntry records the RecyclePath. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EveryMoveToBinCallInApiControllersEitherCapturesTheReturnOrIsExplicitlyAllowed()
    {
        // Wider net: controllers can call MoveToBin during restore swaps, redownload flows, and
        // user-initiated deletes. Any new fire-and-forget site not on the allow-list means either
        // (a) a missing HistoryEntry that leaves the file undiscoverable in the History tab or
        // (b) an intentional case that should be documented in AllowedApiFireAndForget.
        var apiDir = Path.Combine(RepoRoot(), "Jellyfin.Plugin.MediaDash", "Api");
        var offenders = FindFireAndForgetOffenders(apiDir)
            .Where(o => !AllowedApiFireAndForget.Any(a =>
                o.StartsWith(a.File + ":", StringComparison.Ordinal)
                && o.Contains(a.Snippet, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Fire-and-forget _recycleBin.MoveToBin(...) in an Api controller. Either write a " +
            "HistoryEntry with the returned RecyclePath, or add the (file, line) to " +
            "AllowedApiFireAndForget with a justification. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    private static List<string> FindFireAndForgetOffenders(string dir)
    {
        return Directory.GetFiles(dir, "*.cs")
            .SelectMany(path => File.ReadAllLines(path)
                .Select((line, i) => new { path = Path.GetFileName(path), lineNo = i + 1, line })
                .Where(x => StatementLevel.IsMatch(x.line))
                .Select(x => $"{x.path}:{x.lineNo}: {x.line.Trim()}"))
            .ToList();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.MediaDash.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

using System;
using System.IO;
using System.Reflection;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// MD-012 regression: orphan cleanup can recycle whole directories, so the history restore
/// endpoint AND RecycleBin.Restore must treat file and directory entries symmetrically. Before
/// this fix, both used File.Exists / File.Move — a recycled folder showed up in the bin UI but
/// the Restore button silently 409'd. Users emptying the bin thinking history had the folder
/// lost the whole tree.
///
/// RecycleBin.Restore depends on Plugin.Instance!.Configuration for the bin Root path so we
/// can't spin it up here without a plugin harness; instead these tests pin the source-level
/// contracts that close the finding, following the same pattern as
/// RecycleBinAutoAdoptBehaviorTests. Reverting either check re-opens the data-loss window.
/// </summary>
public sealed class DirectoryRestoreTests
{
    [Fact]
    public void RecycleBinRestoreCollisionCheckAcceptsBothFileAndDirectory()
    {
        var src = File.ReadAllText(RepoFile("Jellyfin.Plugin.MediaDash", "Fixers", "RecycleBin.cs"));
        var start = src.IndexOf("public void Restore(string recyclePath, string originalPath)", StringComparison.Ordinal);
        Assert.True(start > 0, "RecycleBin.Restore method not found — has it been renamed?");
        var body = ExtractMethodBody(src, start);

        // Both kinds must gate the collision check; File.Exists alone was the pre-fix behavior.
        Assert.Contains("File.Exists(originalPath)", body);
        Assert.Contains("Directory.Exists(originalPath)", body);
    }

    [Fact]
    public void RecycleBinRestoreDetectsDirectorySourceAndForwardsFlagToMove()
    {
        var src = File.ReadAllText(RepoFile("Jellyfin.Plugin.MediaDash", "Fixers", "RecycleBin.cs"));
        var start = src.IndexOf("public void Restore(string recyclePath, string originalPath)", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = ExtractMethodBody(src, start);

        // Directory detection + explicit flag pass-through are the mechanism that lets
        // MoveAcrossVolumes pick the right primitive (Directory.Move) for directory entries.
        Assert.Contains("Directory.Exists(recyclePath)", body);
        Assert.Contains("MoveAcrossVolumes(recyclePath, originalPath, sourceIsDir)", body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameFilesystemRestoreMovesBothFilesAndDirectories(bool directory)
    {
        var root = Path.Combine(Path.GetTempPath(), "md-directory-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "recycled");
            var target = Path.Combine(root, "restored");
            if (directory) Directory.CreateDirectory(Path.Combine(source, "empty"));
            File.WriteAllText(directory ? Path.Combine(source, "payload") : source, "original bytes");
            var move = typeof(RecycleBin).GetMethod("MoveAcrossVolumes", BindingFlags.Static | BindingFlags.NonPublic)!;
            move.Invoke(null, [source, target, directory]);
            Assert.Equal("original bytes", File.ReadAllText(directory ? Path.Combine(target, "payload") : target));
            if (directory) Assert.True(Directory.Exists(Path.Combine(target, "empty")));
            Assert.False(File.Exists(source) || Directory.Exists(source));
            Assert.Single(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RestoreFromHistoryEndpointAcceptsDirectoryRecycleEntries()
    {
        // 05-B: restore orchestration moved from MediaDashController.RestoreFromHistory to
        // RestoreService.RestoreFromHistory. The MD-012 directory-kind gates still need to appear
        // somewhere in the request path — grep the service body instead of the controller. The
        // controller endpoint now delegates unconditionally, so gating there would be redundant.
        var src = File.ReadAllText(RepoFile("Jellyfin.Plugin.MediaDash", "Fixers", "RestoreService.cs"));
        var start = src.IndexOf("internal RestoreOutcome RestoreFromHistory(long historyId, bool force)", StringComparison.Ordinal);
        Assert.True(start > 0, "RestoreService.RestoreFromHistory method not found — has it been renamed?");
        var body = ExtractMethodBody(src, start);

        // Existence gate must accept either kind. Both directions of the collision guard must
        // also see directories so a folder sitting at the original path isn't overwritten silently.
        Assert.Contains("File.Exists(entry.RecyclePath)", body);
        Assert.Contains("Directory.Exists(entry.RecyclePath)", body);
        Assert.Contains("PathExists(entry.Path)", body);
    }

    private static string RepoFile(params string[] segments)
    {
        var here = Path.GetDirectoryName(typeof(DirectoryRestoreTests).Assembly.Location)!;
        // bin/Debug/net9.0 → repo root
        var repo = Path.GetFullPath(Path.Combine(here, "..", "..", "..", ".."));
        var full = repo;
        foreach (var s in segments)
        {
            full = Path.Combine(full, s);
        }

        return full;
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
                    return src.Substring(startIndex, i - startIndex + 1);
                }
            }
        }

        throw new InvalidOperationException("Unterminated method body");
    }
}

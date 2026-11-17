using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;

[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "type", Target = "~T:Jellyfin.Plugin.MediaDash.Fixers.MediaMoveSafety", Justification = "All move callers validate both paths with LibraryGuard before volume detection or movement.")]

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Shared volume detection and verified cross-volume movement.</summary>
internal static class MediaMoveSafety
{
    internal const string UnsafeSourceDeletionMessage = "Cross-filesystem moves are unavailable because safe source deletion cannot be guaranteed while another process may write. Choose a destination on the same filesystem.";

    internal static bool RequiresCopyDelete(string source, string target) => IsCrossVolume(source, target)
        || (OperatingSystem.IsWindows() && !string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase));

    internal static void RequireSameFilesystemMove(string source, string target)
    {
        if (RequiresCopyDelete(source, target))
        {
            throw new IOException(UnsafeSourceDeletionMessage);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileWithoutCopy(string source, string destination, uint flags);

    [DllImport("libc", EntryPoint = "renameat2", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int RenameNoReplace(int oldDirectory, string source, int newDirectory, string destination, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryExclusive(string path, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "mkdir", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int MakeDirectoryExclusive(string path, uint mode);

    internal static void CreateDirectoryNoMerge(string path)
    {
        if ((OperatingSystem.IsWindows() && CreateDirectoryExclusive(path, IntPtr.Zero))
            || (OperatingSystem.IsLinux() && MakeDirectoryExclusive(path, 511) == 0))
        {
            return;
        }

        throw new IOException("Could not exclusively create the approved destination directory.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    internal static void RenameNoCopy(string source, string target, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
                : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
            if (MoveFileWithoutCopy(Extended(source), Extended(target), 0))
            {
                return;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                const int currentDirectory = -100;
                const uint noReplace = 1;
                if (RenameNoReplace(currentDirectory, source, currentDirectory, target, noReplace) == 0)
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException error)
            {
                throw new IOException("This filesystem cannot guarantee a rename without overwrite or implicit copying. The source was preserved.", error);
            }
        }
        else
        {
            throw new IOException("A strict rename is unavailable on this platform. The source was preserved.");
        }

        var errorCode = Marshal.GetLastWin32Error();
        // Separate Linux bind mounts can share a device number and still reject rename with EXDEV.
        var crossFilesystem = errorCode == (OperatingSystem.IsWindows() ? 17 : 18);
        throw new IOException(
            crossFilesystem ? UnsafeSourceDeletionMessage : "The no-overwrite rename could not complete; no copy/delete fallback was used.",
            new Win32Exception(errorCode));
    }

    internal static void Revalidate(Issue issue, LibraryGuard guard, ProtectionService protection, IssueStore issues, string source, string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Plugin.Instance!.Configuration;
        var locations = guard.GetEnabledLibraryLocations(current.EnabledLibraries);
        var status = issue.Id > 0 ? issues.GetIssueStatus(issue.Id) : issue.Status;
        if (current.DryRun || status is null || (issue.Id > 0 && status != IssueStatus.Queued)
            || !guard.IsInsideLibrary(source) || !guard.IsInsideLibrary(target)
            || !LibraryGuard.IsInEnabledLocations(source, locations) || !LibraryGuard.IsInEnabledLocations(target, locations)
            || protection.IsFixerBlocked(source, issue.Type) || protection.IsFixerBlocked(target, issue.Type)
            || !FixPolicyEvaluator.Evaluate(current, issue.Type, status.Value, FixIntent.Approved, true, false, false).Allowed)
        {
            throw new IOException("Move stopped because approval, library scope, protection or fix policy changed. Preserve any reported recovery paths.");
        }
    }

    internal static void CrossVolumeMove(string source, string target, bool sourceIsDir, Action<string, string>? checkpoint = null, CancellationToken cancellationToken = default)
        => Api.FileBrowserController.CrossDeviceMove(source, target, sourceIsDir, checkpoint, cancellationToken);

    internal static bool IsCrossVolume(string source, string target)
    {
        var ancestor = target;
        while (!File.Exists(ancestor) && !Directory.Exists(ancestor))
        {
            ancestor = Path.GetDirectoryName(ancestor);
            if (string.IsNullOrEmpty(ancestor))
            {
                return true;
            }
        }

        // Unknown volumes are treated as cross-volume and refused by move callers.
        return !PhysicalFileIdentity.TryRead(source, out var sourceId)
            || !PhysicalFileIdentity.TryRead(ancestor, out var targetId)
            || sourceId.Volume != targetId.Volume;
    }
}

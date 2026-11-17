using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Reads the physical file identity (volume + inode/FileID + link count + reparse flag) for a
/// path. Used by the duplicate scanner / fixer so hardlinks and symlinks referring to the same
/// physical byte stream are never flagged as independent copies worth deleting.
/// </summary>
/// <remarks>
/// A read failure is explicitly inconclusive, never evidence of an independent file. Callers
/// MUST treat <c>TryRead</c> returning false as "cannot safely dispose this candidate as a
/// duplicate" per the Task-2 contract. Windows uses <c>GetFileInformationByHandle</c>; Linux
/// shells out to <c>/usr/bin/stat</c> which exposes device/inode/nlink in a libc-version-stable
/// format. macOS/BSD/other platforms fall through to a false return.
/// </remarks>
internal static class PhysicalFileIdentity
{
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    /// <summary>
    /// Try to read the physical identity for <paramref name="path"/>. Returns false when the OS
    /// identity could not be determined — the caller must treat that as "unknown" and REFUSE
    /// any alias-based disposal decision.
    /// </summary>
    /// <param name="path">Absolute path (file or directory).</param>
    /// <param name="value">The identity on success; default on failure.</param>
    /// <returns>True when the identity could be read.</returns>
    internal static bool TryRead(string path, out Value value)
    {
        if (string.IsNullOrEmpty(path))
        {
            value = default;
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return TryReadWindows(path, out value);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            return TryReadUnix(path, out value);
        }

        value = default;
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    private static bool TryReadWindows(string path, out Value value)
    {
        value = default;
        try
        {
            // FILE_FLAG_OPEN_REPARSE_POINT so a symlink reports its OWN identity, not the target's.
            // BACKUP_SEMANTICS required for directories. Zero desired access is enough for metadata
            // reads; full share so we never block another process holding a handle.
            using var handle = CreateFile(
                path,
                dwDesiredAccess: 0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            if (!GetFileInformationByHandle(handle, out var info))
            {
                return false;
            }

            var fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            value = new Value(
                volume: info.VolumeSerialNumber,
                fileId: fileId,
                linkCount: info.NumberOfLinks,
                isReparsePoint: (info.FileAttributes & FileAttributeReparsePoint) != 0);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadUnix(string path, out Value value)
    {
        value = default;
        try
        {
            // Shelling out to coreutils stat sidesteps the libc-version / arch-dependent
            // sizeof(struct stat) problem. Format tokens are stable: %d device, %i inode,
            // %h hardlink count, %f hex mode (0xa000 = symlink). The bare form does NOT follow
            // symlinks (unlike `stat -L`), which is what we need so a symlink reports its own
            // identity and gets flagged as a reparse point.
            var start = new ProcessStartInfo("/usr/bin/stat")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("%d %i %h %f");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(path);
            using var proc = Process.Start(start);
            if (proc is null)
            {
                return false;
            }

            if (!proc.WaitForExit(2000))
            {
                try
                {
                    proc.Kill();
                }
                catch
                {
                }

                return false;
            }

            if (proc.ExitCode != 0)
            {
                return false;
            }

            var parts = proc.StandardOutput.ReadToEnd().Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4
                || !ulong.TryParse(parts[0], out var dev)
                || !ulong.TryParse(parts[1], out var ino)
                || !uint.TryParse(parts[2], out var nlink)
                || !uint.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mode))
            {
                return false;
            }

            // POSIX S_IFLNK = 0xA000; mask with 0xF000.
            var isSymlink = (mode & 0xF000) == 0xA000;
            value = new Value(volume: dev, fileId: ino, linkCount: nlink, isReparsePoint: isSymlink);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Native layout: BY_HANDLE_FILE_INFORMATION packs FILETIMEs as two DWORDs (4-byte aligned),
    // so a C# `long` with its 8-byte alignment misreports NumberOfLinks and FileIndex*. Model
    // each FILETIME as two uints and let LayoutKind.Sequential pack them exactly like the Win32
    // header.
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary>
    /// Opaque physical-file identity. Equality compares volume and FileID only — a hardlink and
    /// its target compare equal.
    /// </summary>
    internal readonly struct Value : IEquatable<Value>
    {
        public Value(ulong volume, ulong fileId, uint linkCount, bool isReparsePoint)
        {
            Volume = volume;
            FileId = fileId;
            LinkCount = linkCount;
            IsReparsePoint = isReparsePoint;
        }

        /// <summary>Gets the volume (device) serial.</summary>
        public ulong Volume { get; }

        /// <summary>Gets the file index (FileID on Windows, inode on Unix).</summary>
        public ulong FileId { get; }

        /// <summary>Gets the hard-link count; 1 means this is the only directory entry pointing at the physical file.</summary>
        public uint LinkCount { get; }

        /// <summary>Gets a value indicating whether the entry is a symlink/junction/reparse point.</summary>
        public bool IsReparsePoint { get; }

        public static bool operator ==(Value left, Value right) => left.Equals(right);

        public static bool operator !=(Value left, Value right) => !left.Equals(right);

        public bool Equals(Value other) => Volume == other.Volume && FileId == other.FileId;

        public override bool Equals(object? obj) => obj is Value v && Equals(v);

        public override int GetHashCode() => HashCode.Combine(Volume, FileId);
    }
}

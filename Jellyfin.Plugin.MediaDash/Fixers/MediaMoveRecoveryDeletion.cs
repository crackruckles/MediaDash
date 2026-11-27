using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Windows recovery disposal retaining unnamed and enumerated stream sharing exclusions.</summary>
/// <remarks>Creation of an unrecorded named stream after enumeration remains an unresolved
/// cleanup race. Read-only attributes are not an exclusion boundary: metadata handles can clear them.</remarks>
internal static class MediaMoveRecoveryDeletion
{
    internal static void DeleteWindows(string path, Action verify)
    {
        var ancestors = new List<SafeFileHandle>();
        var streams = new List<SafeFileHandle>();
        try
        {
            // Pin the namespace too: an ancestor rename must not redirect the path-based verifier.
            for (var parent = Path.GetDirectoryName(Path.GetFullPath(path)); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                var ancestor = Open(parent, 0, 3);
                ancestors.Add(ancestor);
            }

            // DELETE + GENERIC_READ + WRITE_ATTRIBUTES. Unnamed-stream writers and renames/deletes cannot
            // coexist with this handle. No delete disposition is requested until verification passes.
            using var handle = Open(path, 0x80010100, 1);
            MediaMoveSnapshot.ValidateAncestors(path);
            // Windows sharing is per stream. Pin every currently enumerated named stream
            // as well as the unnamed payload; a writer already holding one makes this fail.
            foreach (var name in MediaMoveMetadata.StreamNames(path))
            {
                streams.Add(Open(path + name, 0x80000000, 5));
            }

            verify();
            if (!GetBasic(handle, 0, out var basic, 40))
            {
                throw Failure();
            }

            var originalAttributes = basic.Attributes;
            var changed = (originalAttributes & (uint)FileAttributes.ReadOnly) != 0;
            if (changed)
            {
                basic = new BasicInfo { Attributes = originalAttributes & ~(uint)FileAttributes.ReadOnly };
                if (basic.Attributes == 0)
                {
                    basic.Attributes = (uint)FileAttributes.Normal;
                }

                if (!SetBasic(handle, 0, ref basic, 40))
                {
                    throw Failure();
                }
            }

            var disposition = new DispositionInfo { Delete = 1 };
            if (!SetDisposition(handle, 4, ref disposition, 1))
            {
                var error = Failure();
                if (changed)
                {
                    basic = new BasicInfo { Attributes = originalAttributes };
                    if (!SetBasic(handle, 0, ref basic, 40))
                    {
                        throw new IOException("MediaDash could not remove a temporary recovery copy, or set it back to read-only. If the move worked, delete the copy by hand; the move record lists where it is.", error);
                    }
                }

                throw error;
            }
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }

            foreach (var ancestor in ancestors)
            {
                ancestor.Dispose();
            }
        }
    }

    private static SafeFileHandle Open(string path, uint access, uint share)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Failure();
        handle.Dispose();
        throw error;
    }

    private static IOException Failure() => new("Move recovery could not be exclusively verified or deleted; preserve it for review.", new Win32Exception(Marshal.GetLastWin32Error()));

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasic(SafeFileHandle handle, int kind, out BasicInfo value, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBasic(SafeFileHandle handle, int kind, ref BasicInfo value, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle handle, int kind, ref DispositionInfo value, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public long Creation;
        public long Access;
        public long Write;
        public long Change;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispositionInfo
    {
        public byte Delete;
    }
}

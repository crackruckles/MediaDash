using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "type", Target = "~T:Jellyfin.Plugin.MediaDash.Fixers.MediaMoveMetadata", Justification = "Move and Files callers authorize both paths with LibraryGuard before invoking this internal helper. It rechecks linked ancestors and only applies metadata to uniquely owned copied payloads.")]

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Metadata evidence and preservation for owned move payloads.</summary>
internal static class MediaMoveMetadata
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static AccessControlSections SecuritySections => AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;

    internal static string Capture(string path, bool directory, bool allowDeleteHandle = false)
    {
        try
        {
            MediaMoveSnapshot.ValidateAncestors(path);
            return CaptureCore(path, directory, allowDeleteHandle);
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            throw new IOException("Move refused: this host cannot verify the required filesystem metadata.", error);
        }
    }

    private static string CaptureCore(string path, bool directory, bool allowDeleteHandle)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = directory ? new DirectoryInfo(path).GetAccessControl(SecuritySections) : (FileSystemSecurity)new FileInfo(path).GetAccessControl(SecuritySections);
            return JsonSerializer.Serialize(new { created = File.GetCreationTimeUtc(path).Ticks, attributes = File.GetAttributes(path), acl = security.GetSecurityDescriptorSddlForm(SecuritySections), streams = Streams(path, allowDeleteHandle) });
        }

        if (OperatingSystem.IsLinux())
        {
            var identity = UnixOwnership(path);
            return JsonSerializer.Serialize(new { identity.User, identity.Group, mode = File.GetUnixFileMode(path), attributes = ExtendedAttributes(path) });
        }

        throw new IOException("Move metadata cannot be verified on this platform.");
    }

    internal static void Preserve(string source, string destination, bool directory)
    {
        MediaMoveSnapshot.ValidateAncestors(source);
        MediaMoveSnapshot.ValidateAncestors(destination);
        if (OperatingSystem.IsWindows())
        {
            if (directory)
            {
                var sourceSecurity = new DirectoryInfo(source).GetAccessControl(SecuritySections);
                var targetSecurity = new DirectoryInfo(destination).GetAccessControl(SecuritySections);
                var sections = ChangedSecuritySections(sourceSecurity, targetSecurity);
                if (sections != AccessControlSections.None)
                {
                    var security = new DirectorySecurity();
                    security.SetSecurityDescriptorBinaryForm(sourceSecurity.GetSecurityDescriptorBinaryForm(), sections);
                    new DirectoryInfo(destination).SetAccessControl(security);
                }
            }
            else
            {
                var sourceSecurity = new FileInfo(source).GetAccessControl(SecuritySections);
                var targetSecurity = new FileInfo(destination).GetAccessControl(SecuritySections);
                var sections = ChangedSecuritySections(sourceSecurity, targetSecurity);
                if (sections != AccessControlSections.None)
                {
                    var security = new FileSecurity();
                    security.SetSecurityDescriptorBinaryForm(sourceSecurity.GetSecurityDescriptorBinaryForm(), sections);
                    new FileInfo(destination).SetAccessControl(security);
                }
            }

            if (directory)
            {
                Directory.SetCreationTimeUtc(destination, Directory.GetCreationTimeUtc(source));
                Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
            }
            else
            {
                File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(source));
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            }

            File.SetAttributes(destination, File.GetAttributes(source));
        }
        else if (OperatingSystem.IsLinux())
        {
            var identity = UnixOwnership(source);
            if (ChangeOwnership(destination, identity.User, identity.Group) != 0)
            {
                throw NativeFailure("Move refused: source ownership could not be preserved.");
            }

            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
            foreach (var attribute in ExtendedAttributes(source))
            {
                var value = Convert.FromHexString(attribute.Value);
                if (SetAttribute(destination, attribute.Key, value, (nuint)value.Length, 0) != 0)
                {
                    throw NativeFailure("Move refused: source extended attributes could not be preserved.");
                }
            }

            if (directory)
            {
                Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
            }
            else
            {
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            }
        }
        else
        {
            throw new IOException("Move metadata cannot be preserved on this platform.");
        }

        if (Capture(source, directory) != Capture(destination, directory))
        {
            throw new IOException("Move refused: copied permissions, attributes or alternate streams do not match the source.");
        }
    }

    internal static IEnumerable<string> StreamNames(string path)
    {
        var handle = FindFirstStream(path, 0, out var stream, 0);
        if (handle == new IntPtr(-1))
        {
            if (Marshal.GetLastWin32Error() == 38)
            {
                yield break;
            }

            throw NativeFailure("Move refused: alternate streams could not be enumerated.");
        }

        try
        {
            do
            {
                if (stream.Name != "::$DATA")
                {
                    yield return stream.Name;
                }
            }
            while (FindNextStream(handle, out stream));
            if (Marshal.GetLastWin32Error() != 38)
            {
                throw NativeFailure("Move refused: alternate stream enumeration was incomplete.");
            }
        }
        finally
        {
            FindClose(handle);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static AccessControlSections ChangedSecuritySections(FileSystemSecurity source, FileSystemSecurity target)
    {
        var sections = AccessControlSections.None;
        if (source.GetSecurityDescriptorSddlForm(AccessControlSections.Access) != target.GetSecurityDescriptorSddlForm(AccessControlSections.Access))
        {
            sections |= AccessControlSections.Access;
        }

        if (source.GetOwner(typeof(SecurityIdentifier)) != target.GetOwner(typeof(SecurityIdentifier)))
        {
            sections |= AccessControlSections.Owner;
        }

        if (source.GetGroup(typeof(SecurityIdentifier)) != target.GetGroup(typeof(SecurityIdentifier)))
        {
            sections |= AccessControlSections.Group;
        }

        return sections;
    }

    private static SortedDictionary<string, string> Streams(string path, bool allowDeleteHandle)
    {
        var streams = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in StreamNames(path))
        {
            using var input = new FileStream(path + name, FileMode.Open, FileAccess.Read, FileShare.Read | (allowDeleteHandle ? FileShare.Delete : 0));
            streams.Add(name, Convert.ToHexString(SHA256.HashData(input)));
        }

        return streams;
    }

    private static (uint User, uint Group) UnixOwnership(string path)
    {
        var data = new byte[256];
        if (StatExtended(-100, path, 0x100, 0x7ff, data) != 0 || (BitConverter.ToUInt32(data, 0) & 0x18) != 0x18)
        {
            throw NativeFailure("Move refused: source ownership could not be verified.");
        }

        return (BitConverter.ToUInt32(data, 20), BitConverter.ToUInt32(data, 24));
    }

    private static SortedDictionary<string, string> ExtendedAttributes(string path)
    {
        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var length = ListAttributes(path, null, 0);
        if (length < 0 || length > 16 * 1024 * 1024)
        {
            throw NativeFailure("Move refused: extended attributes could not be enumerated.");
        }

        if (length == 0)
        {
            return attributes;
        }

        var names = new byte[(int)length];
        if (ListAttributes(path, names, (nuint)names.Length) != length)
        {
            throw new IOException("Move refused: source extended attributes changed while reading.");
        }

        foreach (var name in Encoding.UTF8.GetString(names).Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal))
        {
            var size = ReadAttribute(path, name, null, 0);
            if (size < 0 || size > 16 * 1024 * 1024)
            {
                throw NativeFailure("Move refused: an extended attribute could not be read.");
            }

            var value = new byte[(int)size];
            if (ReadAttribute(path, name, value, (nuint)value.Length) != size)
            {
                throw new IOException("Move refused: an extended attribute changed while reading.");
            }

            attributes.Add(name, Convert.ToHexString(value));
        }

        return attributes;
    }

    private static IOException NativeFailure(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr FindFirstStream(string path, int informationLevel, out StreamData data, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(IntPtr handle, out StreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int StatExtended(int directory, string path, int flags, uint mask, [Out] byte[] data);

    [DllImport("libc", EntryPoint = "chown", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int ChangeOwnership(string path, uint user, uint group);

    [DllImport("libc", EntryPoint = "listxattr", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint ListAttributes(string path, [Out] byte[]? list, nuint size);

    [DllImport("libc", EntryPoint = "getxattr", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint ReadAttribute(string path, string name, [Out] byte[]? value, nuint size);

    [DllImport("libc", EntryPoint = "setxattr", CharSet = CharSet.Ansi, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SetAttribute(string path, string name, byte[] value, nuint size, int flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        internal long Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        internal string Name;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.MediaDash.Fixers;

internal sealed class MountedRecycleRoots(string registry)
{
    private const string Prefix = ".mediadash-recycle-";
    private readonly object _gate = new();

    internal static bool Contains(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        foreach (var part in path.Split('/'))
        {
            if (IsManagedName(part))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsManagedName(string name) =>
        name.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(name.AsSpan(Prefix.Length), "N", out _);

    internal string? GetOrCreate(string source, Func<IEnumerable<string>> getLibraryLocations)
    {
        if (!OperatingSystem.IsLinux() || Contains(source) || LibraryGuard.HasReparsePointAncestor(source))
        {
            return null;
        }

        var mount = RecycleBin.FindDriveForPath(source)?.Name;
        if (mount is null || mount == "/" || string.Equals(mount, source, StringComparison.Ordinal)
            || RecycleBin.IsSystemReservedPath(mount))
        {
            return null;
        }

        try
        {
            return GetOrCreateAt(mount);
        }
        catch (UnauthorizedAccessException)
        {
            var libraryRoot = getLibraryLocations()
                .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
                .Where(path => path != mount && path != source && LibraryGuard.IsUnder(source, path)
                    && string.Equals(RecycleBin.FindDriveForPath(path)?.Name, mount, StringComparison.Ordinal))
                .MinBy(path => path.Length);
            if (libraryRoot is null)
            {
                throw;
            }

            return GetOrCreateAt(libraryRoot);
        }
    }

    [SupportedOSPlatform("linux")]
    private string? GetOrCreateAt(string parent)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parent)));
        var record = Path.Combine(registry, key + ".root");
        lock (_gate)
        {
            Directory.CreateDirectory(registry);
            if (File.Exists(record))
            {
                var root = ReadRoot(record);
                return root is not null && string.Equals(Path.GetDirectoryName(root), parent, StringComparison.Ordinal) ? root : null;
            }

            var newRoot = Path.Combine(parent, Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(newRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using (File.Create(Path.Combine(newRoot, ".ignore")))
            {
            }

            // Publish the root before any original can move into it.
            var pending = record + ".pending";
            File.WriteAllText(pending, newRoot);
            File.Move(pending, record);
            return newRoot;
        }
    }

    internal IEnumerable<string> List()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(registry))
        {
            yield break;
        }

        foreach (var record in Directory.GetFiles(registry, "*.root"))
        {
            if (ReadRoot(record) is { } root)
            {
                yield return root;
            }
        }
    }

    private static string? ReadRoot(string record)
    {
        try
        {
            var root = File.ReadAllText(record);
            return Path.IsPathFullyQualified(root) && IsManagedName(Path.GetFileName(root))
                && Directory.Exists(root) && !LibraryGuard.HasReparsePointAncestor(root)
                ? root : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

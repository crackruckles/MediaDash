using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Probing;

[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "type", Target = "~T:Jellyfin.Plugin.MediaDash.Fixers.MediaMoveSnapshot", Justification = "Move callers validate source and destination via LibraryGuard; snapshot reads those paths and refuses linked ancestors and payloads.")]

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>Uncached payload and identity evidence for a move, including empty directories.</summary>
internal sealed class MediaMoveSnapshot
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal static MediaMoveSnapshot Capture(string path, CancellationToken cancellationToken = default)
        => Capture(path, allowDeleteHandle: false, null, cancellationToken);

    internal static MediaMoveSnapshot Capture(string path, Action<string, long, long> progress, CancellationToken cancellationToken)
        => Capture(path, allowDeleteHandle: false, progress, cancellationToken);

    private static MediaMoveSnapshot Capture(string path, bool allowDeleteHandle, Action<string, long, long>? progress, CancellationToken cancellationToken)
    {
        ValidateAncestors(path);
        var snapshot = new MediaMoveSnapshot();
        snapshot.Add(path, string.Empty, allowDeleteHandle, progress, cancellationToken);
        return snapshot;
    }

    internal static void ValidateAncestors(string path)
    {
        for (var ancestor = Path.GetFullPath(path); !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
        {
            if ((File.Exists(ancestor) || Directory.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Move refused: the payload has a linked ancestor.");
            }
        }
    }

    internal void Verify(string path, bool sameIdentity, CancellationToken cancellationToken = default)
    {
        var current = Capture(path, cancellationToken);
        if (_entries.Count != current._entries.Count)
        {
            throw new IOException("Move refused: the payload inventory changed at '" + path + "'.");
        }

        foreach (var (relative, expected) in _entries)
        {
            if (!current._entries.TryGetValue(relative, out var actual)
                || actual.IsDirectory != expected.IsDirectory
                || actual.Hash != expected.Hash
                || actual.Length != expected.Length
                || actual.Metadata != expected.Metadata
                || (!actual.IsDirectory && actual.Modified != expected.Modified)
                || (sameIdentity && actual.Identity != expected.Identity))
            {
                throw new IOException("Move refused: payload or identity changed at '" + Path.Combine(path, relative) + "'.");
            }
        }
    }

    internal bool Matches(string path)
    {
        try
        {
            Verify(path, sameIdentity: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal MediaMovePlan.Payload[] Export() => _entries.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new MediaMovePlan.Payload(
            pair.Key,
            pair.Value.IsDirectory,
            pair.Value.Length,
            pair.Value.Modified.Ticks,
            pair.Value.Identity.Volume,
            pair.Value.Identity.FileId,
            pair.Value.Hash,
            pair.Value.Metadata)).ToArray();

    internal static void VerifyInventory(string path, MediaMovePlan.Payload[] expected, bool sameIdentity, CancellationToken cancellationToken)
    {
        var current = Capture(path, cancellationToken).Export();
        if (current.Length != expected.Length || current.Where((entry, index) =>
                entry.RelativePath != expected[index].RelativePath || entry.IsDirectory != expected[index].IsDirectory
                || entry.Length != expected[index].Length || entry.Sha256 != expected[index].Sha256
                || entry.Metadata != expected[index].Metadata
                || (!entry.IsDirectory && entry.LastWriteUtcTicks != expected[index].LastWriteUtcTicks)
                || (sameIdentity && (entry.Volume != expected[index].Volume || entry.FileId != expected[index].FileId))).Any())
        {
            throw new IOException("Move refused: the approved payload changed at '" + path + "'. Re-scan and approve the new plan.");
        }
    }

    internal void DeleteCaptured(string path, CancellationToken cancellationToken, Action? beforeDelete = null)
    {
        Verify(path, sameIdentity: true, cancellationToken);
        foreach (var (relative, expected) in _entries.Where(pair => !pair.Value.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeDelete?.Invoke();
            var file = Path.Combine(path, relative);
            void VerifyFile()
            {
                if (Capture(file, allowDeleteHandle: OperatingSystem.IsWindows(), null, cancellationToken)._entries[string.Empty] != expected)
                {
                    throw new IOException("Move recovery changed; preserve '" + file + "' for review.");
                }
            }

            if (OperatingSystem.IsWindows())
            {
                MediaMoveRecoveryDeletion.DeleteWindows(file, VerifyFile);
            }
            else
            {
                VerifyFile();
                File.Delete(file);
            }
        }

        // Never recursively sweep a recovery tree: a writer may have added unrecorded children.
        foreach (var (relative, expected) in _entries.Where(pair => pair.Value.IsDirectory).OrderByDescending(pair => pair.Key.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeDelete?.Invoke();
            var directory = Path.Combine(path, relative);
            void VerifyDirectory()
            {
                if (!PhysicalFileIdentity.TryRead(directory, out var identity) || identity != expected.Identity || identity.IsReparsePoint)
                {
                    throw new IOException("Move recovery directory changed; preserve '" + directory + "' for review.");
                }
            }

            if (OperatingSystem.IsWindows())
            {
                MediaMoveRecoveryDeletion.DeleteWindows(directory, VerifyDirectory);
            }
            else
            {
                VerifyDirectory();
                Directory.Delete(directory, recursive: false);
            }
        }
    }

    private void Add(string path, string relative, bool allowDeleteHandle, Action<string, long, long>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PhysicalFileIdentity.TryRead(path, out var identity) || identity.IsReparsePoint)
        {
            throw new IOException("Move refused: the physical identity could not be verified at '" + path + "'.");
        }

        var isDirectory = Directory.Exists(path);
        if (isDirectory)
        {
            _entries.Add(relative, new Entry(true, 0, default, identity, string.Empty, MediaMoveMetadata.Capture(path, true, allowDeleteHandle)));
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                Add(child, Path.Combine(relative, Path.GetFileName(child)), allowDeleteHandle, progress, cancellationToken);
            }
        }
        else
        {
            if (identity.LinkCount != 1)
            {
                throw new IOException("Move refused: a multiply-linked payload cannot be copied safely at '" + path + "'.");
            }

            var info = new FileInfo(path);
            var length = info.Length;
            var modified = info.LastWriteTimeUtc;
            progress?.Invoke(path, 0, length);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | (allowDeleteHandle ? FileShare.Delete : 0), 128 * 1024, FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long reportedBytes = 0;
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, count);
                if (progress is not null && (stream.Position - reportedBytes >= 4 * 1024 * 1024 || stream.Position == length))
                {
                    progress(path, stream.Position, length);
                    reportedBytes = stream.Position;
                }
            }

            info.Refresh();
            if (info.Length != length || info.LastWriteTimeUtc != modified)
            {
                throw new IOException("Move refused: the source changed while reading '" + path + "'.");
            }

            _entries.Add(relative, new Entry(false, length, modified, identity, Convert.ToHexString(hash.GetHashAndReset()), MediaMoveMetadata.Capture(path, false, allowDeleteHandle)));
        }

        if (!PhysicalFileIdentity.TryRead(path, out var after) || after != identity || after.IsReparsePoint)
        {
            throw new IOException("Move refused: the physical identity changed at '" + path + "'.");
        }
    }

    private sealed record Entry(bool IsDirectory, long Length, DateTime Modified, PhysicalFileIdentity.Value Identity, string Hash, string Metadata);
}

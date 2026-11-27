using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Probing;

[assembly: SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Scope = "type", Target = "~T:Jellyfin.Plugin.MediaDash.Fixers.MediaMoveSnapshot", Justification = "Move callers validate source and destination via LibraryGuard; snapshot reads those paths and refuses linked ancestors and payloads.")]

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Uncached payload and identity evidence for a move, including empty directories. A snapshot
/// either hashes every file's content or records identity only (volume + file ID, length,
/// modified time and filesystem metadata, with an empty SHA-256). Moves are same-filesystem
/// renames, which keep the file ID, so identity evidence is enough to prove the approved payload
/// is the one being moved; reading every byte of a 3 TB library at scan time was not.
/// </summary>
internal sealed class MediaMoveSnapshot
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly bool _hashContent;

    private MediaMoveSnapshot(bool hashContent) => _hashContent = hashContent;

    internal static MediaMoveSnapshot Capture(string path, CancellationToken cancellationToken = default)
        => Capture(path, allowDeleteHandle: false, hashContent: true, null, cancellationToken);

    internal static MediaMoveSnapshot Capture(string path, Action<string, long, long> progress, CancellationToken cancellationToken)
        => Capture(path, allowDeleteHandle: false, hashContent: true, progress, cancellationToken);

    // Identity evidence only: no file content is read.
    internal static MediaMoveSnapshot CaptureIdentity(string path, CancellationToken cancellationToken = default)
        => Capture(path, allowDeleteHandle: false, hashContent: false, null, cancellationToken);

    internal static MediaMoveSnapshot CaptureIdentity(string path, Action<string, long, long> progress, CancellationToken cancellationToken)
        => Capture(path, allowDeleteHandle: false, hashContent: false, progress, cancellationToken);

    // Only the root entry (identity and metadata, no children, no content). For plans that are
    // already blocked and can never be approved or executed.
    internal static MediaMoveSnapshot CaptureRootIdentity(string path, CancellationToken cancellationToken = default)
    {
        ValidateAncestors(path);
        var snapshot = new MediaMoveSnapshot(hashContent: false);
        snapshot.Add(path, string.Empty, allowDeleteHandle: false, progress: null, cancellationToken, recurse: false);
        return snapshot;
    }

    // True when the inventory carries content hashes, so verifying it must read content too.
    internal static bool HasContentHashes(IEnumerable<MediaMovePlan.Payload> inventory)
        => inventory.Any(payload => !payload.IsDirectory && payload.Sha256.Length > 0);

    private static MediaMoveSnapshot Capture(string path, bool allowDeleteHandle, bool hashContent, Action<string, long, long>? progress, CancellationToken cancellationToken)
    {
        ValidateAncestors(path);
        var snapshot = new MediaMoveSnapshot(hashContent);
        snapshot.Add(path, string.Empty, allowDeleteHandle, progress, cancellationToken);
        return snapshot;
    }

    internal static void ValidateAncestors(string path)
    {
        for (var ancestor = Path.GetFullPath(path); !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
        {
            if ((File.Exists(ancestor) || Directory.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Not moved: one of the folders above this item is a shortcut (a symlink or junction), which MediaDash does not move through. Move it by hand in the Files tab.");
            }
        }
    }

    internal void Verify(string path, bool sameIdentity, CancellationToken cancellationToken = default)
    {
        var current = Capture(path, allowDeleteHandle: false, _hashContent, null, cancellationToken);
        if (_entries.Count != current._entries.Count)
        {
            throw new IOException("Stopped: files were added to or removed from '" + path + "' after MediaDash checked it. Nothing was changed. Run a scan and try again.");
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
                throw new IOException("Stopped: '" + Path.Combine(path, relative) + "' changed after MediaDash checked it (another program may be writing to it). Nothing was changed. Run a scan and try again.");
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
        // Unhashed (identity-only) inventories are verified by identity; hashed ones by content too.
        var current = Capture(path, allowDeleteHandle: false, HasContentHashes(expected), null, cancellationToken).Export();
        if (current.Length != expected.Length || current.Where((entry, index) =>
                entry.RelativePath != expected[index].RelativePath || entry.IsDirectory != expected[index].IsDirectory
                || entry.Length != expected[index].Length || entry.Sha256 != expected[index].Sha256
                || entry.Metadata != expected[index].Metadata
                || (!entry.IsDirectory && entry.LastWriteUtcTicks != expected[index].LastWriteUtcTicks)
                || (sameIdentity && (entry.Volume != expected[index].Volume || entry.FileId != expected[index].FileId))).Any())
        {
            throw new IOException(ChangedSinceApproval(DescribeChange(path, expected, current, sameIdentity)));
        }
    }

    // User-facing refusal: what changed, that nothing moved, the usual causes and what to do.
    internal static string ChangedSinceApproval(string change)
        => "Not moved: " + change + " after this move was approved, so MediaDash no longer knows it is moving the same file you approved. "
            + "Nothing was moved or changed. This usually means another tool rewrote it (a re-encode script, a download replacing it with a better copy, "
            + "another MediaDash fix, or Jellyfin saving new metadata). Run a scan to get an up-to-date plan, then approve it again.";

    // Names the first difference between the approved inventory and what is on disk now.
    internal static string DescribeChange(string root, MediaMovePlan.Payload[] expected, MediaMovePlan.Payload[] current, bool sameIdentity)
    {
        var now = current.GroupBy(entry => entry.RelativePath, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        string Name(string relative) => "'" + (string.IsNullOrEmpty(relative) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) : relative) + "'";
        foreach (var approved in expected)
        {
            if (!now.TryGetValue(approved.RelativePath, out var actual))
            {
                return Name(approved.RelativePath) + " was removed or renamed";
            }

            if (actual.IsDirectory != approved.IsDirectory)
            {
                return Name(approved.RelativePath) + " changed between a file and a folder";
            }

            if (!actual.IsDirectory && actual.Length != approved.Length)
            {
                return Name(approved.RelativePath) + " was rewritten (its size changed from " + Size(approved.Length) + " to " + Size(actual.Length) + ")";
            }

            if (actual.Sha256 != approved.Sha256)
            {
                return Name(approved.RelativePath) + " has different contents";
            }

            if (sameIdentity && (actual.Volume != approved.Volume || actual.FileId != approved.FileId))
            {
                return Name(approved.RelativePath) + " was replaced by a new copy of the file";
            }

            if (!actual.IsDirectory && actual.LastWriteUtcTicks != approved.LastWriteUtcTicks)
            {
                return Name(approved.RelativePath) + " was modified (last changed " + When(actual.LastWriteUtcTicks) + ")";
            }

            if (actual.Metadata != approved.Metadata)
            {
                return "the permissions or ownership of " + Name(approved.RelativePath) + " changed";
            }
        }

        var added = current.FirstOrDefault(entry => !expected.Any(approved => string.Equals(approved.RelativePath, entry.RelativePath, StringComparison.Ordinal)));
        return added is not null ? "a new file " + Name(added.RelativePath) + " appeared in " + Name(string.Empty) : Name(string.Empty) + " changed";
    }

    private static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static string When(long utcTicks) => new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

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
                if (Capture(file, allowDeleteHandle: OperatingSystem.IsWindows(), _hashContent, null, cancellationToken)._entries[string.Empty] != expected)
                {
                    throw new IOException("A temporary recovery copy ('" + file + "') changed unexpectedly, so MediaDash left it in place. Check it before deleting it.");
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
                    throw new IOException("A temporary recovery folder ('" + directory + "') changed unexpectedly, so MediaDash left it in place. Check it before deleting it.");
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

    private void Add(string path, string relative, bool allowDeleteHandle, Action<string, long, long>? progress, CancellationToken cancellationToken, bool recurse = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PhysicalFileIdentity.TryRead(path, out var identity) || identity.IsReparsePoint)
        {
            throw new IOException("Not moved: MediaDash could not check that '" + path + "' is the same file it checked before. Make sure the drive is connected, then run a scan.");
        }

        var isDirectory = Directory.Exists(path);
        if (isDirectory)
        {
            _entries.Add(relative, new Entry(true, 0, default, identity, string.Empty, MediaMoveMetadata.Capture(path, true, allowDeleteHandle)));
            foreach (var child in recurse ? Directory.EnumerateFileSystemEntries(path) : [])
            {
                Add(child, Path.Combine(relative, Path.GetFileName(child)), allowDeleteHandle, progress, cancellationToken);
            }
        }
        else
        {
            // Hardlinked files (a torrent client's seeding copy, or a second library name) are allowed:
            // every move is a same-filesystem rename (cross-volume moves are refused before this), which
            // changes only this name and leaves the other links and the data untouched.
            // Production 2026-11-22: hardlinked episodes could never be grouped.
            var info = new FileInfo(path);
            var length = info.Length;
            var modified = info.LastWriteTimeUtc;
            if (!_hashContent)
            {
                progress?.Invoke(path, length, length);
                _entries.Add(relative, new Entry(false, length, modified, identity, string.Empty, MediaMoveMetadata.Capture(path, false, allowDeleteHandle)));
                if (!PhysicalFileIdentity.TryRead(path, out var unchanged) || unchanged != identity || unchanged.IsReparsePoint)
                {
                    throw new IOException("Not moved: '" + path + "' was replaced by a different file or folder after MediaDash checked it. Run a scan to get an up-to-date plan, then approve it again.");
                }

                return;
            }

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
                throw new IOException("Not moved: '" + path + "' changed while MediaDash was checking it (it may still be downloading or being written). Try again once it has finished.");
            }

            _entries.Add(relative, new Entry(false, length, modified, identity, Convert.ToHexString(hash.GetHashAndReset()), MediaMoveMetadata.Capture(path, false, allowDeleteHandle)));
        }

        if (!PhysicalFileIdentity.TryRead(path, out var after) || after != identity || after.IsReparsePoint)
        {
            throw new IOException("Not moved: '" + path + "' was replaced by a different file or folder after MediaDash checked it. Run a scan to get an up-to-date plan, then approve it again.");
        }
    }

    private sealed record Entry(bool IsDirectory, long Length, DateTime Modified, PhysicalFileIdentity.Value Identity, string Hash, string Metadata);
}

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Probing;

namespace Jellyfin.Plugin.MediaDash.Data;

// Bind the persisted orphan finding to the directory and image bytes actually observed.
internal static class OrphanMetadataObservation
{
    internal static bool IsCurrent(Issue issue, [NotNullWhen(true)] out Snapshot? snapshot, CancellationToken cancellationToken)
    {
        snapshot = null;
        try
        {
            using var doc = JsonDocument.Parse(issue.DetailsJson);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("metadataObservation", out var observation) && observation.ValueKind == JsonValueKind.Object
                && observation.TryGetProperty("version", out var version) && version.TryGetInt32(out var number) && number == 1
                && observation.TryGetProperty("observedAtUtcTicks", out var observedAt) && observedAt.TryGetInt64(out var ticks)
                && ticks > 0 && ticks == issue.DetectedAtUtc.Ticks
                && observation.TryGetProperty("signature", out var signature) && signature.ValueKind == JsonValueKind.String
                && signature.GetString() is { Length: 64 } expected
                && TryCapture(issue.Path, out snapshot, cancellationToken)
                && string.Equals(expected, snapshot.Signature, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool TryCapture(string path, [NotNullWhen(true)] out Snapshot? snapshot, CancellationToken cancellationToken)
    {
        snapshot = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(path) || !PhysicalFileIdentity.TryRead(path, out var directory) || directory.IsReparsePoint)
            {
                return false;
            }

            var directoryWrite = Directory.GetLastWriteTimeUtc(path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{directory.Volume}:{directory.FileId}:{directoryWrite.Ticks}\0")));
            var buffer = new byte[64 * 1024];
            var files = new List<FileState>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(path).OrderBy(entry => entry, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                    || !PhysicalFileIdentity.TryRead(entry, out var identity) || identity.IsReparsePoint)
                {
                    return false;
                }

                var info = new FileInfo(entry);
                var length = info.Length;
                var written = info.LastWriteTimeUtc;
                files.Add(new FileState(entry, identity, length, written));
                hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{Path.GetFileName(entry)}\0{identity.Volume}:{identity.FileId}:{length}:{written.Ticks}\0")));
                using (var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int read;
                    while ((read = stream.Read(buffer)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        hash.AppendData(buffer.AsSpan(0, read));
                    }
                }

                info.Refresh();
                if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != written
                    || !PhysicalFileIdentity.TryRead(entry, out var current) || current != identity || current.IsReparsePoint)
                {
                    return false;
                }
            }

            if (Directory.GetLastWriteTimeUtc(path) != directoryWrite
                || !PhysicalFileIdentity.TryRead(path, out var final) || final != directory || final.IsReparsePoint)
            {
                return false;
            }

            snapshot = new Snapshot(path, directory, directoryWrite, files, Convert.ToHexString(hash.GetHashAndReset()));
            return snapshot.IsCurrent(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal sealed record FileState(string Path, PhysicalFileIdentity.Value Identity, long Length, DateTime Written);

    internal sealed record Snapshot(string Path, PhysicalFileIdentity.Value DirectoryIdentity, DateTime DirectoryWrite, IReadOnlyList<FileState> Files, string Signature)
    {
        internal bool IsCurrent(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.EnumerateFileSystemEntries(Path).OrderBy(entry => entry, StringComparer.Ordinal)
                    .SequenceEqual(Files.Select(file => file.Path), StringComparer.Ordinal))
                {
                    return false;
                }

                foreach (var file in Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var info = new FileInfo(file.Path);
                    if (!info.Exists || info.Length != file.Length || info.LastWriteTimeUtc != file.Written
                        || !PhysicalFileIdentity.TryRead(file.Path, out var current) || current != file.Identity || current.IsReparsePoint)
                    {
                        return false;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                return Directory.GetLastWriteTimeUtc(Path) == DirectoryWrite
                    && PhysicalFileIdentity.TryRead(Path, out var directory) && directory == DirectoryIdentity && !directory.IsReparsePoint;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }
    }
}

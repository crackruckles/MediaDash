using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Reads the file list of an ISO 9660 disc image (with Joliet long names when present) without
/// mounting it. A real DVD or Blu-ray image holds a <c>VIDEO_TS</c> or <c>BDMV</c> folder; a fake
/// "episode" image that holds a Windows program instead is a common malware wrapper on torrent
/// sites (production 2026-11-27: a 1 GB "MobLand S02E08 … .iso" holding one .EXE).
/// </summary>
/// <remarks>
/// Only the directory records are read — a few kilobytes — never file contents. Images in other
/// formats (UDF-only Blu-ray images, Nero .nrg) are reported as unreadable and never flagged.
/// </remarks>
internal static class DiscImageInspector
{
    private const int SectorSize = 2048;
    private const int MaxEntries = 5000;
    private const int MaxDepth = 8;
    private const int MaxDirectoryBytes = 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".iso", ".img" };

    private static readonly HashSet<string> VideoFolders = new(StringComparer.OrdinalIgnoreCase) { "VIDEO_TS", "BDMV", "HVDVD_TS", "AUDIO_TS" };

    /// <summary>True for the disc-image extensions this inspector reads.</summary>
    /// <param name="path">File path.</param>
    /// <returns>Whether the file is a disc image to inspect.</returns>
    internal static bool IsDiscImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>Reads a disc image's file list.</summary>
    /// <param name="path">The image file.</param>
    /// <returns>What the image holds; <see cref="Result.Readable"/> is false when it is not an ISO 9660 image.</returns>
    internal static Result Inspect(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, SectorSize, FileOptions.RandomAccess);
            return Inspect(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Unreadable;
        }
    }

    /// <summary>Reads a disc image's file list from a stream.</summary>
    /// <param name="stream">Seekable image stream.</param>
    /// <returns>What the image holds.</returns>
    internal static Result Inspect(Stream stream)
    {
        (long Extent, long Length)? primary = null, joliet = null;
        var buffer = new byte[SectorSize];
        for (var sector = 16; sector < 64; sector++)
        {
            if (!ReadAt(stream, (long)sector * SectorSize, buffer) || Encoding.ASCII.GetString(buffer, 1, 5) != "CD001")
            {
                break;
            }

            var type = buffer[0];
            if (type == 255)
            {
                break;
            }

            var root = (BitConverter.ToUInt32(buffer, 156 + 2), BitConverter.ToUInt32(buffer, 156 + 10));
            if (type == 1)
            {
                primary ??= root;
            }
            else if (type == 2 && buffer[88] == 0x25 && buffer[89] == 0x2F && buffer[90] is 0x40 or 0x43 or 0x45)
            {
                joliet ??= root;
            }
        }

        if ((joliet ?? primary) is not { } start)
        {
            return Result.Unreadable;
        }

        var unicode = joliet is not null;
        var files = new List<string>();
        var rootFolders = new List<string>();
        var queue = new Queue<(long Extent, long Length, string Prefix, int Depth)>();
        queue.Enqueue((start.Extent, start.Length, string.Empty, 0));
        var visited = new HashSet<long>();
        while (queue.Count > 0 && files.Count < MaxEntries)
        {
            var (extent, length, prefix, depth) = queue.Dequeue();
            if (length <= 0 || length > MaxDirectoryBytes || !visited.Add(extent))
            {
                continue;
            }

            var directory = new byte[length];
            if (!ReadAt(stream, extent * SectorSize, directory))
            {
                return Result.Unreadable;
            }

            for (var offset = 0; offset < directory.Length && files.Count < MaxEntries;)
            {
                var recordLength = directory[offset];
                if (recordLength == 0)
                {
                    // Records never cross a sector; the rest of this sector is padding.
                    offset = ((offset / SectorSize) + 1) * SectorSize;
                    continue;
                }

                if (offset + recordLength > directory.Length || recordLength < 34)
                {
                    break;
                }

                var nameLength = directory[offset + 32];
                if (33 + nameLength > recordLength)
                {
                    break;
                }

                var isDirectory = (directory[offset + 25] & 2) != 0;
                var childExtent = BitConverter.ToUInt32(directory, offset + 2);
                var childLength = BitConverter.ToUInt32(directory, offset + 10);
                var nameBytes = directory.AsSpan(offset + 33, nameLength);
                offset += recordLength;
                if (nameLength == 1 && nameBytes[0] <= 1)
                {
                    continue; // "." and ".."
                }

                var name = unicode ? Encoding.BigEndianUnicode.GetString(nameBytes) : Encoding.ASCII.GetString(nameBytes);
                var version = name.IndexOf(';', StringComparison.Ordinal);
                if (version >= 0)
                {
                    name = name[..version];
                }

                name = name.TrimEnd('.');
                if (isDirectory)
                {
                    if (depth == 0)
                    {
                        rootFolders.Add(name);
                    }

                    if (depth + 1 < MaxDepth)
                    {
                        queue.Enqueue((childExtent, childLength, prefix + name + "/", depth + 1));
                    }
                }
                else
                {
                    files.Add(prefix + name);
                }
            }
        }

        return new Result(
            true,
            rootFolders.Any(VideoFolders.Contains),
            files.Where(SuspiciousFileScanner.IsSuspicious).ToList(),
            files.Any(file => MediaFormats.Video.Contains(Path.GetExtension(file)) || MediaFormats.Audio.Contains(Path.GetExtension(file))),
            files.Count);
    }

    private static bool ReadAt(Stream stream, long position, byte[] buffer)
    {
        if (position < 0 || position + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = position;
        stream.ReadExactly(buffer);
        return true;
    }

    /// <summary>What a disc image holds.</summary>
    /// <param name="Readable">False when the file is not an ISO 9660 image MediaDash can read.</param>
    /// <param name="HasDiscVideo">True when the image has a DVD or Blu-ray video folder at its root.</param>
    /// <param name="Programs">Program and script files inside the image.</param>
    /// <param name="HasMediaFiles">True when the image holds loose video or audio files.</param>
    /// <param name="FileCount">Files listed.</param>
    internal sealed record Result(bool Readable, bool HasDiscVideo, IReadOnlyList<string> Programs, bool HasMediaFiles, int FileCount)
    {
        /// <summary>Gets the result for a file that is not a readable ISO 9660 image.</summary>
        internal static Result Unreadable { get; } = new(false, false, [], false, 0);

        /// <summary>
        /// Gets a value indicating whether the image holds programs or scripts and no disc video.
        /// Retail DVDs and Blu-rays sometimes carry PC extras (a player installer, autorun), so a
        /// program beside a real <c>VIDEO_TS</c>/<c>BDMV</c> folder is not flagged.
        /// </summary>
        internal bool HoldsPrograms => Programs.Count > 0 && !HasDiscVideo;

        /// <summary>Gets a value indicating whether the image is readable but holds no video at all.</summary>
        internal bool HoldsNoVideo => Readable && FileCount > 0 && !HasDiscVideo && !HasMediaFiles && Programs.Count == 0;
    }
}

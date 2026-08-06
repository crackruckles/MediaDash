using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Looks for MediaDash re-encodes that appear to have been undone by an external process
/// (typically Sonarr/Radarr replacing a "too-small" file that no longer meets its quality profile,
/// or a manual restore from the recycle bin).
/// </summary>
public static class RedownloadDetector
{
    /// <summary>
    /// Compares each recent successful re-encode against the current file at the same path.
    /// When the current file is close to the size of the original (still preserved in the recycle
    /// bin), the re-encode was undone.
    /// </summary>
    /// <param name="db">The plugin database.</param>
    /// <param name="lookback">How far back to check history. 30 days matches the default recycle-bin retention.</param>
    /// <returns>A list of suspected redownload cases, newest first, capped at 25.</returns>
    public static IReadOnlyList<RedownloadWarning> Detect(MediaDashDb db, TimeSpan lookback)
    {
        var cutoff = DateTime.UtcNow - lookback;
        var warnings = new List<RedownloadWarning>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in db.GetHistory(2000))
        {
            if (entry.FixedAtUtc < cutoff)
            {
                break;
            }

            if (!entry.Success
                || entry.WasDryRun
                || entry.Restored
                || string.IsNullOrEmpty(entry.RecyclePath)
                || (entry.Type != IssueType.Quality && entry.Type != IssueType.AudioLanguage && entry.Type != IssueType.SubtitleLanguage))
            {
                continue;
            }

            if (!seen.Add(entry.Path))
            {
                // Latest fix per path is authoritative; a later restore-and-re-fix would show up as a
                // newer history row and we already saw it.
                continue;
            }

            long currentSize;
            long originalSize;
            try
            {
                if (!File.Exists(entry.Path) || !File.Exists(entry.RecyclePath))
                {
                    continue;
                }

                currentSize = new FileInfo(entry.Path).Length;
                originalSize = new FileInfo(entry.RecyclePath).Length;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            // File at the same path is now within 10% of the original we recycled — someone put
            // something roughly the pre-fix size back where we left the smaller copy. That's
            // Sonarr/Radarr redownload behaviour or a manual bin-restore either way we want the
            // user to know before the next scan re-flags the file.
            if (currentSize < (long)(originalSize * 0.9))
            {
                continue;
            }

            warnings.Add(new RedownloadWarning
            {
                Path = entry.Path,
                CurrentBytes = currentSize,
                OriginalBytes = originalSize,
                FixedAtUtc = entry.FixedAtUtc,
                RecyclePath = entry.RecyclePath
            });

            if (warnings.Count >= 25)
            {
                break;
            }
        }

        return warnings;
    }
}

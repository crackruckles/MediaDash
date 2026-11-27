using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.MediaDash.Api;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Preserves a completed encode/remux output when the final swap fails after the original has been
/// disposed. Renames the sidecar out of the sweep's <c>*.mediadash.new*</c> / <c>*.mediadash.swap*</c>
/// pattern set into a distinct <c>*.mediadash-recover-*</c> naming that <see cref="LibraryGuard.SweepOrphanSidecars"/>
/// explicitly does NOT touch. Backs review finding P1-1: without this rename, the sidecar
/// containing the only remaining copy of the user's file gets swept the next fix cycle.
/// </summary>
internal static class SwapRecoveryHelper
{
    /// <summary>
    /// Marker infix used in the preserved filename. Kept in sync with the negative check in
    /// <see cref="LibraryGuard.SweepOrphanSidecars"/> — do not rename either without updating both.
    /// </summary>
    public const string RecoveryMarker = ".mediadash-recover-";

    // Same NAME_MAX budget SidecarPath uses. Anything beyond this triggers the hash-fallback name so
    // a real 244-byte UTF-8 basename doesn't overflow after the recovery marker + timestamp + suffix.
    private const int NameByteBudget = 240;

    /// <summary>
    /// Renames <paramref name="swapPath"/> to a recovery-tagged sibling of <paramref name="originalPath"/>
    /// so the sweep can't delete it on the next fix cycle. Returns the new path on success.
    /// Bug-pattern audit round 4 (2026-09-19) — P-03 reproduced two silent-failure modes on the
    /// pre-fix helper: (1) directory occupying the recovery destination (File.Exists returned false
    /// so the collision loop skipped, then File.Move failed, then caller thought preservation
    /// succeeded); (2) a 244-byte UTF-8 basename overflowed NAME_MAX after the marker was
    /// appended, same silent failure. Both fixed by mirroring SidecarPath's hash-fallback naming
    /// and by probing both File.Exists AND Directory.Exists in the collision loop. Additional
    /// belt-and-braces: if the primary File.Move fails, fall back to File.Copy which succeeds
    /// even when the swap path is briefly locked; if that ALSO fails, emit a red-alert
    /// diagnostic naming the swap path so the user is warned in strong terms rather than the
    /// pre-fix "the file is at X" tone (which the sweep then deleted).
    /// </summary>
    /// <param name="swapPath">The completed-but-unswapped output file.</param>
    /// <param name="originalPath">The target path the swap was trying to reach.</param>
    /// <returns>The preserved path (recovery-tagged sibling on success; the original swapPath on total-failure fallback).</returns>
    public static string TryPreserve(string swapPath, string originalPath)
    {
        try
        {
            var candidate = BuildRecoveryPath(originalPath);
            var attempt = 0;
            while (PathExists(candidate) && attempt < 1000)
            {
                attempt++;
                candidate = BuildRecoveryPath(originalPath, suffix: "-" + attempt.ToString(CultureInfo.InvariantCulture));
            }

            try
            {
                File.Move(swapPath, candidate);
                return candidate;
            }
            catch (IOException)
            {
                // Move can fail if the destination directory has permission quirks, the swap file
                // is briefly locked by an antivirus scan, or the destination fills the disk during
                // a rename-across-directory operation. Copy is a legitimate second attempt — leaves
                // the swap in place but produces a survivor at the recovery path.
                File.Copy(swapPath, candidate, overwrite: false);
                // Best-effort tidy of the now-orphaned swap. If this fails, the sweep will pick it
                // up next cycle — the survivor at `candidate` sits outside the sweep pattern.
                try
                {
                    File.Delete(swapPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                return candidate;
            }
        }
        catch (Exception ex)
        {
            // Total failure — neither Move nor Copy worked, or the candidate build threw. This is
            // the branch that used to silently return swapPath (which the sweep then deleted).
            // Emit an urgent diagnostic so the user is warned in strong terms and can move the
            // file manually before the next fix cycle. The returned path still points at swapPath
            // for the caller's own diagnostic, but the escalation here is the real protection.
            Diagnostics.Record("SwapRecovery.PreserveFailed", "IMPORTANT: MediaDash could not protect the backup copy at '" + swapPath + "', and the next fix run will delete it. If the original '" + originalPath + "' is gone, this is your only copy: move it somewhere safe now. Details: " + ex.Message);
            return swapPath;
        }
    }

    /// <summary>
    /// Returns true when the given filename is a preserved swap recovery copy. Used by the sweep
    /// to skip these files even if they somehow match a broader pattern.
    /// </summary>
    /// <param name="path">Full path or filename to check.</param>
    /// <returns>True when the path carries the recovery marker.</returns>
    public static bool IsRecoveryCopy(string path)
        => !string.IsNullOrEmpty(path) && Path.GetFileName(path).Contains(RecoveryMarker, StringComparison.Ordinal);

    /// <summary>
    /// Root-cause audit 2026-09-19 R02+R03. Renames a verified swap sidecar to a
    /// <c>*.mediadash-recover-*</c> name OUTSIDE every <see cref="LibraryGuard"/> sweep pattern
    /// BEFORE the caller disposes the original. This closes the crash-between-dispose-and-final-
    /// rename window that used to leave the only-remaining copy at a sweep-doomed
    /// <c>.mediadash.swap</c> / <c>.mediadash.new.&lt;token&gt;</c> path.
    ///
    /// Contract for callers: <c>false</c> means preservation could not be established — do NOT
    /// dispose the original. The swap file at its input path is either intact (sweep will clean
    /// it up next cycle; original still present) or the source vanished (nothing to protect).
    /// <c>true</c> hands back the recovery path in <paramref name="preservedPath"/>; complete the
    /// swap by <c>File.Move(preservedPath, target)</c> after the dispose.
    /// </summary>
    /// <param name="swapPath">Verified swap sidecar (already through post-encode ffprobe check).</param>
    /// <param name="originalPath">The path the swap is going to replace once dispose completes.</param>
    /// <param name="preservedPath">On success, the recovery-tagged sibling path; null on failure.</param>
    /// <returns>True when the swap has been renamed outside the sweep pattern set.</returns>
    public static bool TryPromoteBeforeDispose(string swapPath, string originalPath, out string? preservedPath)
    {
        preservedPath = null;
        if (string.IsNullOrEmpty(swapPath) || !File.Exists(swapPath))
        {
            // Either called out of order or the swap already vanished. Neither dispose nor final
            // rename can complete safely from here — caller aborts.
            return false;
        }

        try
        {
            var candidate = BuildRecoveryPath(originalPath);
            var attempt = 0;
            while (PathExists(candidate) && attempt < 1000)
            {
                attempt++;
                candidate = BuildRecoveryPath(originalPath, suffix: "-" + attempt.ToString(CultureInfo.InvariantCulture));
            }

            try
            {
                File.Move(swapPath, candidate);
                preservedPath = candidate;
                return true;
            }
            catch (IOException)
            {
                // Same fallback ladder as TryPreserve: rare rename quirks (permission, antivirus lock,
                // cross-directory move on some filesystems) don't kill the promote — a copy that leaves
                // the swap in place still produces a survivor at the recovery path.
                File.Copy(swapPath, candidate, overwrite: false);
                try
                {
                    File.Delete(swapPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                preservedPath = candidate;
                return true;
            }
        }
        catch (Exception ex)
        {
            // Total failure: neither Move nor Copy worked. Escalate loudly — the caller aborts the
            // fix and the sweep can clean up the swap next cycle because the original is still there.
            Diagnostics.Record("SwapRecovery.PromoteFailed", "A fix was stopped to keep your original '" + originalPath + "' safe: MediaDash could not set aside its new copy ('" + swapPath + "'). The new copy will be cleaned up automatically. If this keeps happening, check the folder's permissions and free space. Details: " + ex.Message);
            return false;
        }
    }

    // Bounded recovery-path builder. When the ideal name (full basename + marker + timestamp + ext)
    // would overflow the OS 255-byte NAME_MAX budget, fall back to a hash-based name — same
    // strategy `TranscodeFixer.SidecarPath` uses. Timestamp is second-precision; callers append a
    // "-N" suffix via the `suffix` parameter to disambiguate same-second collisions.
    private static string BuildRecoveryPath(string originalPath, string suffix = "")
    {
        var dir = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var basename = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = basename + RecoveryMarker + stamp + suffix + ext;
        if (Encoding.UTF8.GetByteCount(candidate) <= NameByteBudget)
        {
            return Path.Combine(dir, candidate);
        }

        // Hash-fallback name embeds the full RecoveryMarker so IsRecoveryCopy still matches — the
        // sweep negative-check depends on that infix regardless of which naming branch produced it.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basename)), 0, 8);
        return Path.Combine(dir, hash + RecoveryMarker + stamp + suffix + ext);
    }

    // Existence probe that treats file and directory the same. The pre-fix collision loop only
    // called File.Exists, so a directory at the candidate path fell through as "free" and the
    // subsequent File.Move failed with IOException — which the pre-fix outer catch silently
    // returned swapPath from.
    private static bool PathExists(string path)
        => File.Exists(path) || Directory.Exists(path);
}

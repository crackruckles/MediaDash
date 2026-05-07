using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Api;

// ── File explorer models ────────────────────────────────────────────────────

/// <summary>A single file or directory entry returned by the file explorer.</summary>
public record FsEntry(
    string Name,
    string Path,
    string Type,
    long   SizeBytes,
    string SizeFmt,
    string Modified,
    string? Ext);

/// <summary>Contents of a directory listing.</summary>
public record FsListing(
    string    Root,
    string    Current,
    string[]  Breadcrumbs,
    FsEntry[] Dirs,
    FsEntry[] Files,
    long      TotalBytes);

/// <summary>Result of a file-system operation (rename, delete, move, copy).</summary>
public record FsOpResult(
    bool Ok,
    string? Error = null);

// ── Auto-sort models ────────────────────────────────────────────────────────

/// <summary>
/// A group of folders that share the same IMDB ID and are candidates for merging.
/// </summary>
public record SortCandidate(
    string ImdbId,
    string Title,
    int    Year,
    string Type,
    string CanonicalName,
    string CanonicalPath,
    FolderGroup[] Groups);

/// <summary>A single folder within a sort candidate group.</summary>
public record FolderGroup(
    string Path,
    string Name,
    int    FileCount,
    long   SizeBytes,
    bool   IsCanonical);

/// <summary>Preview of all merge operations that would be performed by auto-sort.</summary>
public record SortPreview(
    SortCandidate[] Candidates,
    int             TotalFolders,
    long            TotalBytes,
    string[]        Errors);

// ── Service ─────────────────────────────────────────────────────────────────

public sealed class FileExplorerService
{
    private readonly ILibraryManager        _lib;
    private readonly ILogger<FileExplorerService> _log;
    private static readonly HttpClient _http = new();

    public FileExplorerService(ILibraryManager lib, ILogger<FileExplorerService> log)
    {
        _lib = lib; _log = log;
    }

    private static PluginConfiguration Cfg => Plugin.Instance!.Configuration;

    // ── Root guard ─────────────────────────────────────────────────────────

    /// <summary>Returns allowed root paths from Jellyfin libraries.</summary>
    private IReadOnlyList<string> AllowedRoots()
    {
        var roots = new List<string>();
        foreach (var folder in _lib.GetVirtualFolders())
            foreach (var loc in folder.Locations)
                if (!string.IsNullOrEmpty(loc))
                    roots.Add(Path.GetFullPath(loc).TrimEnd(Path.DirectorySeparatorChar));
        return roots;
    }

    /// <summary>Throws if path escapes every allowed root.</summary>
    private void AssertAllowed(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var roots = AllowedRoots();
        if (!roots.Any(r => full.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                            full.StartsWith(r + Path.DirectorySeparatorChar,
                                            StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException(
                $"Path '{path}' is outside all configured library roots.");
    }

    // ── Listing ────────────────────────────────────────────────────────────

    public FsListing List(string path)
    {
        AssertAllowed(path);
        var di = new DirectoryInfo(path);
        if (!di.Exists) throw new DirectoryNotFoundException($"Directory not found: {path}");

        var dirs  = new List<FsEntry>();
        var files = new List<FsEntry>();
        long total = 0;

        foreach (var d in di.GetDirectories().OrderBy(d => d.Name))
        {
            try
            {
                dirs.Add(new FsEntry(d.Name, d.FullName, "dir",
                    0, "–", d.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), null));
            }
            catch { /* skip inaccessible */ }
        }

        foreach (var f in di.GetFiles().OrderBy(f => f.Name))
        {
            try
            {
                total += f.Length;
                files.Add(new FsEntry(f.Name, f.FullName, "file",
                    f.Length, FmtSize(f.Length),
                    f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    f.Extension.ToLowerInvariant()));
            }
            catch { /* skip inaccessible */ }
        }

        // Build breadcrumbs within the allowed root
        var root   = AllowedRoots().FirstOrDefault(r =>
            path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) ?? path;
        var crumbs = BuildBreadcrumbs(path, root);

        return new FsListing(root, path, crumbs, dirs.ToArray(), files.ToArray(), total);
    }

    private static string[] BuildBreadcrumbs(string path, string root)
    {
        var parts = new List<string> { root };
        var rel   = Path.GetFullPath(path).Substring(
            Path.GetFullPath(root).Length).TrimStart(Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(rel)) return parts.ToArray();
        var current = root;
        foreach (var seg in rel.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, seg);
            parts.Add(current);
        }
        return parts.ToArray();
    }

    // ── File operations ────────────────────────────────────────────────────

    public FsOpResult Rename(string path, string newName)
    {
        try
        {
            AssertAllowed(path);
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return new FsOpResult(false, "Invalid characters in name.");
            var dir  = Path.GetDirectoryName(path)!;
            var dest = Path.Combine(dir, newName);
            AssertAllowed(dest);
            if (File.Exists(path))       File.Move(path, dest);
            else if (Directory.Exists(path)) Directory.Move(path, dest);
            else return new FsOpResult(false, "Source not found.");
            return new FsOpResult(true);
        }
        catch (UnauthorizedAccessException ex) { return new FsOpResult(false, ex.Message); }
        catch (Exception ex) { _log.LogError(ex, "Rename {P}", path); return new FsOpResult(false, ex.Message); }
    }

    public FsOpResult Delete(string path)
    {
        try
        {
            AssertAllowed(path);
            if (File.Exists(path))
            {
                File.Delete(path);
                return new FsOpResult(true);
            }
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return new FsOpResult(true);
            }
            return new FsOpResult(false, "Path not found.");
        }
        catch (UnauthorizedAccessException ex) { return new FsOpResult(false, ex.Message); }
        catch (Exception ex) { _log.LogError(ex, "Delete {P}", path); return new FsOpResult(false, ex.Message); }
    }

    public FsOpResult Move(string sourcePath, string destDir)
    {
        try
        {
            AssertAllowed(sourcePath);
            AssertAllowed(destDir);
            if (!Directory.Exists(destDir))
                return new FsOpResult(false, "Destination directory not found.");
            var destPath = Path.Combine(destDir, Path.GetFileName(sourcePath));
            if (File.Exists(sourcePath))       File.Move(sourcePath, destPath, overwrite: false);
            else if (Directory.Exists(sourcePath)) Directory.Move(sourcePath, destPath);
            else return new FsOpResult(false, "Source not found.");
            return new FsOpResult(true);
        }
        catch (UnauthorizedAccessException ex) { return new FsOpResult(false, ex.Message); }
        catch (Exception ex) { _log.LogError(ex, "Move {P}", sourcePath); return new FsOpResult(false, ex.Message); }
    }

    public FsOpResult Copy(string sourcePath, string destDir)
    {
        try
        {
            AssertAllowed(sourcePath);
            AssertAllowed(destDir);
            if (!Directory.Exists(destDir))
                return new FsOpResult(false, "Destination directory not found.");
            if (File.Exists(sourcePath))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, dest, overwrite: false);
                return new FsOpResult(true);
            }
            if (Directory.Exists(sourcePath))
            {
                CopyDir(sourcePath, Path.Combine(destDir, Path.GetFileName(sourcePath)));
                return new FsOpResult(true);
            }
            return new FsOpResult(false, "Source not found.");
        }
        catch (UnauthorizedAccessException ex) { return new FsOpResult(false, ex.Message); }
        catch (Exception ex) { _log.LogError(ex, "Copy {P}", sourcePath); return new FsOpResult(false, ex.Message); }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // ── Auto-sort: build preview ───────────────────────────────────────────

    public async Task<SortPreview> BuildSortPreview(string libraryRoot)
    {
        AssertAllowed(libraryRoot);
        var tmdbKey = Cfg.TmdbApiKey;
        var errors  = new List<string>();
        var byImdb  = new Dictionary<string, List<(string path, int files, long bytes)>>(
                          StringComparer.OrdinalIgnoreCase);

        // Scan top-level folders — each is a title candidate
        var topDirs = Directory.GetDirectories(libraryRoot);
        foreach (var dir in topDirs)
        {
            try
            {
                var name    = Path.GetFileName(dir);
                var imdbId  = await LookupImdb(name, tmdbKey, errors);
                if (string.IsNullOrEmpty(imdbId)) continue;

                var fileCount = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
                var sizeBytes = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                                    .Sum(f => new FileInfo(f).Length);

                if (!byImdb.TryGetValue(imdbId, out var list))
                    byImdb[imdbId] = list = new();
                list.Add((dir, fileCount, sizeBytes));
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(dir)}: {ex.Message}"); }
        }

        // Build candidates — only where there are 2+ folders with same IMDB ID
        var candidates = new List<SortCandidate>();
        foreach (var (imdbId, groups) in byImdb)
        {
            if (groups.Count < 2) continue;

            // Canonical = group with most files (ties: most bytes)
            var canonical = groups.OrderByDescending(g => g.files)
                                  .ThenByDescending(g => g.bytes).First();

            // Look up title/year from TMDB
            var (title, year, type) = await GetTitleInfo(imdbId, tmdbKey, errors);

            candidates.Add(new SortCandidate(
                ImdbId:        imdbId,
                Title:         title,
                Year:          year,
                Type:          type,
                CanonicalName: Path.GetFileName(canonical.path),
                CanonicalPath: canonical.path,
                Groups: groups.Select(g => new FolderGroup(
                    Path:        g.path,
                    Name:        Path.GetFileName(g.path),
                    FileCount:   g.files,
                    SizeBytes:   g.bytes,
                    IsCanonical: g.path == canonical.path)).ToArray()));
        }

        return new SortPreview(
            Candidates:   candidates.OrderBy(c => c.Title).ToArray(),
            TotalFolders: candidates.Sum(c => c.Groups.Length),
            TotalBytes:   candidates.Sum(c => c.Groups.Sum(g => (long)g.SizeBytes)),
            Errors:       errors.ToArray());
    }

    // ── Auto-sort: execute ─────────────────────────────────────────────────

    public FsOpResult ExecuteSort(string libraryRoot, string imdbId)
    {
        AssertAllowed(libraryRoot);
        var errors = new List<string>();

        // Re-read the canonical info from disk (don't trust client-sent paths)
        var byImdb = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(libraryRoot))
        {
            // We stored IMDB IDs as a marker file .imdb_id during preview — or re-lookup
            // Simpler: look for .nfo / tmdb marker files with the ID, else skip
            // For now: re-lookup using cached TMDB (rate-limited, so we store in temp file)
            var markerFile = Path.Combine(dir, ".mediadash_imdb");
            if (!File.Exists(markerFile)) continue;
            var storedId = File.ReadAllText(markerFile).Trim();
            if (!string.Equals(storedId, imdbId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!byImdb.TryGetValue(storedId, out var list)) byImdb[storedId] = list = new();
            list.Add(dir);
        }

        if (!byImdb.TryGetValue(imdbId, out var folders) || folders.Count < 2)
            return new FsOpResult(false, "Not enough folders found for this IMDB ID — run a fresh preview first.");

        // Canonical = most files
        var canonical = folders.OrderByDescending(d =>
            Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length).First();

        foreach (var folder in folders.Where(f => f != canonical))
        {
            try
            {
                AssertAllowed(folder);
                // Move all files from this folder into canonical
                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    var rel  = Path.GetRelativePath(folder, file);
                    var dest = Path.Combine(canonical, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (!File.Exists(dest))
                        File.Move(file, dest);
                    else
                        errors.Add($"Skipped (exists): {rel}");
                }
                // Delete now-empty folder
                if (!Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Any())
                    Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(folder)}: {ex.Message}"); }
        }

        if (errors.Any()) return new FsOpResult(false, string.Join("; ", errors));
        return new FsOpResult(true);
    }

    // ── TMDB helpers ───────────────────────────────────────────────────────

    private async Task<string?> LookupImdb(string folderName, string apiKey, List<string> errors)
    {
        if (string.IsNullOrEmpty(apiKey)) return null;

        // Parse year from folder name: "Title (2020)" or "Title.2020" etc.
        var clean = System.Text.RegularExpressions.Regex
            .Replace(folderName, @"[\._]", " ").Trim();
        var yearMatch = System.Text.RegularExpressions.Regex.Match(clean, @"\((\d{4})\)");
        var year      = yearMatch.Success ? yearMatch.Groups[1].Value : "";
        var title     = yearMatch.Success ? clean[..clean.LastIndexOf('(')].Trim() : clean;

        // Check local cache first
        var cacheKey  = $"{title}_{year}".ToLowerInvariant()
                             .Replace(" ","_");
        var cacheFile = Path.Combine(Path.GetTempPath(), "mediadash_tmdb",
                            cacheKey + ".json");
        if (File.Exists(cacheFile) && File.GetLastWriteTime(cacheFile) > DateTime.Now.AddDays(-30))
        {
            var cached = JsonDocument.Parse(await File.ReadAllTextAsync(cacheFile));
            return cached.RootElement.TryGetProperty("imdb_id", out var v) ? v.GetString() : null;
        }

        try
        {
            // Search TMDB
            var searchUrl = $"https://api.themoviedb.org/3/search/multi?api_key={apiKey}" +
                            $"&query={Uri.EscapeDataString(title)}" +
                            (year.Length > 0 ? $"&year={year}" : "");
            var searchResp = await _http.GetStringAsync(searchUrl);
            var searchDoc  = JsonDocument.Parse(searchResp);
            var results    = searchDoc.RootElement.GetProperty("results");
            if (results.GetArrayLength() == 0) return null;

            var top    = results[0];
            var tmdbId = top.GetProperty("id").GetInt32();
            var mtype  = top.GetProperty("media_type").GetString();

            // Get IMDB ID from details endpoint
            var detailUrl = mtype == "tv"
                ? $"https://api.themoviedb.org/3/tv/{tmdbId}/external_ids?api_key={apiKey}"
                : $"https://api.themoviedb.org/3/movie/{tmdbId}/external_ids?api_key={apiKey}";
            var detailResp = await _http.GetStringAsync(detailUrl);
            var detailDoc  = JsonDocument.Parse(detailResp);

            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            await File.WriteAllTextAsync(cacheFile, detailResp);

            return detailDoc.RootElement.TryGetProperty("imdb_id", out var imdb)
                ? imdb.GetString() : null;
        }
        catch (Exception ex)
        {
            errors.Add($"TMDB lookup failed for '{title}': {ex.Message}");
            return null;
        }
    }

    private async Task<(string title, int year, string type)> GetTitleInfo(
        string imdbId, string apiKey, List<string> errors)
    {
        if (string.IsNullOrEmpty(apiKey)) return (imdbId, 0, "unknown");
        try
        {
            var url  = $"https://api.themoviedb.org/3/find/{imdbId}?api_key={apiKey}&external_source=imdb_id";
            var resp = JsonDocument.Parse(await _http.GetStringAsync(url));
            var root = resp.RootElement;

            if (root.GetProperty("movie_results").GetArrayLength() > 0)
            {
                var m     = root.GetProperty("movie_results")[0];
                var title = m.TryGetProperty("title", out var t) ? t.GetString() ?? imdbId : imdbId;
                var date  = m.TryGetProperty("release_date", out var d) ? d.GetString() ?? "" : "";
                var year  = date.Length >= 4 && int.TryParse(date[..4], out var y) ? y : 0;
                return (title, year, "movie");
            }
            if (root.GetProperty("tv_results").GetArrayLength() > 0)
            {
                var s     = root.GetProperty("tv_results")[0];
                var title = s.TryGetProperty("name", out var n) ? n.GetString() ?? imdbId : imdbId;
                var date  = s.TryGetProperty("first_air_date", out var d) ? d.GetString() ?? "" : "";
                var year  = date.Length >= 4 && int.TryParse(date[..4], out var y) ? y : 0;
                return (title, year, "tv");
            }
        }
        catch (Exception ex) { errors.Add($"Title lookup for {imdbId}: {ex.Message}"); }
        return (imdbId, 0, "unknown");
    }

    // ── Sort: write marker files ───────────────────────────────────────────

    public async Task WriteImdbMarkers(string libraryRoot, List<string> errors)
    {
        var tmdbKey = Cfg.TmdbApiKey;
        if (string.IsNullOrEmpty(tmdbKey)) { errors.Add("No TMDB API key configured."); return; }

        foreach (var dir in Directory.GetDirectories(libraryRoot))
        {
            try
            {
                var name   = Path.GetFileName(dir);
                var imdbId = await LookupImdb(name, tmdbKey, errors);
                if (string.IsNullOrEmpty(imdbId)) continue;
                await File.WriteAllTextAsync(Path.Combine(dir, ".mediadash_imdb"), imdbId);
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(dir)}: {ex.Message}"); }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string FmtSize(long bytes) =>
        bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:F1} GB" :
        bytes >= 1_048_576     ? $"{bytes / 1_048_576.0:F1} MB"     :
        bytes >= 1_024         ? $"{bytes / 1_024.0:F0} KB"          :
                                 $"{bytes} B";
}

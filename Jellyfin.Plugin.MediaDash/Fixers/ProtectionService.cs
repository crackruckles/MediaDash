using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Runtime decision service for the "Never modify" protection roadmap card
/// (i-110-protected-media). Loads rules from <see cref="ProtectionStore"/>, caches them until
/// <see cref="Invalidate"/> is called, and answers "is this scanner / this fixer blocked for
/// this path?" at scan-emit and fix-execute time.
/// </summary>
/// <remarks>
/// Path matching follows <see cref="LibraryGuard"/>: case-insensitive on Windows and
/// case-sensitive on Linux, where differently cased directories can contain different media.
/// ponytail: rules are cached forever until Invalidate is called by the CRUD endpoints; a
/// per-rule TTL only matters if rules ever gain server-side auto-expiry (they don't).
/// </remarks>
public sealed class ProtectionService
{
    private const string MaskAll = "*";
    private const string KindFolder = "folder";
    private const string KindLibrary = "library";

    private readonly ProtectionStore _store;
    private readonly ILibraryManager? _libraryManager;
    private readonly object _cacheLock = new();
    private IReadOnlyList<ProtectionRule>? _cachedRules;

    /// <summary>Initializes a new instance of the <see cref="ProtectionService"/> class.</summary>
    /// <param name="store">Persistence for the rules.</param>
    /// <param name="libraryManager">Used to resolve <c>library</c>-kind rules to their virtual-folder paths.
    /// May be null in tests; when null, library-kind rules never match (folder-kind rules still work).</param>
    public ProtectionService(ProtectionStore store, ILibraryManager? libraryManager)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _libraryManager = libraryManager;
    }

    /// <summary>Snapshot of the current ruleset.</summary>
    /// <returns>All protection rules.</returns>
    public IReadOnlyList<ProtectionRule> GetRules()
    {
        lock (_cacheLock)
        {
            _cachedRules ??= _store.GetAll();
            return _cachedRules;
        }
    }

    /// <summary>Forces the next lookup to re-read the DB. Call from the CRUD endpoints after Add / Remove.</summary>
    public void Invalidate()
    {
        lock (_cacheLock)
        {
            _cachedRules = null;
        }
    }

    /// <summary>
    /// True when a scanner should NOT emit an issue for <paramref name="path"/> under
    /// <paramref name="type"/>. Matched by <see cref="ProtectionRule.ScannersMask"/>.
    /// </summary>
    /// <param name="path">Absolute path being scanned.</param>
    /// <param name="type">Issue type the scanner is about to emit.</param>
    /// <returns>Whether the emit is blocked.</returns>
    public bool IsScannerBlocked(string path, IssueType type) => IsBlocked(path, type, scanners: true);

    /// <summary>
    /// True when a fixer should NOT modify <paramref name="path"/> under <paramref name="type"/>.
    /// Matched by <see cref="ProtectionRule.FixersMask"/>.
    /// </summary>
    /// <param name="path">Absolute path being fixed.</param>
    /// <param name="type">Issue type the fixer is about to run.</param>
    /// <returns>Whether the fix is blocked.</returns>
    public bool IsFixerBlocked(string path, IssueType type) => IsBlocked(path, type, scanners: false);

    internal Func<string, IssueType, bool> CreateFixerCheck()
    {
        var rules = GetRules();
        var libraries = BuildLibraryPathCache();
        return (path, type) => MountedRecycleRoots.Contains(path)
            || IsBlockedInternal(path, type, scanners: false, rules, libraries);
    }

    /// <summary>
    /// External audit P1 #2: a tree-destructive operation (recycle-whole-folder, cover-strip
    /// sweep) must refuse when ANY descendant under <paramref name="parentPath"/> is covered by
    /// a protection rule for <paramref name="type"/>. The ordinary <see cref="IsFixerBlocked"/>
    /// check only matches when the rule's root equals the parent or is ONE OF its ancestors, so
    /// a rule for <c>/library/show/season3</c> never blocked a delete of <c>/library/show</c>
    /// even though the recycle call would eat Season 3 along with everything else.
    /// </summary>
    /// <param name="parentPath">Absolute path of the parent being destroyed.</param>
    /// <param name="type">Issue type to check (ManualDelete for Files-tab deletes; the specific
    /// fixer type for sweep operations).</param>
    /// <returns>True when at least one rule's protected root sits under the parent.</returns>
    /// <param name="scanners">Check scanner masks instead of fixer masks.</param>
    public bool IsAnyDescendantProtected(string parentPath, IssueType type, bool scanners = false)
    {
        if (string.IsNullOrEmpty(parentPath))
        {
            return false;
        }

        var rules = GetRules();
        if (rules.Count == 0)
        {
            return false;
        }

        var normalisedParent = TryCanonicalise(parentPath);
        if (normalisedParent is null)
        {
            return false;
        }

        var libraryPaths = BuildLibraryPathCache();
        foreach (var rule in rules)
        {
            if (!MaskCoversType(scanners ? rule.ScannersMask : rule.FixersMask, type))
            {
                continue;
            }

            if (string.Equals(rule.Kind, KindFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (RuleRootIsUnder(rule.Target, normalisedParent))
                {
                    return true;
                }
            }
            else if (string.Equals(rule.Kind, KindLibrary, StringComparison.OrdinalIgnoreCase)
                && libraryPaths.TryGetValue(rule.Target, out var folders))
            {
                foreach (var folder in folders)
                {
                    if (RuleRootIsUnder(folder, normalisedParent))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // Returns true when the rule's root sits inside parentPath (i.e. deleting parentPath would
    // also destroy whatever that rule is protecting). Mirror of PathIsUnder but direction-flipped.
    private static bool RuleRootIsUnder(string ruleRoot, string canonicalParent)
    {
        if (string.IsNullOrEmpty(ruleRoot))
        {
            return false;
        }

        var normalisedRoot = TryCanonicalise(ruleRoot);
        if (normalisedRoot is null)
        {
            return false;
        }

        try
        {
            return LibraryGuard.IsUnder(normalisedRoot, canonicalParent);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Filters a scan-result batch down to the issues that are not blocked. Convenience for scan callers.</summary>
    /// <param name="issues">The batch a scanner is about to hand to <c>IssueStore.ReplaceDetectedIssues</c>.</param>
    /// <param name="type">The type shared by all rows in the batch.</param>
    /// <returns>Only the issues that survive protection.</returns>
    public IReadOnlyList<Issue> FilterEmittable(IReadOnlyList<Issue> issues, IssueType type)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (issues.Count == 0)
        {
            return issues;
        }

        var rules = GetRules();
        if (rules.Count == 0)
        {
            return issues.Any(issue => MountedRecycleRoots.Contains(issue.Path))
                ? issues.Where(issue => !MountedRecycleRoots.Contains(issue.Path)).ToArray() : issues;
        }

        var libraryPathMap = BuildLibraryPathCache();
        var survivors = new List<Issue>(issues.Count);
        foreach (var issue in issues)
        {
            if (!IsBlockedInternal(issue.Path, type, scanners: true, rules, libraryPathMap))
            {
                survivors.Add(issue);
            }
        }

        return survivors;
    }

    /// <summary>Excludes protected media before a scanner reads or probes it.</summary>
    /// <param name="items">Items selected for this scan.</param>
    /// <param name="type">The scanner's issue type.</param>
    /// <returns>Items whose media paths are all scannable.</returns>
    public IReadOnlyList<BaseItem> FilterScannable(IReadOnlyList<BaseItem> items, IssueType type)
    {
        var rules = GetRules();
        if (rules.Count == 0)
        {
            return items;
        }

        var libraryPathMap = BuildLibraryPathCache();
        return items.Where(item => !MediaFileHelper.GetFilePaths(item).Any(path => IsBlockedInternal(path, type, scanners: true, rules, libraryPathMap))).ToList();
    }

    private bool IsBlocked(string path, IssueType type, bool scanners)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (MountedRecycleRoots.Contains(path))
        {
            return true;
        }

        var rules = GetRules();
        if (rules.Count == 0)
        {
            return false;
        }

        return IsBlockedInternal(path, type, scanners, rules, BuildLibraryPathCache());
    }

    private static bool IsBlockedInternal(
        string path,
        IssueType type,
        bool scanners,
        IReadOnlyList<ProtectionRule> rules,
        Dictionary<string, IReadOnlyList<string>> libraryPathCache)
    {
        if (MountedRecycleRoots.Contains(path))
        {
            return true;
        }

        foreach (var rule in rules)
        {
            var mask = scanners ? rule.ScannersMask : rule.FixersMask;
            if (!MaskCoversType(mask, type))
            {
                continue;
            }

            if (string.Equals(rule.Kind, KindFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (PathIsUnder(path, rule.Target))
                {
                    return true;
                }
            }
            else if (string.Equals(rule.Kind, KindLibrary, StringComparison.OrdinalIgnoreCase))
            {
                if (libraryPathCache.TryGetValue(rule.Target, out var folders))
                {
                    foreach (var folder in folders)
                    {
                        if (PathIsUnder(path, folder))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static bool MaskCoversType(string? mask, IssueType type)
    {
        if (string.IsNullOrWhiteSpace(mask))
        {
            return false;
        }

        if (mask.Trim() == MaskAll)
        {
            return true;
        }

        var wanted = type.ToString();
        foreach (var raw in mask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(raw, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PathIsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Canonicalize path so mixed separators, dot segments, and case differences on Windows
        // all collapse to a comparable form. External audit P1 #3: previously this method appended
        // a separator to the trimmed root to boundary-check StartsWith — on a drive root like
        // `C:\`, Path.TrimEndingDirectorySeparator keeps the trailing slash (it's part of what
        // makes the path rooted), so prefix became `C:\\` and ordinary descendants never matched.
        // LibraryGuard.IsUnder already handles that drive-root edge AND the "D:\Movies2 must not
        // match D:\Movies" boundary case — reuse it so protection and library containment never
        // drift apart.
        var normalisedPath = TryCanonicalise(path);
        if (normalisedPath is null)
        {
            return false;
        }

        try
        {
            return LibraryGuard.IsUnder(normalisedPath, root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? TryCanonicalise(string value)
    {
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private Dictionary<string, IReadOnlyList<string>> BuildLibraryPathCache()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (_libraryManager is null || !GetRules().Any(rule => string.Equals(rule.Kind, KindLibrary, StringComparison.OrdinalIgnoreCase)))
        {
            return map;
        }

        var idLookup = VirtualFolderIdentity.BuildIdLookup(_libraryManager);
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            var id = VirtualFolderIdentity.GetId(folder, idLookup);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var locations = (folder.Locations ?? Array.Empty<string>())
                .Where(l => !string.IsNullOrEmpty(l))
                .Select(Path.TrimEndingDirectorySeparator)
                .ToList();
            map[id] = locations;
        }

        return map;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Walks each configured library folder for <c>.nfo</c> metadata sidecars and validates them —
/// zero-byte files, malformed XML, and files missing a recognised root element (movie / tvshow /
/// episode / musicvideo / album / artist) are flagged for removal. Follows the same "delete corrupt,
/// let Jellyfin's next scan try again" strategy as <see cref="ArtworkScanner"/>.
/// </summary>
public sealed class NfoScanner : IScanner
{
    // Roots Jellyfin's NFO providers understand. Anything else means we don't know what the file is
    // trying to be — flag it as unrecognised rather than silently keeping useless bytes on disk.
    // "season" and "person" are Jellyfin's own writer conventions (season.nfo per-Season, person.nfo
    // per-Actor); omitting them caused the scanner to flag valid Jellyfin metadata as corrupt.
    internal static readonly HashSet<string> KnownRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "movie", "tvshow", "season", "episodedetails", "episode", "musicvideo", "album", "artist", "boxset", "person"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<NfoScanner> _logger;

    /// <summary>Initializes a new instance of the <see cref="NfoScanner"/> class.</summary>
    /// <param name="libraryManager">Used to enumerate configured library locations.</param>
    /// <param name="logger">The logger.</param>
    public NfoScanner(ILibraryManager libraryManager, ILogger<NfoScanner> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.CorruptNfo;

    /// <inheritdoc />
    public bool AlwaysUnscoped => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var issues = new List<Issue>();
        // Only walk libraries the user opted into via Settings → Libraries. NfoScanner is
        // AlwaysUnscoped (i.e. skips the DB scoped-delete branch), which historically also meant
        // "walk every folder on disk" — the 2026-08-23 field-report bug class. See
        // VirtualFolderIdentity.GetEnabledFolders. A corrupt NFO in a library the user chose NOT
        // to touch should not become an issue MediaDash can be asked to delete.
        var config = Plugin.Instance!.Configuration;
        var locations = VirtualFolderIdentity.GetEnabledFolders(_libraryManager, config.EnabledLibraries)
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < locations.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = locations[i];
            IEnumerable<string> nfoFiles;
            try
            {
                // AttributesToSkip = ReparsePoint defends against symlink cycles inside a library root
                // (`/movies/all -> /movies`) that would otherwise recurse until OOM or process kill.
                // IgnoreInaccessible so one unreadable subfolder doesn't abort the whole scan mid-walk.
                var enumOpts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = true
                };
                nfoFiles = Directory.EnumerateFiles(root, "*.nfo", enumOpts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "NfoScanner: could not walk {Root}", root);
                continue;
            }

            foreach (var nfo in nfoFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reason = EvaluateFile(nfo);
                if (reason is null)
                {
                    continue;
                }

                long size;
                try
                {
                    size = new FileInfo(nfo).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    size = 0;
                }

                issues.Add(new Issue
                {
                    Type = Type,
                    Path = nfo,
                    Status = IssueStatus.Detected,
                    DetectedAtUtc = DateTime.UtcNow,
                    SizeSavings = size,
                    DetailsJson = JsonSerializer.Serialize(new { reason }),
                    SuggestedFix = string.Format(
                        CultureInfo.InvariantCulture,
                        "Delete corrupt NFO \"{0}\" ({1}) — Jellyfin re-reads metadata on the next scan.",
                        Path.GetFileName(nfo),
                        reason)
                });
            }

            progress.Report((i + 1) * 100.0 / Math.Max(locations.Count, 1));
        }

        progress.Report(100);
        _logger.LogInformation("NfoScanner: {Count} corrupt NFO file(s) across {Roots} library root(s).", issues.Count, locations.Count);
        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    /// <summary>
    /// Validates one <c>.nfo</c> file. Returns null on a clean read, or a short human-readable reason
    /// describing why the file is corrupt. Exposed internal for direct unit-testing without ILibraryManager.
    /// </summary>
    /// <param name="path">Full path to the NFO.</param>
    /// <returns>Null when the file is fine, else a reason string.</returns>
    internal static string? EvaluateFile(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return "unreadable: " + ex.Message;
        }

        if (!info.Exists)
        {
            return "missing";
        }

        if (info.Length == 0)
        {
            return "empty file";
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreWhitespace = true,
                IgnoreComments = true,
                CheckCharacters = false,
                MaxCharactersInDocument = 4_000_000L,
                // Kodi/Jellyfin NFO convention appends a bare provider URL after the closing root
                // tag ("</movie>\nhttps://…"). Document mode throws XmlException on that trailing
                // text; Fragment mode tolerates it, matching what Jellyfin's own reader accepts.
                ConformanceLevel = ConformanceLevel.Fragment
            };

            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, settings);

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (!KnownRoots.Contains(reader.LocalName))
                {
                    return "root element <" + reader.LocalName + "> is not a Jellyfin NFO type";
                }

                // Root is recognised. Validate its subtree is well-formed (Skip walks to the matching
                // end tag), then stop — anything after the root is trailing convention content that
                // Jellyfin also ignores.
                reader.Skip();
                return null;
            }

            return "no root element";
        }
        catch (XmlException ex)
        {
            return "malformed XML: " + Truncate(ex.Message);
        }
        catch (IOException ex)
        {
            return "read failed: " + Truncate(ex.Message);
        }
    }

    private static string Truncate(string s)
    {
        return s.Length > 120 ? s[..120] + "…" : s;
    }
}

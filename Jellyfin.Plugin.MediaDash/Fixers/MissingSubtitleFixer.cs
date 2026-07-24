using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Downloads a subtitle track for a video that has none in the wanted languages, via Jellyfin's configured
/// subtitle providers (OpenSubtitles etc.). Depends on the admin having a provider set up in Jellyfin;
/// without one, every fix reports "no providers configured" and nothing is downloaded.
/// </summary>
public sealed class MissingSubtitleFixer : IFixer
{
    private readonly ISubtitleManager _subtitleManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MissingSubtitleFixer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MissingSubtitleFixer"/> class.
    /// </summary>
    /// <param name="subtitleManager">Jellyfin's subtitle download service.</param>
    /// <param name="libraryManager">The Jellyfin library manager, used to resolve the video from the issue's item id.</param>
    /// <param name="logger">The logger.</param>
    public MissingSubtitleFixer(ISubtitleManager subtitleManager, ILibraryManager libraryManager, ILogger<MissingSubtitleFixer> logger)
    {
        _subtitleManager = subtitleManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.MissingSubtitles;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        if (_libraryManager.GetItemById(issue.ItemId) is not Video video)
        {
            return FixResult.Fail("The library item is no longer available — re-scan to refresh the list.");
        }

        IReadOnlyList<string> wanted = ReadMissingLanguages(issue) ?? (IReadOnlyList<string>)config.AllowedSubtitleLanguages;
        if (wanted.Count == 0)
        {
            return FixResult.Fail("No wanted subtitle languages configured. Set some in Settings → Languages, then re-scan.");
        }

        // Skip the "already added since scan" pre-check — a re-scan clears the issue in that case, and
        // refreshing metadata inline here would need a Task path that's more code than the rare race avoids.

        var attempts = new List<string>();
        foreach (var lang in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = LanguageHelper.Normalize(lang);
            RemoteSubtitleInfo[] hits;
            try
            {
                hits = await _subtitleManager.SearchSubtitles(video, normalized, isPerfectMatch: null, isAutomated: true, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                attempts.Add($"{normalized}: {ex.Message}");
                continue;
            }
            catch (HttpRequestException ex)
            {
                attempts.Add($"{normalized}: provider unreachable ({ex.Message})");
                continue;
            }

            if (hits is null || hits.Length == 0)
            {
                attempts.Add($"{normalized}: no matches from any provider");
                continue;
            }

            var pick = hits[0];
            var actionText = string.Format(
                CultureInfo.InvariantCulture,
                "downloaded {0} subtitle for {1} from {2}",
                normalized,
                video.Name ?? System.IO.Path.GetFileNameWithoutExtension(issue.Path),
                pick.ProviderName ?? "provider");

            if (config.DryRun)
            {
                return FixResult.DryRun(actionText, 0);
            }

            try
            {
                await _subtitleManager.DownloadSubtitles(video, pick.Id, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Subtitle download: {Action}", actionText);
                return new FixResult { Success = true, Message = actionText, BytesFreed = 0 };
            }
            catch (InvalidOperationException ex)
            {
                attempts.Add($"{normalized}: download failed ({ex.Message})");
            }
            catch (HttpRequestException ex)
            {
                attempts.Add($"{normalized}: download failed ({ex.Message})");
            }
        }

        var reason = attempts.Count == 0
            ? "No subtitle providers are configured in Jellyfin's Dashboard → Metadata."
            : string.Join("; ", attempts);
        return FixResult.Fail(reason);
    }

    private static List<string>? ReadMissingLanguages(Issue issue)
    {
        try
        {
            using var doc = JsonDocument.Parse(issue.DetailsJson);
            if (doc.RootElement.TryGetProperty("missingLanguages", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                return arr.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .ToList();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Analytics;

/// <summary>
/// Pushes month-to-date aggregated stats to the MediaDash community analytics board (Supabase).
/// Opt-in only. Fully swallows failures so a network hiccup never blocks a fix run.
///
/// What's sent: an anonymous install UUID, plugin + Jellyfin version strings, the current month,
/// per-type success counts (duplicate, playability, quality, subtitle, audio, misplaced,
/// missing-subs, stale, corrupt-artwork, suspicious/malware) and total bytes freed. No paths, no
/// filenames, no usernames, no IP-derived data. The backend clamps each field monotonically so
/// stale numbers can never reduce the totals.
/// </summary>
public sealed class AnalyticsReporter
{
    // The Supabase project URL + publishable anon key are not secrets — the anon key is designed to
    // ship in clients. RLS + SECURITY DEFINER on the report_stats RPC restrict what it can actually
    // do. Documented at docs/PRIVACY.md.
    private const string BaseUrl = "https://mcgpyjtcqyrffydpfdrd.supabase.co";
    private const string AnonKey = "sb_publishable_dstMpg4VGn2tSS_DEhsdZg_n9jwnFXk";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly MediaDashDb _db;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<AnalyticsReporter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalyticsReporter"/> class.
    /// </summary>
    /// <param name="db">The plugin DB, used to aggregate the current month's history.</param>
    /// <param name="appHost">Application host, used for the Jellyfin version string.</param>
    /// <param name="logger">The logger.</param>
    public AnalyticsReporter(MediaDashDb db, IServerApplicationHost appHost, ILogger<AnalyticsReporter> logger)
    {
        _db = db;
        _appHost = appHost;
        _logger = logger;
    }

    /// <summary>
    /// Aggregates the current month's history and POSTs it to the analytics RPC. Returns immediately
    /// if the user hasn't opted in. All exceptions are caught and logged at debug level only —
    /// analytics failing is never surfaced to users.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A completed task once the report is sent (or skipped).</returns>
    public async Task ReportMonthToDateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.AnalyticsEnabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(config.AnalyticsInstallId) || !Guid.TryParse(config.AnalyticsInstallId, out var installId))
            {
                return;
            }

            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEnd = monthStart.AddMonths(1);
            var aggregate = _db.GetMonthAggregate(monthStart, monthEnd);

            var pluginVersion = Plugin.Instance?.Version?.ToString(3) ?? string.Empty;
            var jellyfinVersion = _appHost.ApplicationVersionString ?? string.Empty;

            var payload = new Dictionary<string, object?>
            {
                ["p_install_id"] = installId.ToString(),
                ["p_plugin_version"] = pluginVersion,
                ["p_jellyfin_version"] = jellyfinVersion,
                ["p_month"] = monthStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["p_duplicate"] = Count(aggregate, IssueType.Duplicate),
                ["p_playability"] = Count(aggregate, IssueType.Playability),
                ["p_quality"] = Count(aggregate, IssueType.Quality),
                ["p_subtitle"] = Count(aggregate, IssueType.SubtitleLanguage),
                ["p_audio"] = Count(aggregate, IssueType.AudioLanguage),
                ["p_misplaced"] = Count(aggregate, IssueType.Misplaced),
                ["p_missing_subs"] = Count(aggregate, IssueType.MissingSubtitles),
                ["p_stale"] = Count(aggregate, IssueType.Stale),
                ["p_corrupt_artwork"] = Count(aggregate, IssueType.CorruptArtwork),
                ["p_suspicious"] = Count(aggregate, IssueType.MalwareRisk),
                ["p_bytes_freed"] = aggregate.BytesFreed
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/rest/v1/rpc/report_stats");
            request.Headers.Add("apikey", AnonKey);
            request.Headers.Add("Authorization", "Bearer " + AnonKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Analytics report returned {Status}", (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path — silent.
        }
        catch (Exception ex)
        {
            // Debug only — a public plugin phoning home should never nag the user when the network is flaky.
            _logger.LogDebug(ex, "Analytics report failed");
        }
    }

    private static int Count(MonthAggregate aggregate, IssueType type)
        => aggregate.ByType.TryGetValue(type, out var n) ? n : 0;
}

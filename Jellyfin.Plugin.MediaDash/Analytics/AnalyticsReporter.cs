using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
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
/// What's sent: a **month-rotated** install identifier (see <see cref="ComputeMonthlyInstallId"/>),
/// plugin + Jellyfin version strings, the current month, per-type success counts, and total bytes
/// freed. No paths, no filenames, no usernames, no IP-derived data.
///
/// Privacy: the install ID is derived as
/// <c>SHA256(plugin-scoped-salt || jellyfin-system-id || year-month)</c>, formatted as a UUIDv5-shaped
/// string. That means (a) the same install produces the same ID for every report inside a month so
/// the backend can still deduplicate to one row per install per month, and (b) the ID rotates on
/// the first of every month, so nothing links reports across months back to a single install. The
/// input to the hash is the Jellyfin SystemId — not stored anywhere else, not derivable from the
/// payload, and never sent — which makes the resulting ID one-way anonymous under APP + GDPR
/// (previously the ID was a persistent UUID minted on first opt-in, which is stronger linkability
/// than APP tolerates for "aggregate community stats"). The backend clamps each field monotonically
/// so stale numbers can never reduce the totals.
/// </summary>
public sealed class AnalyticsReporter
{
    // The Supabase project URL + publishable anon key are not secrets — the anon key is designed to
    // ship in clients. RLS + SECURITY DEFINER on the report_stats RPC restrict what it can actually
    // do. Documented at docs/PRIVACY.md.
    private const string BaseUrl = "https://mcgpyjtcqyrffydpfdrd.supabase.co";
    private const string AnonKey = "sb_publishable_dstMpg4VGn2tSS_DEhsdZg_n9jwnFXk";

    // Plugin-scoped salt. Bump the "v1" suffix if the input construction ever changes so old backend
    // rows can't accidentally be correlated with new ones. Not a secret — it's about domain
    // separation from any other plugin that might hash the same SystemId, not about hiding the input.
    private const string TbrhSalt = "mediadash-analytics-tbrh-v1";

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

            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEnd = monthStart.AddMonths(1);
            var installId = ComputeMonthlyInstallId(_appHost.SystemId, monthStart);
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
                ["p_ungrouped"] = Count(aggregate, IssueType.Ungrouped),
                ["p_large_trickplay"] = Count(aggregate, IssueType.LargeTrickplay),
                ["p_subtitle_fonts"] = Count(aggregate, IssueType.SubtitleFonts),
                ["p_orphaned_debris"] = Count(aggregate, IssueType.OrphanedDebris),
                ["p_corrupt_nfo"] = Count(aggregate, IssueType.CorruptNfo),
                ["p_heavy_transcode"] = Count(aggregate, IssueType.HeavyTranscode),
                ["p_failed_transcode"] = Count(aggregate, IssueType.FailedTranscode),
                ["p_embedded_cover_art"] = Count(aggregate, IssueType.EmbeddedCoverArt),
                ["p_bytes_freed"] = aggregate.BytesFreed
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/rest/v1/rpc/report_stats");
            request.Headers.Add("apikey", AnonKey);
            request.Headers.Add("Authorization", "Bearer " + AnonKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Warn once so backend schema drift is visible in the logs — Debug alone hides silent
                // analytics breakage indefinitely. Still doesn't nag the user; server logs only.
                _logger.LogWarning("Analytics report returned {Status} — server schema may have drifted from client payload.", (int)response.StatusCode);
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

    /// <summary>
    /// Derives a month-rotated install ID from the Jellyfin SystemId and the month-of-report.
    /// Same install → same ID inside a calendar month (backend dedup works); new month → fresh ID,
    /// not linkable to the previous month's. Internal for direct unit testing.
    /// </summary>
    /// <param name="systemId">The Jellyfin server's SystemId, or null/empty when unavailable.</param>
    /// <param name="monthStart">UTC first-of-month timestamp — provides the temporal rotation input.</param>
    /// <returns>A UUIDv5-shaped Guid derived from the salted hash.</returns>
    internal static Guid ComputeMonthlyInstallId(string? systemId, DateTime monthStart)
    {
        // Empty SystemId path: use a deterministic sentinel rather than a random Guid so a
        // (rare) Jellyfin build without SystemId still gets stable within-month dedup and
        // predictable rotation. All such installs will share this ID — acceptable for
        // aggregate stats and clearly non-personal.
        var input = TbrhSalt + "|" + (systemId ?? "no-system-id") + "|" + monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        // RFC 4122 § 4.3: version 5 (name-based, SHA-1 in the spec — we use SHA-256 and take the
        // first 16 bytes, a common defensible extension). Set the version and variant bits so
        // downstream tools that validate the UUID shape don't reject it. `new Guid(byte[])` uses
        // mixed-endian: the time-low/mid/hi_and_version fields (bytes 0-7) are read little-endian,
        // so the version nibble in the .NET byte layout ends up in the HIGH nibble of byte 7, not
        // byte 6 like in the big-endian RFC layout. Bytes 8-15 are read as-is, so the variant
        // stays in byte 8. Getting these indexes wrong doesn't corrupt determinism — collision
        // risk is unchanged — it just means the resulting Guid wouldn't advertise "v5" cleanly.
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }
}

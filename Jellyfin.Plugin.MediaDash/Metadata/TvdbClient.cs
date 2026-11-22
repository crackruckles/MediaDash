using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Metadata;

/// <summary>
/// Minimal TVDb v4 client: login, title search and IMDb remote-id lookup. Every request passes
/// through one gate that spaces request starts at least <see cref="DefaultMinInterval"/> apart,
/// so the plugin stays under TVDb's 20 requests/second limit however many scans run.
/// </summary>
public sealed class TvdbClient : IDisposable
{
    // MediaDash's free TVDb project key. It is meant to be public: TVDb rate-limits it per IP
    // address, so every server using it has its own allowance.
    internal const string ProjectApiKey = "39fbe6b9-d3ae-4566-95bf-6e3fac2f1231";

    // 60 ms between request starts = at most 16.7 requests per second.
    internal static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMilliseconds(60);

    private static readonly Uri BaseUri = new("https://api4.thetvdb.com/v4/");
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    private readonly Func<HttpClient> _clientFactory;
    private readonly ILogger _logger;
    private readonly TimeSpan _minInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _nextSlot = TimeSpan.Zero;
    private string? _token;
    private string? _tokenKey;
    private DateTime _tokenExpiresUtc;
    private int _requestsSent;

    /// <summary>Initializes a new instance of the <see cref="TvdbClient"/> class.</summary>
    /// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public TvdbClient(IHttpClientFactory httpClientFactory, ILogger<TvdbClient> logger)
        : this(() => httpClientFactory.CreateClient(nameof(TvdbClient)), logger, DefaultMinInterval)
    {
    }

    internal TvdbClient(Func<HttpClient> clientFactory, ILogger logger, TimeSpan minInterval)
    {
        _clientFactory = clientFactory;
        _logger = logger;
        _minInterval = minInterval;
    }

    internal int RequestsSent => Volatile.Read(ref _requestsSent);

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        _loginGate.Dispose();
    }

    /// <summary>Searches TVDb for titles of one type.</summary>
    /// <param name="apiKey">TVDb API key.</param>
    /// <param name="query">Title to search for.</param>
    /// <param name="type"><c>series</c> or <c>movie</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search hits, best first as TVDb ranks them.</returns>
    public async Task<IReadOnlyList<TvdbSearchHit>> SearchAsync(string apiKey, string query, string type, CancellationToken cancellationToken)
    {
        var relative = "search?query=" + Uri.EscapeDataString(query) + "&type=" + Uri.EscapeDataString(type) + "&limit=20";
        using var doc = await GetAsync(apiKey, relative, cancellationToken).ConfigureAwait(false);
        var hits = new List<TvdbSearchHit>();
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return hits;
        }

        foreach (var hit in data.EnumerateArray())
        {
            if (!TryInt(hit, "tvdb_id", out var id) || !hit.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var aliases = new List<string>();
            if (hit.TryGetProperty("aliases", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var alias in list.EnumerateArray())
                {
                    if (alias.ValueKind == JsonValueKind.String && alias.GetString() is { Length: > 0 } text)
                    {
                        aliases.Add(text);
                    }
                }
            }

            hits.Add(new TvdbSearchHit(id, name.GetString()!, TryInt(hit, "year", out var year) ? year : null, aliases));
        }

        return hits;
    }

    /// <summary>Resolves an IMDb id to a TVDb series or movie id.</summary>
    /// <param name="apiKey">TVDb API key.</param>
    /// <param name="remoteId">IMDb id such as <c>tt1196946</c>.</param>
    /// <param name="type"><c>series</c> or <c>movie</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The TVDb id, or null when TVDb has no such title.</returns>
    public async Task<int?> RemoteIdAsync(string apiKey, string remoteId, string type, CancellationToken cancellationToken)
    {
        using var doc = await GetAsync(apiKey, "search/remoteid/" + Uri.EscapeDataString(remoteId), cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(type, out var title)
                && title.ValueKind == JsonValueKind.Object && TryInt(title, "id", out var id))
            {
                return id;
            }
        }

        return null;
    }

    private static bool TryInt(JsonElement element, string property, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var raw))
        {
            return false;
        }

        return raw.ValueKind switch
        {
            JsonValueKind.Number => raw.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(raw.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private async Task<JsonDocument?> GetAsync(string apiKey, string relative, CancellationToken cancellationToken)
    {
        var relogged = false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var token = await EnsureTokenAsync(apiKey, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !relogged)
            {
                relogged = true;
                _token = null;
                continue;
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        throw new HttpRequestException("TVDb did not answer after repeated retries.");
    }

    private async Task<string> EnsureTokenAsync(string apiKey, CancellationToken cancellationToken)
    {
        await _loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && _tokenKey == apiKey && DateTime.UtcNow < _tokenExpiresUtc)
            {
                return _token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "login"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { apikey = apiKey }), Encoding.UTF8, "application/json")
            };
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new TvdbAuthenticationException("TVDb rejected the API key. Check the TVDb API key in MediaDash settings.");
            }

            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var token = doc.RootElement.GetProperty("data").GetProperty("token").GetString();
                if (string.IsNullOrEmpty(token))
                {
                    throw new TvdbAuthenticationException("TVDb login returned no token.");
                }

                _token = token;
                _tokenKey = apiKey;
                _tokenExpiresUtc = DateTime.UtcNow + TokenLifetime;
                _logger.LogInformation("TVDb: signed in.");
                return token;
            }
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _nextSlot - _clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _nextSlot = _clock.Elapsed + _minInterval;
            Interlocked.Increment(ref _requestsSent);
        }
        finally
        {
            _gate.Release();
        }

        using var client = _clientFactory();
        client.Timeout = TimeSpan.FromSeconds(30);
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

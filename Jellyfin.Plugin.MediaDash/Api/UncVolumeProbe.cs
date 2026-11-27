using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Background UNC capacity cache adapted from Icabaad's PR #91. Dashboard requests never wait
/// for network I/O. Measurements are bounded both per share and across all configured shares.
/// </summary>
internal sealed class UncVolumeProbe
{
    private readonly Func<string, Reading> _measure;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _utcNow;
    private readonly int _maxConcurrent;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _active;

    /// <summary>Initializes a new instance of the <see cref="UncVolumeProbe"/> class.</summary>
    /// <param name="measure">Blocking filesystem measurement, run only in the background.</param>
    /// <param name="ttl">Minimum delay between completed measurements, including failures.</param>
    /// <param name="utcNow">Optional test clock.</param>
    /// <param name="maxConcurrent">Maximum background measurements, including hung calls.</param>
    public UncVolumeProbe(Func<string, Reading> measure, TimeSpan ttl, Func<DateTime>? utcNow = null, int maxConcurrent = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        _measure = measure;
        _ttl = ttl;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _maxConcurrent = maxConcurrent;
    }

    internal int ActiveMeasurements
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>Returns cached capacity immediately and starts a refresh when needed.</summary>
    /// <param name="root">Canonical UNC share root.</param>
    /// <param name="reading">Last successful measurement, or default if unavailable.</param>
    /// <param name="stale">Whether the reading has expired or its latest refresh failed.</param>
    /// <returns>Whether a successful measurement is available.</returns>
    public bool TryGet(string root, out Reading reading, out bool stale)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(root, out var entry))
            {
                entry = new Entry();
                _entries[root] = entry;
            }

            var expired = !entry.Attempted || _utcNow() - entry.LastAttemptUtc >= _ttl;
            if (expired && !entry.Pending && _active < _maxConcurrent)
            {
                entry.Pending = true;
                _active++;
                // A stuck SMB call cannot be cancelled safely. Retain its slot until it completes;
                // repeated polls neither wait for it nor create additional workers for that share.
                _ = Task.Run(() => Measure(root, entry));
            }

            reading = entry.LastGood;
            stale = expired || !entry.LastSucceeded;
            return reading.Ok;
        }
    }

    private void Measure(string root, Entry entry)
    {
        Reading reading;
        try
        {
            reading = _measure(root);
        }
        catch (Exception)
        {
            // An unavailable share must not propagate into the dashboard or leave a slot occupied.
            reading = default;
        }

        lock (_gate)
        {
            var valid = reading.Ok && reading.TotalBytes > 0 && reading.FreeBytes >= 0 && reading.FreeBytes <= reading.TotalBytes;
            if (valid)
            {
                entry.LastGood = reading;
            }

            entry.LastSucceeded = valid;
            entry.Attempted = true;
            entry.LastAttemptUtc = _utcNow();
            entry.Pending = false;
            _active--;
        }
    }

    /// <summary>One filesystem capacity measurement.</summary>
    /// <param name="Ok">Whether the query succeeded.</param>
    /// <param name="FreeBytes">Bytes available to the Jellyfin account.</param>
    /// <param name="TotalBytes">Total bytes visible to the Jellyfin account.</param>
    internal readonly record struct Reading(bool Ok, long FreeBytes, long TotalBytes);

    private sealed class Entry
    {
        public Reading LastGood { get; set; }

        public DateTime LastAttemptUtc { get; set; }

        public bool Attempted { get; set; }

        public bool LastSucceeded { get; set; }

        public bool Pending { get; set; }
    }
}

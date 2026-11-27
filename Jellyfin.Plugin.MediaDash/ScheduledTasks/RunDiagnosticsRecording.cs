using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>Retains the latest scan and fix diagnostics without writing to the database on every sample.</summary>
internal sealed class RunDiagnosticsRecording : IDisposable
{
    private static RunDiagnosticsRecording? _activeScan;
    private static RunDiagnosticsRecording? _activeFix;
    private readonly object _sync = new();
    private readonly MediaDashDb _db;
    private readonly ILogger _logger;
    private readonly string _kind;
    private readonly string _stateKey;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly List<JsonElement> _records = [];
    private string _outcome = "Running";
    private string? _error;
    private DateTime? _finished;

    private RunDiagnosticsRecording(MediaDashDb db, ILogger logger, string kind)
    {
        _db = db;
        _logger = logger;
        _kind = kind;
        _stateKey = kind == "fix" ? "LastFixDiagnostics" : "LastScanDiagnostics";
    }

    internal static RunDiagnosticsRecording? Active(string kind)
        => kind == "fix" ? System.Threading.Volatile.Read(ref _activeFix) : System.Threading.Volatile.Read(ref _activeScan);

    internal static RunDiagnosticsRecording? Start(MediaDashDb db, ILogger logger, bool enabled, string kind = "scan")
    {
        if (!enabled)
        {
            return null;
        }

        var recording = new RunDiagnosticsRecording(db, logger, kind);
        if (kind == "fix")
        {
            System.Threading.Volatile.Write(ref _activeFix, recording);
        }
        else
        {
            System.Threading.Volatile.Write(ref _activeScan, recording);
        }

        return recording;
    }

    internal static JsonElement? Read(MediaDashDb db, string kind = "scan")
    {
        var active = Active(kind);
        if (active is not null && ReferenceEquals(active._db, db))
        {
            return active.Snapshot();
        }

        try
        {
            var stored = db.GetState(kind == "fix" ? "LastFixDiagnostics" : "LastScanDiagnostics");
            if (stored is null)
            {
                return null;
            }

            var value = JsonSerializer.Deserialize<JsonElement>(stored);
            if (value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Report the damaged singleton below without failing the dashboard request.
        }

        Api.Diagnostics.Record(kind == "fix" ? "Fix.Diagnostics" : "Scan.Diagnostics", "The saved diagnostics for the last " + kind + " run are damaged and cannot be shown. Run another " + kind + " with diagnostics on to replace them.");
        return null;
    }

    internal void Record(JsonElement record)
    {
        lock (_sync)
        {
            if (_finished is null)
            {
                _records.Add(record.Clone());
            }
        }
    }

    internal void Complete(string outcome, string? error = null)
    {
        lock (_sync)
        {
            _outcome = outcome;
            _error = error;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_finished is not null)
            {
                return;
            }

            _finished = DateTime.UtcNow;
            if (_outcome == "Running")
            {
                _outcome = "Interrupted";
            }
        }

        try
        {
            _db.SetState(_stateKey, Snapshot().GetRawText());
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Could not save the latest {Kind} diagnostics.", _kind);
            Api.Diagnostics.Record(_kind == "fix" ? "Fix.Diagnostics" : "Scan.Diagnostics", "Could not save the diagnostics for the latest " + _kind + " run, so the Diagnostics tab still shows the previous one. Details: " + error.Message);
        }
        finally
        {
            if (_kind == "fix")
            {
                System.Threading.Interlocked.CompareExchange(ref _activeFix, null, this);
            }
            else
            {
                System.Threading.Interlocked.CompareExchange(ref _activeScan, null, this);
            }
        }
    }

    private JsonElement Snapshot()
    {
        (DateTime? FinishedAtUtc, string Outcome, string? Error, JsonElement[] Records) snapshot;
        lock (_sync)
        {
            snapshot = (_finished, _outcome, _error, _records.ToArray());
        }

        // Error-store reads must not hold up run instrumentation.
        var end = snapshot.FinishedAtUtc ?? DateTime.UtcNow;
        return JsonSerializer.SerializeToElement(new
        {
            StartedAtUtc = _started,
            snapshot.FinishedAtUtc,
            snapshot.Outcome,
            snapshot.Error,
            snapshot.Records,
            Errors = Api.Diagnostics.RecentAll().Where(entry => (entry.LastAtUtc >= _started || entry.AtUtc >= _started) && entry.AtUtc <= end).ToArray()
        });
    }
}

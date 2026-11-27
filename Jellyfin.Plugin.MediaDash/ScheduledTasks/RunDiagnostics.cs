using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.MediaDash.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

internal sealed class RunDiagnostics : IDisposable
{
    private static readonly AsyncLocal<RunDiagnostics?> Current = new();
    private readonly object _lock = new();
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly RunDiagnosticsRecording? _capture;
    private readonly string _kind;
    private readonly string _component;
    private readonly Guid _id = Guid.NewGuid();
    private readonly long _started;
    private readonly Dictionary<long, (string Name, long Started)> _active = new();
    private RunDiagnostics? _previous;
    private Timer? _timer;
    private long _nextOperation;
    private long _lastProgress;
    private double _progress;
    private int _filesExamined;
    private bool _disposed;
    private int _sampling;
    private int _samples;
    private Sample? _first;
    private Sample? _latest;
    private double? _cpuPeak;
    private double? _systemCpuPeak;
    private double? _gpuPeak;
    private string? _gpuSource;
    private long? _memoryPeak;
    private long? _systemMemoryPeak;
    private double? _networkReadPeak;
    private double? _networkWritePeak;
    private double? _diskPeak;
    private double? _networkPeak;

    internal RunDiagnostics(ILogger logger, string kind, string component, TimeProvider time, RunDiagnosticsRecording? capture = null)
    {
        _logger = logger;
        _kind = kind;
        _component = component;
        _time = time;
        _capture = capture;
        _started = _lastProgress = time.GetTimestamp();
    }

    internal static RunDiagnostics? Start(ILogger logger, string kind, string component)
    {
        if (Plugin.Instance?.Configuration.DiagnosticsEnabled != true)
        {
            return null;
        }

        var scope = new RunDiagnostics(logger, kind, component, TimeProvider.System, RunDiagnosticsRecording.Active(kind));
        scope._previous = Current.Value;
        Current.Value = scope;
        scope.Log("start");
        scope._timer = new Timer(static state => ((RunDiagnostics)state!).Tick(), scope, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        return scope;
    }

    internal static IDisposable? Track(string operation, bool examinesFile = false)
        => Current.Value?.BeginOperation(operation, examinesFile);

    internal IDisposable BeginOperation(string operation, bool examinesFile = false)
    {
        lock (_lock)
        {
            var id = ++_nextOperation;
            _active.Add(id, (operation, _time.GetTimestamp()));
            return new Operation(this, id, examinesFile);
        }
    }

    internal void Report(double progress)
    {
        lock (_lock)
        {
            if (double.IsFinite(progress) && progress > _progress)
            {
                _progress = progress;
                _lastProgress = _time.GetTimestamp();
            }
        }
    }

    internal IProgress<double> Observe(IProgress<double> inner) => new ObservedProgress(this, inner);

    internal (string? Operation, int ActiveOperations, double SecondsSinceProgress, double OperationSeconds) ReadState()
    {
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            var oldest = _active.Count == 0 ? default : _active.MinBy(pair => pair.Key).Value;
            return (oldest.Name, _active.Count, _time.GetElapsedTime(_lastProgress, now).TotalSeconds,
                oldest.Name is null ? 0 : _time.GetElapsedTime(oldest.Started, now).TotalSeconds);
        }
    }

    internal void Finish(string outcome, int? candidateItems, int? findings, int? fixTargets, int? resolvedIssues)
        => Log("finish", outcome, candidateItems, findings, fixTargets, resolvedIssues);

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        _timer?.Dispose();
        if (ReferenceEquals(Current.Value, this))
        {
            Current.Value = _previous;
        }
    }

    private static double? Peak(double? previous, double? value)
        => previous is null ? value : value is null ? previous : Math.Max(previous.Value, value.Value);

    private static long? Peak(long? previous, long? value)
        => previous is null ? value : value is null ? previous : Math.Max(previous.Value, value.Value);

    private static (long Read, long Write)? ReadNetwork()
    {
        try
        {
            var counters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback && nic.OperationalStatus == OperationalStatus.Up)
                .Select(nic => nic.GetIPStatistics()).ToArray();
            return counters.Length == 0 ? null : (counters.Sum(item => item.BytesReceived), counters.Sum(item => item.BytesSent));
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or OverflowException)
        {
            return null;
        }
    }

    private void Tick()
    {
        if (Plugin.Instance?.Configuration.DiagnosticsEnabled != true)
        {
            return;
        }

        if (Interlocked.Exchange(ref _sampling, 1) != 0)
        {
            return;
        }

        try
        {
            // Emit the operation state before querying optional platform counters.
            if (_time.GetElapsedTime(_started) >= TimeSpan.FromSeconds(30))
            {
                Log("heartbeat");
            }

            var sample = new Sample(_time.GetTimestamp(), DiskIo.ReadCounters(), ReadNetwork());
            var system = SystemStats.SampleCore(includeDisk: false);
            RecordSample(sample, system);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("MediaDash run diagnostics sample unavailable: {ErrorType}", ex.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    internal void RecordSample(Sample sample, SystemStats system)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_latest is { } previous)
            {
                var seconds = _time.GetElapsedTime(previous.Timestamp, sample.Timestamp).TotalSeconds;
                var networkRead = DiskIo.BytesPerSecond(previous.Network?.Read, sample.Network?.Read, seconds);
                var networkWrite = DiskIo.BytesPerSecond(previous.Network?.Write, sample.Network?.Write, seconds);
                _networkReadPeak = Peak(_networkReadPeak, networkRead);
                _networkWritePeak = Peak(_networkWritePeak, networkWrite);
                _diskPeak = Peak(_diskPeak, DiskIo.BytesPerSecond(previous.Disk?.Read, sample.Disk?.Read, seconds)
                    + DiskIo.BytesPerSecond(previous.Disk?.Write, sample.Disk?.Write, seconds));
                _networkPeak = Peak(_networkPeak, networkRead + networkWrite);
            }

            _first ??= sample;
            _latest = sample;
            _samples++;
            _cpuPeak = Peak(_cpuPeak, system.CpuPercent);
            _systemCpuPeak = Peak(_systemCpuPeak, system.SystemCpuPercent);
            _gpuPeak = Peak(_gpuPeak, system.GpuPercent);
            _gpuSource ??= system.GpuSource;
            _memoryPeak = Peak(_memoryPeak, (long?)system.RamUsedBytes);
            _systemMemoryPeak = Peak(_systemMemoryPeak, system.SystemRamUsedBytes);
        }
    }

    private void Log(string phase, string? outcome = null, int? candidateItems = null, int? findings = null, int? fixTargets = null, int? resolvedIssues = null)
    {
        JsonElement record;
        lock (_lock)
        {
            if (_disposed || Plugin.Instance?.Configuration.DiagnosticsEnabled == false)
            {
                return;
            }

            var state = ReadState();
            var seconds = _first is { } first && _latest is { } latest
                ? _time.GetElapsedTime(first.Timestamp, latest.Timestamp).TotalSeconds : 0;
            record = JsonSerializer.SerializeToElement(new
            {
                Id = _id,
                Kind = _kind,
                Component = _component,
                Phase = phase,
                Outcome = outcome,
                ElapsedSeconds = _time.GetElapsedTime(_started).TotalSeconds,
                ProgressPercent = _progress,
                state.Operation,
                state.ActiveOperations,
                ActiveOperationNames = _active.Values.Select(operation => operation.Name).Distinct().ToArray(),
                state.OperationSeconds,
                state.SecondsSinceProgress,
                CandidateItems = candidateItems,
                Findings = findings,
                ObservedFileChecks = _filesExamined,
                FixTargets = fixTargets,
                ResolvedIssues = resolvedIssues,
                ResourceSamples = _samples,
                ResourceSampleSeconds = seconds,
                JellyfinCpuPeakPercent = _cpuPeak,
                HostOrContainerCpuPeakPercent = _systemCpuPeak,
                JellyfinWorkingSetPeakBytes = _memoryPeak,
                HostOrContainerMemoryPeakBytes = _systemMemoryPeak,
                HostGpuPeakPercent = _gpuPeak,
                GpuSource = _gpuSource,
                HostDiskSource = DiskIo.Source,
                HostDiskPeakBytesPerSecond = _diskPeak,
                NetworkInterfacePeakBytesPerSecond = _networkPeak,
                HostDiskReadAverageBytesPerSecond = DiskIo.BytesPerSecond(_first?.Disk?.Read, _latest?.Disk?.Read, seconds),
                HostDiskWriteAverageBytesPerSecond = DiskIo.BytesPerSecond(_first?.Disk?.Write, _latest?.Disk?.Write, seconds),
                NetworkInterfaceReceivePeakBytesPerSecond = _networkReadPeak,
                NetworkInterfaceSendPeakBytesPerSecond = _networkWritePeak,
            });
            _capture?.Record(record);
        }

        _logger.LogInformation("MediaDash run diagnostics {DiagnosticRecord}", record.GetRawText());
    }

    internal readonly record struct Sample(long Timestamp, (long Read, long Write)? Disk, (long Read, long Write)? Network);

    private sealed class Operation(RunDiagnostics owner, long id, bool examinesFile) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._lock)
            {
                if (owner._active.Remove(id) && examinesFile)
                {
                    owner._filesExamined++;
                    owner._lastProgress = owner._time.GetTimestamp();
                }
            }
        }
    }

    private sealed class ObservedProgress(RunDiagnostics owner, IProgress<double> inner) : IProgress<double>
    {
        public void Report(double value)
        {
            owner.Report(value);
            inner.Report(value);
        }
    }
}

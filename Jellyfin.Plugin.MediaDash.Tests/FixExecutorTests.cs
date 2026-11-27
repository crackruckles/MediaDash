using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Contract tests for <see cref="FixExecutor"/>. Silent-skip paths (no fixer, status changed
/// mid-run, policy refused) return null; every attempted operation returns a truthful
/// (Result, PrimaryIssueId, ResolvedIssueIds) triple. Exception translation table matches the
/// pre-refactor <c>FixTask</c> catch chain verbatim so history-tab wording stays intact.
/// </summary>
[Collection("MediaDashDb")]
public sealed class FixExecutorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-executor-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;
    private readonly PluginConfiguration _config = new();

    public FixExecutorTests()
    {
        _db = new MediaDashDb(_dbPath);
        // Default PluginConfiguration sets every FixMode to ManualApprove, which the policy
        // evaluator refuses under FixIntent.Scheduled. Flip the ones our tests exercise to
        // Automatic so the recheck passes; tests that specifically want a refusal set the mode
        // back to Off inline.
        _config.PlayabilityFixMode = FixMode.Automatic;
        _config.TranscodeFixMode = FixMode.Automatic;
        _config.AudioFixMode = FixMode.Automatic;
        _config.SubtitleFixMode = FixMode.Automatic;
        // Default ctor sets DryRun = true (safe first-run default); real runs would flip this off
        // via the UI. The executor gates CompleteLegacy status transitions on !config.DryRun, so
        // our tests need it off unless they explicitly exercise the dry-run F-206 branch.
        _config.DryRun = false;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private FixExecutor NewExecutor(params IFixer[] fixers)
    {
        var provider = new PluginConfigurationProvider(() => _config);
        var protection = new Jellyfin.Plugin.MediaDash.Fixers.ProtectionService(_db.Protection, null);
        return new FixExecutor(_db.Issues, fixers, provider, TimeProvider.System, protection, NullLogger<FixExecutor>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ProtectionAddedAfterApproval_SkipsFixerUntilRemoved()
    {
        var path = Path.Combine(Path.GetTempPath(), "protected-approved.mkv");
        var id = SeedQueued(IssueType.Playability, path);
        var plan = SinglePlan(Load(id));
        var called = false;
        var fixer = new StubFixer(IssueType.Playability, _ =>
        {
            called = true;
            return Task.FromResult(new FixResult { Success = true, Message = "ok" });
        });
        var ruleId = _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = path, ScannersMask = "*", FixersMask = "*" });
        Assert.Null(await NewExecutor(fixer).ExecuteAsync(plan, true, NullProgress, CancellationToken.None));
        Assert.False(called);
        Assert.Equal(IssueStatus.Queued, Load(id).Status);
        _db.Protection.Remove(ruleId);
        Assert.NotNull(await NewExecutor(fixer).ExecuteAsync(plan, true, NullProgress, CancellationToken.None));
        Assert.True(called);
    }

    private long SeedQueued(IssueType type, string path)
    {
        _db.ReplaceDetectedIssues(type, new[]
        {
            new Issue { Type = type, Path = path, Status = IssueStatus.Detected, DetectedAtUtc = DateTime.UtcNow },
        });
        var id = _db.GetIssues(type).First(i => i.Path == path).Id;
        _db.TryTransitionIssueStatus(id, new[] { IssueStatus.Detected }, IssueStatus.Queued);
        return id;
    }

    private Issue Load(long id) => _db.GetIssues().Single(i => i.Id == id);

    [Fact]
    public async Task ExecuteAsync_CurrentFindingRequiresRescan_RefusesOlderQueuedSnapshot()
    {
        var id = SeedQueued(IssueType.AudioLanguage, Path.Combine(Path.GetTempPath(), "moved-approved.mkv"));
        var plan = SinglePlan(Load(id));
        using (var connection = _db.ConnectionFactory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE issues SET details='{" + "\"requiresRescan\":true}' WHERE id=@id";
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }

        var called = false;
        var fixer = new StubFixer(IssueType.AudioLanguage, _ =>
        {
            called = true;
            return Task.FromResult(new FixResult { Success = true });
        });
        Assert.Null(await NewExecutor(fixer).ExecuteAsync(plan, true, NullProgress, CancellationToken.None));
        Assert.False(called);
    }

    private static PlannedFix SinglePlan(Issue primary)
        => new(PlannedFixKind.Single, primary, Array.Empty<Issue>());

    [Fact]
    public async Task ReportOnlyFindingAddedAfterApprovalDoesNotInvokeTheFixer()
    {
        var id = SeedQueued(IssueType.Quality, Path.Combine(Path.GetTempPath(), "report-only.m4a"));
        var plan = SinglePlan(Load(id));
        using (var connection = _db.ConnectionFactory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE issues SET details='{\"fixerAvailable\":false}' WHERE id=@id";
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }

        var called = false;
        var fixer = new StubFixer(IssueType.Quality, _ =>
        {
            called = true;
            return Task.FromResult(new FixResult { Success = true });
        });
        Assert.Null(await NewExecutor(fixer).ExecuteAsync(plan, true, NullProgress, CancellationToken.None));
        Assert.False(called);
    }

    [Theory]
    [InlineData(PlannedFixKind.TrackPair)]
    [InlineData(PlannedFixKind.TranscodeWithTracks)]
    internal async Task ReportOnlyCompanionInvalidatesAnOlderCombinedPlan(PlannedFixKind kind)
    {
        var path = Path.Combine(Path.GetTempPath(), "report-only-companion.mkv");
        var primaryId = SeedQueued(IssueType.Quality, path);
        var companionId = SeedQueued(IssueType.SubtitleLanguage, path);
        var plan = new PlannedFix(kind, Load(primaryId), [Load(companionId)]);
        using (var connection = _db.ConnectionFactory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE issues SET details='{\"fixerAvailable\":false}' WHERE id=@id";
            command.Parameters.AddWithValue("@id", companionId);
            command.ExecuteNonQuery();
        }

        var called = false;
        var fixer = new StubFixer(IssueType.Quality, _ =>
        {
            called = true;
            return Task.FromResult(new FixResult { Success = true });
        });
        Assert.Null(await NewExecutor(fixer).ExecuteAsync(plan, true, NullProgress, CancellationToken.None));
        Assert.False(called);
        Assert.Equal(IssueStatus.Queued, Load(companionId).Status);
    }

    // Jellyfin's DI resolves plugin services through Microsoft.Extensions.DependencyInjection,
    // which only sees PUBLIC constructors. An internal-only ctor here makes the plugin load
    // Malfunctioned at runtime (FixTask fails to construct → whole plugin dies → configPage.html
    // never renders). Caught live during 06-D observation sweep 2026-09-23 — both FixExecutor
    // and FixResultRecorder had regressed to internal ctors from 04-B/C.
    [Theory]
    [InlineData(typeof(FixExecutor))]
    [InlineData(typeof(FixResultRecorder))]
    public void DIRegisteredService_HasPublicConstructor(Type type)
    {
        var ctors = type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotEmpty(ctors);
    }

    [Fact]
    public async Task ExecuteAsync_SilentSkipTally_RecordsReasonForEachSkipPath()
    {
        var tally = new Dictionary<string, int>(StringComparer.Ordinal);

        // Path 1: no fixer registered.
        var noFixerId = SeedQueued(IssueType.Playability, Path.Combine(Path.GetTempPath(), "no-fixer.mkv"));
        await NewExecutor().ExecuteAsync(SinglePlan(Load(noFixerId)), withinEnabledLibrary: true, NullProgress, tally, CancellationToken.None);

        // Path 2: policy refuses (fixer type set to Off mid-run).
        var policyId = SeedQueued(IssueType.AudioLanguage, Path.Combine(Path.GetTempPath(), "policy.mkv"));
        _config.AudioFixMode = FixMode.Off;
        await NewExecutor(new StubFixer(IssueType.AudioLanguage, _ => Task.FromResult(FixResult.Fail("should not run"))))
            .ExecuteAsync(SinglePlan(Load(policyId)), withinEnabledLibrary: true, NullProgress, tally, CancellationToken.None);
        _config.AudioFixMode = FixMode.Automatic;

        // Path 3: status changed between snapshot and execution.
        var statusId = SeedQueued(IssueType.Playability, Path.Combine(Path.GetTempPath(), "status.mkv"));
        var snapshot = Load(statusId);
        _db.Issues.TryTransitionIssueStatus(statusId, new[] { IssueStatus.Queued }, IssueStatus.Dismissed);
        await NewExecutor(new StubFixer(IssueType.Playability, _ => Task.FromResult(new FixResult { Success = true })))
            .ExecuteAsync(SinglePlan(snapshot), withinEnabledLibrary: true, NullProgress, tally, CancellationToken.None);

        // Path 4: protection added mid-run.
        var protectedPath = Path.Combine(Path.GetTempPath(), "protected.mkv");
        var protectedId = SeedQueued(IssueType.Playability, protectedPath);
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = protectedPath, ScannersMask = "*", FixersMask = "*" });
        await NewExecutor(new StubFixer(IssueType.Playability, _ => Task.FromResult(new FixResult { Success = true })))
            .ExecuteAsync(SinglePlan(Load(protectedId)), withinEnabledLibrary: true, NullProgress, tally, CancellationToken.None);

        Assert.Equal(4, tally.Values.Sum());
        Assert.True(tally.ContainsKey("no fixer registered for this issue type"));
        Assert.Contains(tally.Keys, k => k.StartsWith("settings changed mid-run", StringComparison.Ordinal));
        Assert.True(tally.ContainsKey("status changed between approval and run"));
        Assert.True(tally.ContainsKey("protected from modification"));
    }

    [Fact]
    public async Task ExecuteAsync_SilentSkipTally_LegacyOverloadStillReturnsNull()
    {
        // The no-tally overload is the one existing production code and 20+ tests use. A regression
        // here (e.g. tally overload accidentally changing return semantics) would silently break
        // every FixExecutorTests.Null assertion. Pins the forwarder's shape.
        var id = SeedQueued(IssueType.Playability, "");
        var executor = NewExecutor();
        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ExecuteAsync_PrimaryStatusChangedMidRun_ReturnsNullSilentSkip()
    {
        var id = SeedQueued(IssueType.Playability, "");
        _db.TryTransitionIssueStatus(id, new[] { IssueStatus.Queued }, IssueStatus.Dismissed);
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => Task.FromResult(FixResult.Fail("should not run"))));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExecuteAsync_PolicyRefusesForOffType_ReturnsNullSilentSkip()
    {
        var id = SeedQueued(IssueType.Playability, "");
        _config.PlayabilityFixMode = FixMode.Off;
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => Task.FromResult(FixResult.Fail("should not run"))));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExecuteAsync_LibraryScopeRefuses_ReturnsNullSilentSkip()
    {
        var id = SeedQueued(IssueType.Playability, "");
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => Task.FromResult(FixResult.Fail("should not run"))));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: false, NullProgress, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExecuteAsync_NoFixerRegistered_ReturnsNullSilentSkip()
    {
        var id = SeedQueued(IssueType.Playability, "");
        var executor = NewExecutor(); // no fixers

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessSingle_ResolvedIdsContainsPrimary()
    {
        var id = SeedQueued(IssueType.Playability, "");
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => Task.FromResult(new FixResult { Success = true, Message = "ok", BytesFreed = 42 })));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Result.Success);
        Assert.Equal(id, result.PrimaryIssueId);
        Assert.Equal(new[] { id }, result.ResolvedIssueIds);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessTranscodeWithTracks_ResolvedIdsContainsPrimaryAndCompanions()
    {
        var transcodeId = SeedQueued(IssueType.Quality, "");
        var audioId = SeedQueued(IssueType.AudioLanguage, "");
        var subId = SeedQueued(IssueType.SubtitleLanguage, "");
        var plan = new PlannedFix(
            PlannedFixKind.TranscodeWithTracks,
            Load(transcodeId),
            new[] { Load(audioId), Load(subId) });
        var executor = NewExecutor(new StubFixer(IssueType.Quality, _ => Task.FromResult(new FixResult { Success = true, Message = "encoded" })));

        var result = await executor.ExecuteAsync(plan, withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(new[] { transcodeId, audioId, subId }, result!.ResolvedIssueIds);
    }

    [Fact]
    public async Task ExecuteAsync_FailureWithoutCompleteLegacy_ResolvedIdsEmpty()
    {
        var id = SeedQueued(IssueType.Playability, "");
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => Task.FromResult(FixResult.Fail(FixReason.ProcessFailed, "ffmpeg crashed"))));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Result.Success);
        Assert.Empty(result.ResolvedIssueIds);
    }

    [Fact]
    public async Task ExecuteAsync_FailureWithCompleteLegacyReason_ResolvedIdsContainsPrimaryOnly()
    {
        var id = SeedQueued(IssueType.Playability, "");
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => Task.FromResult(FixResult.Fail(FixReason.SourceMissing, "file gone"))));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Result.Success);
        Assert.Equal(new[] { id }, result.ResolvedIssueIds);
    }

    [Fact]
    public async Task ExecuteAsync_DryRunFailureWithCompleteLegacy_ResolvedIdsEmpty()
    {
        // F-206: dry-run never advances status even for otherwise-terminal reasons.
        _config.DryRun = true; // override the ctor's !DryRun default
        var id = SeedQueued(IssueType.Playability, "");
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => Task.FromResult(FixResult.Fail(FixReason.SourceMissing, "file gone"))));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!.ResolvedIssueIds);
    }

    [Fact]
    public async Task ExecuteAsync_UnauthorizedAccessException_TranslatesToAccessDenied()
    {
        var id = SeedQueued(IssueType.Playability, "");
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => throw new UnauthorizedAccessException("permission")));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Result.Success);
        Assert.Equal(FixReason.AccessDenied, result.Result.Reason);
        Assert.Contains("permission denied", result.Result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_FileNotFoundException_TranslatesToSourceMissing()
    {
        var id = SeedQueued(IssueType.Playability, "");
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => throw new FileNotFoundException("gone")));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(FixReason.SourceMissing, result!.Result.Reason);
        Assert.Contains("went missing", result.Result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_DirectoryNotFoundException_TranslatesToSourceMissing()
    {
        var id = SeedQueued(IssueType.Playability, "");
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => throw new DirectoryNotFoundException("gone")));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(FixReason.SourceMissing, result!.Result.Reason);
        Assert.Contains("containing folder went missing", result.Result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_DiskFullIoException_TranslatesToInsufficientSpace()
    {
        var id = SeedQueued(IssueType.Playability, "");
        // 0x80070070 = Windows ERROR_DISK_FULL (112)
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => throw new IOException("disk full") { HResult = unchecked((int)0x80070070) }));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(FixReason.InsufficientSpace, result!.Result.Reason);
        Assert.Contains("ran out of space", result.Result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_UnclassifiedIoException_TranslatesToUnknown()
    {
        var id = SeedQueued(IssueType.Playability, "");
        // HResult = 0 → neither sharing nor disk full.
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => throw new IOException("something else")));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(FixReason.Unknown, result!.Result.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_UnexpectedException_TranslatesToUnknown()
    {
        var id = SeedQueued(IssueType.Playability, "");
        var executor = NewExecutor(new StubFixer(IssueType.Playability, _ => throw new InvalidOperationException("boom")));

        var result = await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(FixReason.Unknown, result!.Result.Reason);
        Assert.Contains("unexpected error", result.Result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_OperationCancelledExceptionPropagates()
    {
        var id = SeedQueued(IssueType.Playability, "");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var executor = NewExecutor(new StubFixer(IssueType.Playability, ct => Task.FromCanceled<FixResult>(ct)));

        // Per plan §04-C: OperationCanceledException must never be translated to a corruption
        // error; it propagates so the scheduler distinguishes "user pressed Stop" from every
        // other exit path.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(SinglePlan(Load(id)), withinEnabledLibrary: true, NullProgress, cts.Token));
    }

    private static IProgress<double> NullProgress => new Progress<double>(_ => { });

    /// <summary>
    /// Minimal <see cref="IFixer"/> whose only job is to return a caller-supplied result (or
    /// throw a caller-supplied exception). Avoids Moq/NSubstitute — the test project doesn't
    /// take a dependency on a mocking library.
    /// </summary>
    private sealed class StubFixer : IFixer
    {
        private readonly IssueType _handledType;
        private readonly Func<CancellationToken, Task<FixResult>> _behaviour;

        public StubFixer(IssueType handledType, Func<CancellationToken, Task<FixResult>> behaviour)
        {
            _handledType = handledType;
            _behaviour = behaviour;
        }

        public bool CanFix(IssueType type) => type == _handledType;

        public Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
            => _behaviour(cancellationToken);
    }
}

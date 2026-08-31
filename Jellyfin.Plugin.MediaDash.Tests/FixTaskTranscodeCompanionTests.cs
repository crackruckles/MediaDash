using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Routing invariants for transcode + track (AudioLanguage / SubtitleLanguage) triples on the
/// same file. TranscodeFixer.BuildArgs already filters mapped audio + subtitle streams by the
/// configured language allow-lists, so a queued transcode drops unwanted tracks incidentally.
/// FixTask.BuildTranscodeCompanions detects the co-queued track issues and claims them as
/// companions so the main loop doesn't schedule a redundant TrackFixer remux.
/// </summary>
public sealed class FixTaskTranscodeCompanionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-transcompanion-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    public FixTaskTranscodeCompanionTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Theory]
    [InlineData(IssueType.Quality)]
    [InlineData(IssueType.HeavyTranscode)]
    [InlineData(IssueType.FailedTranscode)]
    public void QualityFamilyClaimsBothAudioAndSubtitleOnSamePath(IssueType transcodeType)
    {
        var transcode = NewIssue(1, transcodeType, "/lib/tv/E.mkv");
        var audio = NewIssue(2, IssueType.AudioLanguage, "/lib/tv/E.mkv");
        var subtitle = NewIssue(3, IssueType.SubtitleLanguage, "/lib/tv/E.mkv");

        var map = FixTask.BuildTranscodeCompanions([transcode, audio, subtitle]);

        Assert.Single(map);
        var companions = map[transcode.Id];
        Assert.Equal(2, companions.Count);
        Assert.Contains(audio, companions);
        Assert.Contains(subtitle, companions);
    }

    [Fact]
    public void QualityWithAudioOnlyClaimsAudio()
    {
        var transcode = NewIssue(1, IssueType.Quality, "/lib/movies/M.mp4");
        var audio = NewIssue(2, IssueType.AudioLanguage, "/lib/movies/M.mp4");

        var map = FixTask.BuildTranscodeCompanions([transcode, audio]);

        Assert.Single(map);
        Assert.Single(map[transcode.Id]);
        Assert.Equal(audio, map[transcode.Id][0]);
    }

    [Fact]
    public void QualityWithSubtitleOnlyClaimsSubtitle()
    {
        var transcode = NewIssue(1, IssueType.Quality, "/lib/movies/M.mp4");
        var subtitle = NewIssue(2, IssueType.SubtitleLanguage, "/lib/movies/M.mp4");

        var map = FixTask.BuildTranscodeCompanions([transcode, subtitle]);

        Assert.Single(map);
        Assert.Single(map[transcode.Id]);
        Assert.Equal(subtitle, map[transcode.Id][0]);
    }

    [Fact]
    public void QualityAloneClaimsNothing()
    {
        var transcode = NewIssue(1, IssueType.Quality, "/lib/movies/M.mp4");

        var map = FixTask.BuildTranscodeCompanions([transcode]);

        Assert.Empty(map);
    }

    [Fact]
    public void AudioPlusSubtitleWithoutTranscodeClaimsNothing()
    {
        // These should still be picked up by the existing combined-pass block in FixTask, which is
        // out of scope here — this test only asserts the transcode-companion routing stays out of
        // the way when no transcode issue is queued.
        var audio = NewIssue(1, IssueType.AudioLanguage, "/lib/tv/E.mkv");
        var subtitle = NewIssue(2, IssueType.SubtitleLanguage, "/lib/tv/E.mkv");

        var map = FixTask.BuildTranscodeCompanions([audio, subtitle]);

        Assert.Empty(map);
    }

    [Fact]
    public void PathMatchingIsCaseInsensitive()
    {
        // Windows filesystems are case-insensitive and the scanner can emit either casing across
        // rescans; a same-file triple across cases must still route as one.
        var transcode = NewIssue(1, IssueType.Quality, @"C:\Media\TV\Show S01E01.mkv");
        var audio = NewIssue(2, IssueType.AudioLanguage, @"c:\media\tv\show s01e01.mkv");

        var map = FixTask.BuildTranscodeCompanions([transcode, audio]);

        Assert.Single(map);
        Assert.Equal(audio, map[transcode.Id][0]);
    }

    [Fact]
    public void TranscodeAndTracksOnDifferentFilesAreNotPaired()
    {
        var transcode = NewIssue(1, IssueType.Quality, "/lib/movies/A.mkv");
        var audio = NewIssue(2, IssueType.AudioLanguage, "/lib/movies/B.mkv");

        var map = FixTask.BuildTranscodeCompanions([transcode, audio]);

        Assert.Empty(map);
    }

    [Fact]
    public void MixedFilesRouteIndependently()
    {
        // File A has a triple, file B has only a transcode, file C has only track issues. The map
        // must contain exactly one entry (file A's transcode → its two track companions).
        var aTranscode = NewIssue(1, IssueType.Quality, "/lib/A.mkv");
        var aAudio = NewIssue(2, IssueType.AudioLanguage, "/lib/A.mkv");
        var aSubtitle = NewIssue(3, IssueType.SubtitleLanguage, "/lib/A.mkv");
        var bTranscode = NewIssue(4, IssueType.HeavyTranscode, "/lib/B.mkv");
        var cAudio = NewIssue(5, IssueType.AudioLanguage, "/lib/C.mkv");
        var cSubtitle = NewIssue(6, IssueType.SubtitleLanguage, "/lib/C.mkv");

        var map = FixTask.BuildTranscodeCompanions([aTranscode, aAudio, aSubtitle, bTranscode, cAudio, cSubtitle]);

        Assert.Single(map);
        Assert.True(map.ContainsKey(aTranscode.Id));
        Assert.Equal(2, map[aTranscode.Id].Count);
    }

    [Fact]
    public void EmptyQueueReturnsEmptyMap()
    {
        var map = FixTask.BuildTranscodeCompanions([]);

        Assert.Empty(map);
    }

    [Fact]
    public void UnrelatedIssueTypesAreIgnored()
    {
        // A Duplicate + Playability + LargeTrickplay pile on the same path must not produce any
        // companion routing — the transcode-companion logic is scoped strictly to the transcode
        // family + language track types.
        var duplicate = NewIssue(1, IssueType.Duplicate, "/lib/A.mkv");
        var playability = NewIssue(2, IssueType.Playability, "/lib/A.mkv");
        var trickplay = NewIssue(3, IssueType.LargeTrickplay, "/lib/A.mkv");

        var map = FixTask.BuildTranscodeCompanions([duplicate, playability, trickplay]);

        Assert.Empty(map);
    }

    [Fact]
    public void CompanionHistoryRoundTripsThroughDb()
    {
        // Simulates FixTask writing the "Resolved by transcode pass" HistoryEntry when a Quality
        // fix with two companions succeeds. All three rows share the transcode's RecyclePath but
        // each is tied to its own IssueId so the Issues tab and Recycle Bin tab both render
        // sensible per-issue history.
        const long qualityIssueId = 100L;
        const long audioIssueId = 101L;
        const long subtitleIssueId = 102L;
        const string binPath = "/bin/20260831-101010-000-a/E.mkv";

        _db.AddHistory(new HistoryEntry
        {
            IssueId = qualityIssueId,
            Type = IssueType.Quality,
            Path = "/lib/E.mkv",
            Action = "re-encoded to 1080p HEVC (mkv), original kept in recycle bin",
            RecyclePath = binPath,
            FixedAtUtc = DateTime.UtcNow,
            Success = true
        });
        _db.AddHistory(new HistoryEntry
        {
            IssueId = audioIssueId,
            Type = IssueType.AudioLanguage,
            Path = "/lib/E.mkv",
            Action = "Resolved by transcode pass: re-encoded to 1080p HEVC (mkv), original kept in recycle bin",
            RecyclePath = binPath,
            FixedAtUtc = DateTime.UtcNow,
            Success = true
        });
        _db.AddHistory(new HistoryEntry
        {
            IssueId = subtitleIssueId,
            Type = IssueType.SubtitleLanguage,
            Path = "/lib/E.mkv",
            Action = "Resolved by transcode pass: re-encoded to 1080p HEVC (mkv), original kept in recycle bin",
            RecyclePath = binPath,
            FixedAtUtc = DateTime.UtcNow,
            Success = true
        });

        var rows = _db.GetHistory().Where(h => h.RecyclePath == binPath).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows.Select(r => r.IssueId).Distinct().Count());
        Assert.All(rows, r => Assert.Equal(binPath, r.RecyclePath));
        var companionRows = rows.Where(r => r.Action.StartsWith("Resolved by transcode pass:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, companionRows.Count);
    }

    [Fact]
    public void FixTaskSourceStillSkipsTranscodeCompanionsInMainLoop()
    {
        // Grep-style guard: FixTask.ExecuteAsync must skip the loop iteration for any track
        // issue claimed as a transcode companion. A refactor that drops this skip re-introduces
        // the original bug where TrackFixer ran redundantly (and often failed with "nothing to
        // remove") on the already-transcoded file.
        var src = File.ReadAllText(FixTaskSourcePath());
        Assert.Contains("transcodeCompanionIds.Contains(issue.Id)", src);
    }

    [Fact]
    public void FixTaskSourceStillWritesResolvedByTranscodeHistory()
    {
        // Companion resolution has to fire inside the success branch and use the companion's
        // OWN IssueId + the transcode's RecyclePath — a refactor that reused issue.Id or dropped
        // the RecyclePath assignment would leave the Recycle Bin tab unable to join the row
        // back to a bin file, showing "no history" and dead-ending the user.
        var src = File.ReadAllText(FixTaskSourcePath());
        Assert.Contains("Resolved by transcode pass:", src);
        Assert.Contains("transcodeCompanions.TryGetValue(issue.Id", src);
        Assert.Contains("IssueId = companion.Id", src);
        Assert.Contains("RecyclePath = result.RecyclePath", src);
    }

    [Fact]
    public void FixTaskSourceStillGatesCompanionStatusFlipOnRealRun()
    {
        // Match combined-pass symmetry: history rows fire on both dry-run and real runs, but the
        // Fixed status flip must only apply when !WasDryRun. Otherwise a dry-run silently drains
        // the queue and the user loses the ability to re-approve after inspection (F-206).
        var src = File.ReadAllText(FixTaskSourcePath());
        Assert.Contains("if (!result.WasDryRun)", src);
        Assert.Contains("_db.UpdateIssueStatus(companion.Id, IssueStatus.Fixed);", src);
    }

    [Fact]
    public void FixTaskSourceStillHasCombinedPairsExcludingTranscodeCompanions()
    {
        // The combined-pairs block must also exclude issues already claimed as transcode
        // companions, otherwise a triple would route through BOTH the transcode success path
        // AND the combined-pass — the audio issue would fire two ffmpeg runs and record duplicate
        // history rows.
        var src = File.ReadAllText(FixTaskSourcePath());
        Assert.Contains("!transcodeCompanionIds.Contains(i.Id)", src);
    }

    private static Issue NewIssue(long id, IssueType type, string path) => new()
    {
        Id = id,
        Type = type,
        Path = path,
        Status = IssueStatus.Queued,
        DetectedAtUtc = DateTime.UtcNow
    };

    private static string FixTaskSourcePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.MediaDash.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Jellyfin.Plugin.MediaDash", "ScheduledTasks", "FixTask.cs");
    }
}

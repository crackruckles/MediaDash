using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class ScanDiagnosticsTests
{
    [Fact]
    public void DisabledByDefaultAndDoesNotCreateARecording()
    {
        Assert.False(new PluginConfiguration().DiagnosticsEnabled);
        Assert.Null(RunDiagnosticsRecording.Start(null!, NullLogger.Instance, false));
        Assert.Null(RunDiagnosticsRecording.Start(null!, NullLogger.Instance, false, "fix"));
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("fix")]
    public void LatestRunRetainsEveryRecordAndReplacesOnlyThePreviousRunOfItsKind(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "md-scan-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = new MediaDashDb(Path.Combine(root, "diagnostics.db"));
            var otherKey = kind == "scan" ? "LastFixDiagnostics" : "LastScanDiagnostics";
            db.SetState(otherKey, "{\"Outcome\":\"Unchanged\"}");
            using (var scan = RunDiagnosticsRecording.Start(db, NullLogger.Instance, true, kind)!)
            {
                scan.Record(JsonSerializer.SerializeToElement(new { Component = "FirstScanner", Phase = "start", ProgressPercent = 0 }));
                scan.Record(JsonSerializer.SerializeToElement(new { Component = "FirstScanner", Phase = "finish", Findings = 7 }));
                Assert.Equal(2, RunDiagnosticsRecording.Read(db, kind)!.Value.GetProperty("Records").GetArrayLength());
                scan.Complete("Completed");
            }

            var saved = RunDiagnosticsRecording.Read(db, kind)!.Value;
            Assert.Equal("Completed", saved.GetProperty("Outcome").GetString());
            Assert.Equal(7, saved.GetProperty("Records")[1].GetProperty("Findings").GetInt32());
            Assert.NotEqual(JsonValueKind.Null, saved.GetProperty("FinishedAtUtc").ValueKind);
            using (var scan = RunDiagnosticsRecording.Start(db, NullLogger.Instance, true, kind)!)
            {
                Assert.Empty(RunDiagnosticsRecording.Read(db, kind)!.Value.GetProperty("Records").EnumerateArray());
                scan.Complete("Cancelled");
            }

            Assert.Equal("Cancelled", RunDiagnosticsRecording.Read(db, kind)!.Value.GetProperty("Outcome").GetString());
            db.SetState(kind == "scan" ? "LastScanDiagnostics" : "LastFixDiagnostics", "not json");
            Assert.Null(RunDiagnosticsRecording.Read(db, kind));
            Assert.Equal("{\"Outcome\":\"Unchanged\"}", db.GetState(otherKey));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}

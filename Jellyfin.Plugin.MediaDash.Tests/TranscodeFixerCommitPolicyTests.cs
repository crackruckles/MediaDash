using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// External audit finding P1-F3 (2026-09-28): pre-fix the commit-time policy callback re-read
/// config for DryRun but never re-consulted <see cref="ProtectionService"/>, so a rule added
/// during a long encode was ignored and the source file was replaced anyway. These tests pin
/// the two branches on the extracted helper — the lambda in <c>TranscodeFixer.FixAsync</c> is
/// a straight delegation to <c>EvaluateCommitPolicy</c>.
/// </summary>
public sealed class TranscodeFixerCommitPolicyTests : IDisposable
{
    private static string P(string path) => System.IO.Path.Combine(System.IO.Path.GetTempPath(), path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "transcode-commit-policy-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;
    private readonly ProtectionService _protection;

    public TranscodeFixerCommitPolicyTests()
    {
        _db = new MediaDashDb(_dbPath);
        _protection = new ProtectionService(_db.Protection, libraryManager: null);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void EvaluateCommitPolicy_NoProtection_NotDryRun_Allows()
    {
        var config = new PluginConfiguration { DryRun = false, TranscodeFixMode = FixMode.Automatic };
        var decision = TranscodeFixer.EvaluateCommitPolicy(config, _protection, P("media/Alien.mkv"), IssueType.Quality);
        Assert.Equal(PolicyDecision.Allow, decision);
    }

    [Fact]
    public void EvaluateCommitPolicy_DryRunFlippedMidEncode_RefusesWithTypeDisabled()
    {
        // F-206: user flipped Dry-run ON during a long encode. The commit MUST NOT run under
        // the stale snapshot the fixer captured at FixAsync start.
        var config = new PluginConfiguration { DryRun = true };
        var decision = TranscodeFixer.EvaluateCommitPolicy(config, _protection, P("media/Alien.mkv"), IssueType.Quality);
        Assert.False(decision.Allowed);
        Assert.Equal(FixReason.TypeDisabled, decision.Reason);
    }

    [Theory]
    [InlineData(FixMode.Off)]
    public void EvaluateCommitPolicy_ModeRevokedMidEncode_Refuses(FixMode mode)
    {
        var config = new PluginConfiguration { DryRun = false, TranscodeFixMode = mode };
        var decision = TranscodeFixer.EvaluateCommitPolicy(config, _protection, P("media/Alien.mkv"), IssueType.Quality);
        Assert.False(decision.Allowed);
        Assert.Equal(FixReason.TypeDisabled, decision.Reason);
    }

    [Fact]
    public void EvaluateCommitPolicy_FixerDisabledMidEncode_Refuses()
    {
        var config = new PluginConfiguration { DryRun = false, TranscodeFixMode = FixMode.Automatic, FixerDisabled = ["Quality"] };
        Assert.False(TranscodeFixer.EvaluateCommitPolicy(config, _protection, P("media/Alien.mkv"), IssueType.Quality).Allowed);
    }

    [Fact]
    public void EvaluateCommitPolicy_ProtectionAddedMidEncode_RefusesWithProtected()
    {
        // Exact P1-F3 shape: user added an All-mask folder rule between plan build and commit.
        // The extracted helper must refuse so the swap is aborted and the source file survives.
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = P("media/movies"), ScannersMask = "*", FixersMask = "*" });
        _protection.Invalidate();

        var config = new PluginConfiguration { DryRun = false, TranscodeFixMode = FixMode.Automatic };
        var decision = TranscodeFixer.EvaluateCommitPolicy(config, _protection, P("media/movies/Alien.mkv"), IssueType.Quality);
        Assert.False(decision.Allowed);
        Assert.Equal(FixReason.Protected, decision.Reason);
    }

    [Fact]
    public void EvaluateCommitPolicy_ProtectionOnDifferentTree_DoesNotRefuse()
    {
        // Path outside the protected tree must still commit. Regression guard against a
        // future "block everything" bug.
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = P("media/tv"), ScannersMask = "*", FixersMask = "*" });
        _protection.Invalidate();

        var config = new PluginConfiguration { DryRun = false, TranscodeFixMode = FixMode.Automatic };
        var decision = TranscodeFixer.EvaluateCommitPolicy(config, _protection, P("media/movies/Alien.mkv"), IssueType.Quality);
        Assert.Equal(PolicyDecision.Allow, decision);
    }

    [Fact]
    public void EvaluateCommitPolicy_DryRunTakesPrecedenceOverProtection()
    {
        // Both gates fire — the order isn't a behaviour contract but the refusal must be
        // decisive (one reason wins). DryRun is checked first so the caller sees TypeDisabled.
        _db.Protection.Add(new ProtectionRule { Kind = "folder", Target = P("media/movies"), FixersMask = "*" });
        _protection.Invalidate();

        var config = new PluginConfiguration { DryRun = true };
        var decision = TranscodeFixer.EvaluateCommitPolicy(config, _protection, P("media/movies/Alien.mkv"), IssueType.Quality);
        Assert.False(decision.Allowed);
        Assert.Equal(FixReason.TypeDisabled, decision.Reason);
    }
}


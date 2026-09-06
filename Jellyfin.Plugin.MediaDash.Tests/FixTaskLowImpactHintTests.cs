using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using System.Linq;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Discoverability invariants for the Low system impact hint (<c>FixTask.LowImpactHint</c>).
/// The hint should nudge exactly the users who need it — machines where a fix run is currently
/// competing with real user activity — and nudge them at most once per plugin lifetime so
/// dismissing it isn't a losing battle against a diagnostic that keeps returning.
/// </summary>
public class FixTaskLowImpactHintTests
{
    public FixTaskLowImpactHintTests()
    {
        // Reset session flag between tests. This class touches a static so tests must be
        // independent — xUnit constructs a fresh instance per test, and the reset here runs
        // per-test.
        FixTask.LowImpactHintEmitted = false;
        Diagnostics.RemoveMatching("FixTask.LowImpactHint", "Low system impact mode");
    }

    [Fact]
    public void TryEmitLowImpactHint_EmitsOnceWhenConfigOff()
    {
        var emitted = FixTask.TryEmitLowImpactHint(lowSystemImpactMode: false);

        Assert.True(emitted);
        Assert.True(FixTask.LowImpactHintEmitted);
        var hits = Diagnostics.Recent().Count(d => d.Source == "FixTask.LowImpactHint");
        Assert.Equal(1, hits);
    }

    [Fact]
    public void TryEmitLowImpactHint_SecondCallDoesNothing()
    {
        FixTask.TryEmitLowImpactHint(lowSystemImpactMode: false);
        var recorded = Diagnostics.Recent().Count(d => d.Source == "FixTask.LowImpactHint");

        var emittedAgain = FixTask.TryEmitLowImpactHint(lowSystemImpactMode: false);

        Assert.False(emittedAgain);
        // Diagnostics.Record dedups by source + message, so the count would still be 1 either way —
        // asserting emittedAgain is the meaningful signal. But confirm the count didn't grow.
        Assert.Equal(recorded, Diagnostics.Recent().Count(d => d.Source == "FixTask.LowImpactHint"));
    }

    [Fact]
    public void TryEmitLowImpactHint_ConfigOnSuppresses()
    {
        // User has already turned the setting on — hinting at it would be noise. Even the FIRST
        // call must no-op, and the emitted-flag must NOT be flipped so a subsequent config-off
        // period (e.g. user toggled it back to compare speeds) can still receive the hint.
        var emitted = FixTask.TryEmitLowImpactHint(lowSystemImpactMode: true);

        Assert.False(emitted);
        Assert.False(FixTask.LowImpactHintEmitted);
        Assert.DoesNotContain(Diagnostics.Recent(), d => d.Source == "FixTask.LowImpactHint");
    }

    [Fact]
    public void TryEmitLowImpactHint_ConfigOnThenOff_HintStillFires()
    {
        // A user who tried the setting on, decided it was too slow, and turned it back off should
        // still be able to receive the hint on the next mid-run pause.
        FixTask.TryEmitLowImpactHint(lowSystemImpactMode: true);
        Assert.False(FixTask.LowImpactHintEmitted);

        var emitted = FixTask.TryEmitLowImpactHint(lowSystemImpactMode: false);

        Assert.True(emitted);
        Assert.True(FixTask.LowImpactHintEmitted);
    }

    [Fact]
    public void HintMessage_NamesTheSettingPathAndTheSymptom()
    {
        // The hint has to tell the user WHERE the setting is AND link to the symptom they're
        // hitting, otherwise it reads like unrelated noise on the Errors tab. Grep the emitted
        // diagnostic to pin both.
        FixTask.TryEmitLowImpactHint(lowSystemImpactMode: false);

        var hint = Diagnostics.Recent().Single(d => d.Source == "FixTask.LowImpactHint");
        Assert.Contains("Low system impact mode", hint.Message);
        Assert.Contains("Settings → Safety", hint.Message);
        Assert.Contains("HDDs", hint.Message);
    }
}

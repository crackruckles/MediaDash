using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Regression guard: the diagnostics dedup key must be process-stable so restarting Jellyfin
// doesn't accumulate "duplicate" rows for the same recurring error message. The reference
// values in this file are the deterministic FNV-1a 32-bit output — if these numbers ever
// change, every existing user's diagnostics dedup breaks on upgrade (or worse, silently
// collapses two different messages together via a hash change that happened to coincide).
public class DiagnosticsStableHashTests
{
    [Fact]
    public void HashIsDeterministicWithinProcess()
    {
        var msg = "Movies target folder 'C:\\dev\\mediadash-fixtures\\movies' is not inside any Jellyfin library.";
        Assert.Equal(Diagnostics.StableStringHash(msg), Diagnostics.StableStringHash(msg));
    }

    [Fact]
    public void HashIsProcessStable_KnownReferenceValues()
    {
        // These reference values pin the hash function. A change here on next test run means
        // the algorithm changed and every user's existing (source, hash) key would rotate,
        // silently breaking dedup for anything already persisted. Recomputing these constants
        // is intentional — the DB-migration path (schema v9) wipes stale rows so a controlled
        // rotation is safe, but an accidental one is not.
        Assert.Equal(-2128831035, Diagnostics.StableStringHash(string.Empty));
        Assert.Equal(1335831723, Diagnostics.StableStringHash("hello"));
        Assert.Equal(1519859555, Diagnostics.StableStringHash("MediaSorter.BadTarget: Movies missing"));
    }

    [Fact]
    public void DistinctMessagesProduceDistinctHashes()
    {
        var a = Diagnostics.StableStringHash("Movies target folder is missing.");
        var b = Diagnostics.StableStringHash("TV target folder is missing.");
        var c = Diagnostics.StableStringHash("Anime target folder is missing.");
        Assert.NotEqual(a, b);
        Assert.NotEqual(b, c);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void EmptyAndSingleCharDoNotCollide()
    {
        Assert.NotEqual(Diagnostics.StableStringHash(string.Empty), Diagnostics.StableStringHash("a"));
    }

    [Fact]
    public void UnicodeMessagesHashStably()
    {
        // Real diagnostics carry em-dashes and non-ASCII path components; assert one is stable.
        var msg = "Repair — dropped 3 bitmap subtitle track(s) that .m4v can't hold. Also affects café/naïve paths.";
        Assert.Equal(Diagnostics.StableStringHash(msg), Diagnostics.StableStringHash(msg));
    }
}

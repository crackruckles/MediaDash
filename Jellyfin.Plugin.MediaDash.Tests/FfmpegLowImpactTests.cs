using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Insertion invariants for <see cref="FfmpegExecutor.InjectRealtimeThrottle"/>. Low system impact
/// mode wraps every ffmpeg call in <c>-re</c> to cap effective throughput at 1x realtime. The
/// helper has to (1) put <c>-re</c> as an INPUT option (before <c>-i</c>), (2) never double-add
/// when the caller was already realtime-aware, and (3) leave input-less calls alone.
/// </summary>
public class FfmpegLowImpactTests
{
    [Fact]
    public void InjectRealtimeThrottle_InsertsBeforeFirstInput()
    {
        // Typical TrackFixer remux arg shape after 1.0.7.3 (-fflags +genpts is an input option).
        var input = new[] { "-fflags", "+genpts", "-i", "src.mkv", "-map", "0", "-c", "copy", "out.mkv" };

        var output = FfmpegExecutor.InjectRealtimeThrottle(input);

        Assert.Equal(
            new[] { "-fflags", "+genpts", "-re", "-i", "src.mkv", "-map", "0", "-c", "copy", "out.mkv" },
            output);
    }

    [Fact]
    public void InjectRealtimeThrottle_InputAtIndexZero_InsertsAtIndexZero()
    {
        var input = new[] { "-i", "src.mkv", "-c", "copy", "out.mkv" };

        var output = FfmpegExecutor.InjectRealtimeThrottle(input);

        Assert.Equal(new[] { "-re", "-i", "src.mkv", "-c", "copy", "out.mkv" }, output);
    }

    [Fact]
    public void InjectRealtimeThrottle_MultipleInputs_ThrottlesFirstOnly()
    {
        // Some ffmpeg invocations declare two inputs (e.g. video + external subtitle sidecar).
        // -re on the first input is enough to pace the overall pipeline; adding it to every input
        // duplicates work and can confuse the encoder.
        var input = new[] { "-i", "video.mkv", "-i", "subs.srt", "-map", "0", "-map", "1", "out.mkv" };

        var output = FfmpegExecutor.InjectRealtimeThrottle(input);

        Assert.Equal(
            new[] { "-re", "-i", "video.mkv", "-i", "subs.srt", "-map", "0", "-map", "1", "out.mkv" },
            output);
    }

    [Fact]
    public void InjectRealtimeThrottle_IdempotentWhenAlreadyPresent()
    {
        // A caller that ALREADY wanted realtime pacing must not get two -re flags — ffmpeg would
        // still work but log a "duplicate option" warning and the log line would mislead.
        var input = new[] { "-re", "-i", "src.mkv", "-c", "copy", "out.mkv" };

        var output = FfmpegExecutor.InjectRealtimeThrottle(input);

        Assert.Same(input, output);
    }

    [Fact]
    public void InjectRealtimeThrottle_NoInput_ReturnsUnchanged()
    {
        // Filter-only or codec-listing invocations have no -i. -re has no meaning there and would
        // just be dead flag noise. Return the input unchanged (same reference so callers can
        // detect the no-op if they care).
        var input = new[] { "-hide_banner", "-encoders" };

        var output = FfmpegExecutor.InjectRealtimeThrottle(input);

        Assert.Same(input, output);
    }

    [Fact]
    public void InjectRealtimeThrottle_EmptyArgs_ReturnsUnchanged()
    {
        var input = System.Array.Empty<string>();

        var output = FfmpegExecutor.InjectRealtimeThrottle(input);

        Assert.Same(input, output);
    }

    [Fact]
    public void InjectRealtimeThrottle_ReTokenNotConfusedWithReencodeOptions()
    {
        // -re is a specific token. Something like "-report" or "-reset_timestamps" starts with the
        // same two letters but must NOT count as "already has -re" — string.Equals guards against this.
        var input = new[] { "-report", "-i", "src.mkv", "out.mkv" };

        var output = FfmpegExecutor.InjectRealtimeThrottle(input);

        Assert.Equal(new[] { "-report", "-re", "-i", "src.mkv", "out.mkv" }, output);
    }
}

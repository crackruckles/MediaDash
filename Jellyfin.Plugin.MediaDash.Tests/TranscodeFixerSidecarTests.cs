using System.IO;
using System.Text;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class TranscodeFixerSidecarTests
{
    [Fact]
    public void SidecarPath_ShortName_AppendsMarkerAndInflightTail()
    {
        // R09 (2026-09-20): sidecar tail is `.mediadash.tmp` so Jellyfin doesn't index the
        // in-flight file as media. Extension is preserved in the middle for human-readability
        // and for the ffmpeg -f mapping at call sites.
        var path = TranscodeFixer.SidecarPath(@"/mnt/media/movies/Short.mkv", "tmp", "mkv");
        Assert.EndsWith("Short.mkv.mediadash.tmp.mkv.mediadash.tmp", path.Replace('\\', '/'));
    }

    [Fact]
    public void SidecarPath_ShortName_NoExtension_AppendsBareMarkerAndInflightTail()
    {
        var path = TranscodeFixer.SidecarPath(@"/mnt/media/movies/Short.mkv", "new", string.Empty);
        Assert.EndsWith("Short.mkv.mediadash.new.mediadash.tmp", path.Replace('\\', '/'));
    }

    [Fact]
    public void SidecarPath_LongCyrillicName_FallsBackToHashedName()
    {
        // The real production filename that triggered ffmpeg's "Error opening output". Appending
        // ".mediadash.tmp.mkv.mediadash.tmp" would push this over the 255-byte Linux NAME_MAX.
        var longName = "Бродяга Кэнсин - Начало _ Ruroni Kenshin - Sai shusho - The Beginning _ Rurouni Kenshin - Final Chapter Part II [2021, Япония, Боевик, драма, WEB-DL 1080p] DVO (RealFake) + Sub Rus, Eng + Multi + Original Jpn.mkv";
        var input = "/mnt/media/Movies/" + longName;

        var path = TranscodeFixer.SidecarPath(input, "tmp", "mkv");

        var sidecarName = Path.GetFileName(path);
        Assert.True(Encoding.UTF8.GetByteCount(sidecarName) <= 255,
            $"Sidecar filename must fit under Linux NAME_MAX (got {Encoding.UTF8.GetByteCount(sidecarName)} bytes).");
        Assert.StartsWith("mediadash.tmp.", sidecarName);
        Assert.EndsWith(".mediadash.tmp", sidecarName);
    }

    [Fact]
    public void SidecarPath_TailIsNotAJellyfinVideoExtension()
    {
        // R09 (2026-09-20): guard against future refactors accidentally putting a media
        // extension back at the tail. Jellyfin's video-extension list checks the LAST
        // extension component; anything ending in .mkv/.mp4/.avi/etc. gets indexed. `.tmp`
        // does not.
        var path = TranscodeFixer.SidecarPath("/x/movie.mkv", "tmp.abc123", "mp4");
        var lastExt = Path.GetExtension(path);
        Assert.Equal(".tmp", lastExt, ignoreCase: false);
        Assert.DoesNotMatch(@"\.(mkv|mp4|m4v|avi|mov|mpg|ts|m2ts|wmv|webm|flv|mp3|flac|aac|m4a|opus|wav|jpg|png|webp)$", path);
    }

    [Fact]
    public void SidecarPath_MiddleInfixStillMatchesSweepPattern()
    {
        // R09 (2026-09-20): the LibraryGuard.SweepOrphanSidecars patterns match on the middle
        // `.mediadash.<marker>` infix — regression guard that the InflightTail addition doesn't
        // break the substring match.
        var tmp = TranscodeFixer.SidecarPath("/x/movie.mkv", "tmp.abc123", "mp4");
        var newf = TranscodeFixer.SidecarPath("/x/movie.mp4", "new.abc123", "");
        var strip = TranscodeFixer.SidecarPath("/x/album.mp3", "strip", "mp3");
        var swap = TranscodeFixer.SidecarPath("/x/album.mp3", "swap", "");
        Assert.Contains(".mediadash.tmp", tmp);
        Assert.Contains(".mediadash.new", newf);
        Assert.Contains(".mediadash.strip", strip);
        Assert.Contains(".mediadash.swap", swap);
    }

    [Theory]
    [InlineData("mkv", "matroska")]
    [InlineData("mp4", "mp4")]
    [InlineData("m4v", "mp4")]
    [InlineData("mov", "mov")]
    [InlineData("webm", "webm")]
    [InlineData("ts", "mpegts")]
    [InlineData("m2ts", "mpegts")]
    [InlineData("mp3", "mp3")]
    [InlineData("flac", "flac")]
    [InlineData("m4a", "ipod")]
    [InlineData("wav", "wav")]
    [InlineData("opus", "opus")]
    [InlineData("png", "image2")]
    [InlineData("webp", "webp")]
    [InlineData("jpg", "image2")]
    public void ContainerToFfmpegFormat_MapsKnownContainers(string container, string expected)
    {
        Assert.Equal(expected, TranscodeFixer.ContainerToFfmpegFormat(container));
    }

    [Fact]
    public void ContainerToFfmpegFormat_UnknownIsUnsupported()
    {
        // Callers refuse an empty muxer before starting ffmpeg.
        Assert.Equal(string.Empty, TranscodeFixer.ContainerToFfmpegFormat("WeirdFormat"));
    }

    [Fact]
    public void SidecarPath_LongName_IsStableAcrossCalls()
    {
        // Retries must land on the same sidecar path so the finally-block cleanup finds them.
        var longName = new string('л', 300); // 600-byte Cyrillic filename, well over the limit.
        var input = "/mnt/media/" + longName;

        var a = TranscodeFixer.SidecarPath(input, "tmp", "mkv");
        var b = TranscodeFixer.SidecarPath(input, "tmp", "mkv");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Truncate_ShortText_ReturnsUnchanged()
    {
        Assert.Equal("hello", TranscodeFixer.Truncate("hello"));
    }

    [Fact]
    public void Truncate_LongText_KeepsTailNotHead()
    {
        // ffmpeg's actual error line is at the END of its output; the head is codec/stream banners.
        // Truncating to the head would hide the real failure — regress-test that we keep the tail.
        var banner = new string('x', 1000);
        var errorLine = "Error opening output file: No space left on device";
        var input = banner + errorLine;

        var truncated = TranscodeFixer.Truncate(input);

        Assert.Contains(errorLine, truncated);
        Assert.StartsWith("… ", truncated);
    }
}

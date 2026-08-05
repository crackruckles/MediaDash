using System.IO;
using System.Text;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class TranscodeFixerSidecarTests
{
    [Fact]
    public void SidecarPath_ShortName_AppendsMarker()
    {
        var path = TranscodeFixer.SidecarPath(@"/mnt/media/movies/Short.mkv", "tmp", "mkv");
        Assert.EndsWith("Short.mkv.mediadash.tmp.mkv", path.Replace('\\', '/'));
    }

    [Fact]
    public void SidecarPath_ShortName_NoExtension_AppendsBareMarker()
    {
        var path = TranscodeFixer.SidecarPath(@"/mnt/media/movies/Short.mkv", "new", string.Empty);
        Assert.EndsWith("Short.mkv.mediadash.new", path.Replace('\\', '/'));
    }

    [Fact]
    public void SidecarPath_LongCyrillicName_FallsBackToHashedName()
    {
        // The real production filename that triggered ffmpeg's "Error opening output". Appending
        // ".mediadash.tmp.mkv" would push this over the 255-byte Linux NAME_MAX.
        var longName = "Бродяга Кэнсин - Начало _ Ruroni Kenshin - Sai shusho - The Beginning _ Rurouni Kenshin - Final Chapter Part II [2021, Япония, Боевик, драма, WEB-DL 1080p] DVO (RealFake) + Sub Rus, Eng + Multi + Original Jpn.mkv";
        var input = "/mnt/media/Movies/" + longName;

        var path = TranscodeFixer.SidecarPath(input, "tmp", "mkv");

        var sidecarName = Path.GetFileName(path);
        Assert.True(Encoding.UTF8.GetByteCount(sidecarName) <= 255,
            $"Sidecar filename must fit under Linux NAME_MAX (got {Encoding.UTF8.GetByteCount(sidecarName)} bytes).");
        Assert.StartsWith("mediadash.tmp.", sidecarName);
        Assert.EndsWith(".mkv", sidecarName);
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

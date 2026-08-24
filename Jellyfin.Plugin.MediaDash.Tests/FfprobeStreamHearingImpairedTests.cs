using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Probing;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class FfprobeStreamHearingImpairedTests
{
    [Fact]
    public void Disposition_HearingImpaired_Set_IsTrue()
    {
        var s = new FfprobeStreamInfo { Disposition = new Dictionary<string, int> { ["hearing_impaired"] = 1 } };
        Assert.True(s.IsHearingImpaired);
    }

    [Fact]
    public void Disposition_HearingImpaired_Zero_IsFalse()
    {
        var s = new FfprobeStreamInfo { Disposition = new Dictionary<string, int> { ["hearing_impaired"] = 0, ["default"] = 1 } };
        Assert.False(s.IsHearingImpaired);
    }

    [Fact]
    public void NoDisposition_IsFalse()
    {
        Assert.False(new FfprobeStreamInfo().IsHearingImpaired);
    }

    // Real ffprobe -show_streams output shape for an SDH English subtitle track (subrip).
    [Fact]
    public void Deserializes_RealFfprobeDisposition()
    {
        const string json = """
        {
          "index": 3,
          "codec_type": "subtitle",
          "codec_name": "subrip",
          "tags": { "language": "eng", "title": "English SDH" },
          "disposition": {
            "default": 0, "dub": 0, "original": 0, "comment": 0, "lyrics": 0,
            "karaoke": 0, "forced": 0, "hearing_impaired": 1, "visual_impaired": 0,
            "clean_effects": 0, "attached_pic": 0, "timed_thumbnails": 0, "captions": 0,
            "descriptions": 0, "metadata": 0, "dependent": 0, "still_image": 0
          }
        }
        """;

        var stream = JsonSerializer.Deserialize<FfprobeStreamInfo>(json);
        Assert.NotNull(stream);
        Assert.Equal("eng", stream!.Language);
        Assert.True(stream.IsHearingImpaired);
    }
}

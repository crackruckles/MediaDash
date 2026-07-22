namespace Jellyfin.Plugin.MediaDash.Configuration;

/// <summary>
/// Speed vs. quality preset for re-encodes. Maps to ffmpeg preset + CRF pairs.
/// </summary>
public enum EncodePreset
{
    /// <summary>preset=fast, crf=25 for x264/x265; preset=6/crf=32 for svtav1. Cuts encode time roughly in half at a modest quality cost.</summary>
    Faster = 0,

    /// <summary>preset=medium, crf=23 for x264/x265; preset=8/crf=30 for svtav1. Default; the "recommended" ffmpeg settings.</summary>
    Balanced = 1,

    /// <summary>preset=slow, crf=20 for x264/x265; preset=10/crf=28 for svtav1. Doubles encode time for slightly smaller / higher-quality output.</summary>
    Best = 2
}

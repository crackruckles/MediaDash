using System.IO;
using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class RestoreResolveNonCollidingPathTests
{
    [Fact]
    public void ReturnsOriginal_WhenNothingAtPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mediadash-restore-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "clip.mkv");
            Assert.Equal(target, MediaDashController.ResolveNonCollidingRestorePath(target));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UsesRestoredSuffix_OnFirstCollision()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mediadash-restore-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var original = Path.Combine(dir, "clip.mkv");
            File.WriteAllText(original, string.Empty);

            var expected = Path.Combine(dir, "clip-restored.mkv");
            Assert.Equal(expected, MediaDashController.ResolveNonCollidingRestorePath(original));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CountsUp_OnFurtherCollisions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mediadash-restore-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var original = Path.Combine(dir, "clip.mkv");
            File.WriteAllText(original, string.Empty);
            File.WriteAllText(Path.Combine(dir, "clip-restored.mkv"), string.Empty);
            File.WriteAllText(Path.Combine(dir, "clip-restored-2.mkv"), string.Empty);

            var expected = Path.Combine(dir, "clip-restored-3.mkv");
            Assert.Equal(expected, MediaDashController.ResolveNonCollidingRestorePath(original));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HandlesExtensionlessFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mediadash-restore-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var original = Path.Combine(dir, "README");
            File.WriteAllText(original, string.Empty);

            var expected = Path.Combine(dir, "README-restored");
            Assert.Equal(expected, MediaDashController.ResolveNonCollidingRestorePath(original));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HandlesMultipleDotsInBasename()
    {
        // "S01E01.Show.Name.1080p.mkv" — Path.GetFileNameWithoutExtension strips the last extension.
        // The suffix must land before ".mkv", not before the first dot, otherwise the restored file
        // gets an ambiguous extension chain.
        var dir = Path.Combine(Path.GetTempPath(), "mediadash-restore-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var original = Path.Combine(dir, "S01E01.Show.Name.1080p.mkv");
            File.WriteAllText(original, string.Empty);

            var expected = Path.Combine(dir, "S01E01.Show.Name.1080p-restored.mkv");
            Assert.Equal(expected, MediaDashController.ResolveNonCollidingRestorePath(original));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HandlesDoubleExtension_LikeSubtitleSidecars()
    {
        // ".en.srt" is the common pattern for language-tagged subtitle sidecars. GetFileNameWithoutExtension
        // strips ".srt" only, so the suffix lands before ".srt" while ".en" stays in the name. Lossy
        // but consistent with how the rest of the codebase treats language-suffixed files.
        var dir = Path.Combine(Path.GetTempPath(), "mediadash-restore-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var original = Path.Combine(dir, "Movie.en.srt");
            File.WriteAllText(original, string.Empty);

            var expected = Path.Combine(dir, "Movie.en-restored.srt");
            Assert.Equal(expected, MediaDashController.ResolveNonCollidingRestorePath(original));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CountsUp_ThroughManyCollisions()
    {
        // Stress: 10 consecutive collisions. Guards against off-by-one in the counter (a naive
        // "counter = 1" start would skip the -1 suffix; the current implementation starts the
        // suffixless "-restored" then "-restored-2", so hitting -10 exercises the loop several times.
        var dir = Path.Combine(Path.GetTempPath(), "mediadash-restore-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var original = Path.Combine(dir, "clip.mkv");
            File.WriteAllText(original, string.Empty);
            File.WriteAllText(Path.Combine(dir, "clip-restored.mkv"), string.Empty);
            for (var i = 2; i <= 9; i++)
            {
                File.WriteAllText(Path.Combine(dir, "clip-restored-" + i + ".mkv"), string.Empty);
            }

            var expected = Path.Combine(dir, "clip-restored-10.mkv");
            Assert.Equal(expected, MediaDashController.ResolveNonCollidingRestorePath(original));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

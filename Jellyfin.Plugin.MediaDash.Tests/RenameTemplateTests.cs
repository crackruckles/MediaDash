using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class RenameTemplateTests
{
    [Theory]
    [InlineData("Blade Runner", "Blade Runner")]
    [InlineData("Blade: Runner", "Blade Runner")]
    [InlineData("Path/With\\Sep|<Chars>", "PathWithSepChars")]
    [InlineData("  Trim  ", "Trim")]
    [InlineData("Multiple   spaces", "Multiple spaces")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("", "Untitled")]
    [InlineData("///", "Untitled")]
    public void Scrub_HandlesForbiddenAndEdgeCases(string input, string expected)
    {
        Assert.Equal(expected, RenameTemplate.Scrub(input));
    }
}

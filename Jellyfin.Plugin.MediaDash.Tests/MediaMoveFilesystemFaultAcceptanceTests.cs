using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>Requires the owned private Linux mount driver; never run against a media share.</summary>
[Trait("Category", "ReportedIssueExternal")]
[Collection("Diagnostics")]
public sealed class MediaMoveFilesystemFaultAcceptanceTests
{
    [Theory]
    [InlineData("enospc")]
    [InlineData("permission")]
    [InlineData("healthy")]
    public async Task CrossFilesystemRefusalPreservesCompleteSourcesBeforeJournal(string fault)
    {
        using var fixture = new MediaMoveExecutionTests();
        await fixture.RunOwnedLinuxFilesystemFault(fault);
    }
}

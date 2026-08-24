using System;
using Jellyfin.Plugin.MediaDash.Analytics;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Field report: analytics install identity replaced by a Time-Bounded Rotational Hash so nothing
// persistent that ties reports across months lives on disk or in the payload. Guards:
//   - Stable inside a calendar month (backend can still dedup).
//   - Different across months (no cross-month linkability).
//   - Different across installs in the same month (still counts distinct installs).
//   - Deterministic sentinel when the Jellyfin SystemId is missing (no random Guid slippage).
public sealed class AnalyticsInstallIdTests
{
    [Fact]
    public void SameInstallSameMonth_ProducesSameId()
    {
        var month = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = AnalyticsReporter.ComputeMonthlyInstallId("jellyfin-abc123", month);
        var b = AnalyticsReporter.ComputeMonthlyInstallId("jellyfin-abc123", month);
        Assert.Equal(a, b);
    }

    [Fact]
    public void SameInstallDifferentMonth_ProducesDifferentId()
    {
        var systemId = "jellyfin-abc123";
        var jul = AnalyticsReporter.ComputeMonthlyInstallId(systemId, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var aug = AnalyticsReporter.ComputeMonthlyInstallId(systemId, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(jul, aug);
    }

    [Fact]
    public void DifferentInstallsSameMonth_ProduceDifferentIds()
    {
        var month = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = AnalyticsReporter.ComputeMonthlyInstallId("jellyfin-abc123", month);
        var b = AnalyticsReporter.ComputeMonthlyInstallId("jellyfin-xyz789", month);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingSystemId_ProducesDeterministicSentinel(string? systemId)
    {
        // A rare Jellyfin build without a SystemId shouldn't blow up or return a random Guid —
        // it should return the same stable "no-system-id" fallback for every install that hits
        // this branch inside a month. Acceptable as an aggregate stats bucket.
        var month = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = AnalyticsReporter.ComputeMonthlyInstallId(systemId, month);
        var b = AnalyticsReporter.ComputeMonthlyInstallId(systemId, month);
        Assert.Equal(a, b);
        Assert.NotEqual(Guid.Empty, a);
    }

    [Fact]
    public void Uuidv5_VersionAndVariantBitsAreSet()
    {
        // RFC 4122 § 4.3 shape: version nibble = 0x5, variant top bits = 10x.
        var id = AnalyticsReporter.ComputeMonthlyInstallId("jellyfin-abc123", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        var bytes = id.ToByteArray();
        // Guid.ToByteArray() emits the first three fields little-endian, so the "version" nibble
        // that would be at byte-index 6 in big-endian ends up at byte-index 7 here. Same story for
        // the variant nibble: RFC 4122 § 4.1.1 puts it at big-endian byte 8, which stays 8 in
        // Guid's mixed-endian layout because bytes 8-15 aren't byte-swapped.
        Assert.Equal(0x50, bytes[7] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }
}

using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class EbookConverterTests : FormatFixtureBase
{
    [Fact]
    public void KeyIsEbook()
    {
        Assert.Equal("ebook", NewConverter().Key);
    }

    [Fact]
    public void CanConvertMatrixEbookExtensions()
    {
        var c = NewConverter();
        Assert.True(c.CanConvert(".mobi"));
        Assert.True(c.CanConvert(".azw3"));
        Assert.True(c.CanConvert(".djvu"));
        Assert.True(c.CanConvert(".fb2"));
        Assert.False(c.CanConvert(".epub"));
        Assert.False(c.CanConvert(".cbr"));
    }

    [Fact]
    public async Task ReportsUnavailableWhenToolchainMissing()
    {
        using var tmp = new TempDir();
        // External audit P2 #8: inject a path resolver that ignores the host's installed Calibre
        // so the "unavailable" assertion holds regardless of developer-machine state.
        var toolchain = new CalibreToolchain(new NoOpToolchainDownloader(), NullLogger<CalibreToolchain>.Instance, tmp.Path, pathResolver: _ => null);
        var config = new StubConfig();
        var converter = new EbookConverter(toolchain, config, NullLogger<EbookConverter>.Instance);

        var availability = await converter.EnsureToolchainAsync(CancellationToken.None);

        Assert.Equal(ConverterAvailability.Unavailable, availability);
    }

    [Fact]
    public async Task ConvertsMobiToEpubWhenCalibreAvailable()
    {
        var source = FixturePath("ebook", ".mobi");
        if (!File.Exists(source))
        {
            return;
        }

        using var tmp = new TempDir();
        var toolchain = new CalibreToolchain(new NoOpToolchainDownloader(), NullLogger<CalibreToolchain>.Instance, tmp.Path);
        var config = new StubConfig();
        var converter = new EbookConverter(toolchain, config, NullLogger<EbookConverter>.Instance);

        var readiness = await converter.EnsureToolchainAsync(CancellationToken.None);
        if (readiness != ConverterAvailability.Ready)
        {
            // Calibre not installed on this test host — the Task 15 E2E run covers the real conversion.
            return;
        }

        var target = Path.Combine(tmp.Path, "out.epub");
        var result = await converter.ConvertAsync(source, target, null, CancellationToken.None);

        Assert.True(result.Success, result.Reason);
        Assert.True(File.Exists(target));
        Assert.True(new FileInfo(target).Length > 0);
        using var epub = ZipFile.OpenRead(target);
        var package = Assert.Single(epub.Entries, entry => entry.Name.EndsWith(".opf", StringComparison.Ordinal));
        using var packageStream = package.Open();
        Assert.Equal("3.0", (string?)XDocument.Load(packageStream).Root?.Attribute("version"));
    }

    private static EbookConverter NewConverter()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "mediadash-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        var toolchain = new CalibreToolchain(new NoOpToolchainDownloader(), NullLogger<CalibreToolchain>.Instance, tmp);
        return new EbookConverter(toolchain, new StubConfig(), NullLogger<EbookConverter>.Instance);
    }

    private sealed class StubConfig : IPluginConfigurationProvider
    {
        public PluginConfiguration GetSnapshot() => new();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mediadash-tests-" + Guid.NewGuid());

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}

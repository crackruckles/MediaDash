using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Probing;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("Diagnostics")]
public sealed class PlayabilityFalsePositiveTests : IDisposable
{
    private readonly Plugin? _previous = Plugin.Instance;
    private readonly string _path = Path.Combine(Path.GetTempPath(), "md-playability-" + Guid.NewGuid().ToString("N") + ".mkv");

    public PlayabilityFalsePositiveTests()
    {
        File.WriteAllBytes(_path, new byte[] { 1, 2, 3 });
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        typeof(Plugin).BaseType!.GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, new PluginConfiguration { ThoroughPlayabilityCheck = false });
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, plugin);
    }

    [Fact]
    public async Task HealthyPhotoDoesNotNeedAVideoDuration()
        => Assert.Null(await Evaluate(new Photo(), new FfprobeData { Streams = new[] { new FfprobeStreamInfo { CodecType = "video", CodecName = "mjpeg" } } }));

    [Theory]
    [InlineData("N/A")]
    [InlineData("0")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public async Task UnknownDurationStillRequiresADecodeToReportCorruption(string duration)
        => await Assert.ThrowsAsync<InvalidOperationException>(() => Evaluate(new Video(), new FfprobeData { Format = new FfprobeFormat { Duration = duration }, Streams = new[] { new FfprobeStreamInfo { CodecType = "video" } } }));

    [Fact]
    public async Task UnknownContainerDurationStillRequiresAnAudioDecode()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => Evaluate(new Audio(), new FfprobeData { Format = new FfprobeFormat { Duration = "N/A" }, Streams = new[] { new FfprobeStreamInfo { CodecType = "audio", Duration = "120" } } }));

    [Theory]
    [InlineData("Permission denied")]
    [InlineData("Input/output error")]
    [InlineData("Resource temporarily unavailable")]
    public async Task StorageReadFailureDoesNotFlagMediaAsCorrupt(string message)
        => Assert.Null(await Evaluate(new Video(), new FfprobeData { Error = new FfprobeError { Message = message } }));

    private async Task<Issue?> Evaluate(BaseItem item, FfprobeData probe)
    {
        var db = new MediaDashDb(_path + ".db");
        if (item is Photo)
        {
            var info = new FileInfo(_path);
            db.Probes.StoreDecode(_path, info.Length, info.LastWriteTimeUtc.Ticks, string.Empty);
        }

        var encoder = DispatchProxy.Create<IMediaEncoder, IdleCheckTests.ProxyHandler>();
        var ffprobe = new FfprobeService(encoder, db.Probes, NullLogger<FfprobeService>.Instance);
        var scanner = new PlayabilityScanner(ffprobe, new BookProbeService(db.Probes), new ComicProbeService(db.Probes), NullLogger<PlayabilityScanner>.Instance);
        return await (Task<Issue?>)typeof(PlayabilityScanner).GetMethod("EvaluateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(scanner, new object[] { item, _path, probe, CancellationToken.None })!;
    }

    [Theory]
    [InlineData(".epub")]
    [InlineData(".cbz")]
    public async Task UnsupportedDocumentRepairIsReportedWithoutOfferingApproval(string extension)
    {
        var directory = _path + "-documents";
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "broken" + extension);
            File.WriteAllBytes(path, [1, 2, 3]);
            var db = new MediaDashDb(Path.Combine(directory, "probe.db"));
            var scanner = new PlayabilityScanner(null!, new BookProbeService(db.Probes), new ComicProbeService(db.Probes),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayabilityScanner>.Instance);
            var result = await (Task<Issue?>)typeof(PlayabilityScanner).GetMethod("EvaluateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(scanner, [new Book { Path = path }, path, null, CancellationToken.None])!;
            Assert.NotNull(result);
            using var details = System.Text.Json.JsonDocument.Parse(result.DetailsJson);
            Assert.True(details.RootElement.TryGetProperty("fixerAvailable", out var available));
            Assert.False(available.GetBoolean());
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty("Instance")!.SetValue(null, _previous);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path + ".db");
        File.Delete(_path);
    }
}
